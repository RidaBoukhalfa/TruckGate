using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using Npgsql;
using TruckGate.Api;
using TruckGate.Application;
using TruckGate.Application.Alerts;
using TruckGate.Application.Cameras;
using TruckGate.Data;

// Under the Windows service manager the working directory is System32, so the program folder
// (appsettings.json, wwwroot) is taken from the executable's location instead.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// Site configuration (database passwords, the address phones use, data folder) lives outside the
// program folder so that an upgrade never overwrites it: TRUCKGATE_CONFIG, or on Windows
// %ProgramData%\TruckGate\config\appsettings.json (written by the installer). Environment variables
// and the command line still win over it.
var siteConfig = Environment.GetEnvironmentVariable("TRUCKGATE_CONFIG");
if (string.IsNullOrEmpty(siteConfig) && OperatingSystem.IsWindows())
    siteConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TruckGate", "config", "appsettings.json");
if (!string.IsNullOrEmpty(siteConfig))
{
    builder.Configuration.AddJsonFile(siteConfig, optional: true, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

if (OperatingSystem.IsWindows())
{
    builder.Services.Configure<EventLogSettings>(s => s.SourceName = "TruckGate");
    builder.Services.AddWindowsService(o => o.ServiceName = "TruckGate");
}
if (builder.Configuration["Logging:File:Path"] is { Length: > 0 } logDirectory)
    builder.Logging.AddProvider(new FileLoggerProvider(logDirectory));

var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
              ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

var appConnection = builder.Configuration.GetConnectionString("App")
                    ?? throw new InvalidOperationException("ConnectionStrings:App is required.");
var ownerConnection = builder.Configuration.GetConnectionString("Owner");
var dataDir = builder.Configuration["Data:Directory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(appConnection));
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<GateService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton(_ => ServerSigningKey.LoadOrCreate(
    builder.Configuration["Signing:KeyPath"] ?? Path.Combine(dataDir, "server-signing-key.pem")));
builder.Services.AddSingleton(_ => SecretBox.LoadOrCreate(
    builder.Configuration["Secrets:KeyPath"] ?? Path.Combine(dataDir, "secrets.key")));
builder.Services.AddSingleton(_ => new StorageOptions(
    builder.Configuration["Storage:Root"] ?? Path.Combine(dataDir, "snapshots")));
builder.Services.AddSingleton(sp => new CameraService(sp.GetRequiredService<Db>(), sp.GetRequiredService<SecretBox>(),
    sp.GetRequiredService<StorageOptions>()));
// Event video: ffmpeg/ffprobe come with the Windows installer ({app}\ffmpeg, next to {app}\server); a path in
// Video:FfmpegPath / Video:FfprobePath wins; otherwise "ffmpeg" / "ffprobe" from PATH (Linux, development).
static string VideoTool(string? configured, string name)
{
    if (configured is { Length: > 0 }) return configured;
    var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ffmpeg",
        OperatingSystem.IsWindows() ? name + ".exe" : name));
    return File.Exists(bundled) ? bundled : name;
}
builder.Services.AddSingleton(sp => new VideoService(sp.GetRequiredService<Db>(), new VideoOptions(
    VideoTool(builder.Configuration["Video:FfmpegPath"], "ffmpeg"),
    VideoTool(builder.Configuration["Video:FfprobePath"], "ffprobe"),
    builder.Configuration["Video:BufferDir"] ?? Path.Combine(dataDir, "video-buffer"),
    sp.GetRequiredService<StorageOptions>().Root)));
builder.Services.AddSingleton(_ => EnrollmentOptions.Default(
    builder.Configuration["Server:PublicUrl"] ?? throw new InvalidOperationException("Server:PublicUrl is required (the address phones use).")));
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<ExitService>();
builder.Services.AddSingleton<ReviewService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<FleetService>();
builder.Services.AddSingleton<ReceiptsService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<Watchdog>();
builder.Services.AddHttpClient("telegram", http => http.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<ITelegramSender>(sp => new TelegramSender(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram"),
    builder.Configuration["Telegram:BaseUrl"] ?? "https://api.telegram.org"));
builder.Services.AddHttpClient("whatsapp", http => http.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IWhatsAppSender>(sp => new WhatsAppSender(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("whatsapp"),
    builder.Configuration["WhatsApp:BaseUrl"] ?? "https://graph.facebook.com"));
builder.Services.AddSingleton<SerialSmsSender>();
builder.Services.AddSingleton(_ => new ZteRouterSmsSender());
builder.Services.AddSingleton<ISmsSender, SmsSender>();
builder.Services.AddSingleton<AlertService>();
if (builder.Configuration["Background:Disabled"] != "true")
{
    builder.Services.AddHostedService<WatchdogHost>();
    if (builder.Configuration["Video:Disabled"] != "true") builder.Services.AddHostedService<VideoRecorderHost>();
    builder.Services.AddHostedService<AlertDispatchHost>();
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
});

// ------------------------------------------------------------------ login (cookie) and roles

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "dp-keys")))
    .SetApplicationName("TruckGate");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "tg_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        // An API answers 401/403, it does not redirect.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        // A disabled or deleted user loses access at once, not when the cookie expires.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var id = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var auth = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
            var user = int.TryParse(id, out var uid) ? await auth.GetUserAsync(uid) : null;
            if (user is null || user.Role != ctx.Principal?.FindFirstValue(ClaimTypes.Role))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Gate, p => p.RequireRole("GATEKEEPER", "SUPERVISOR", "ADMIN"))
    .AddPolicy(Policies.Admin, p => p.RequireRole("ADMIN"))
    .AddPolicy(Policies.Audit, p => p.RequireRole("ADMIN", "MANAGER", "AUDITOR"))
    .AddPolicy(Policies.Evidence, p => p.RequireRole("GATEKEEPER", "SUPERVISOR", "ADMIN", "MANAGER", "AUDITOR"))
    .AddPolicy(Policies.Review, p => p.RequireRole("SUPERVISOR", "ADMIN"))
    .AddPolicy(Policies.Alerts, p => p.RequireRole("SUPERVISOR", "ADMIN", "MANAGER"))
    .AddPolicy(Policies.Reports, p => p.RequireRole("SUPERVISOR", "ADMIN", "MANAGER", "ACCOUNTANT", "AUDITOR"))
    .AddPolicy(Policies.Pay, p => p.RequireRole("ACCOUNTANT", "ADMIN", "MANAGER"))
    .AddPolicy(Policies.PayWrite, p => p.RequireRole("ACCOUNTANT", "ADMIN"))
    .AddPolicy(Policies.Supervise, p => p.RequireRole("SUPERVISOR", "ADMIN"));

var app = builder.Build();

// ------------------------------------------------------------------ migrations, then command line

if (!string.IsNullOrEmpty(ownerConnection))
{
    await using var owner = NpgsqlDataSource.Create(ownerConnection);
    var applied = await DbMigrator.MigrateAsync(owner);
    foreach (var name in applied) app.Logger.LogInformation("Applied migration {Migration}", name);
    if (args.Length > 0 && args[0] == "migrate")
    {
        Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} migration(s).");
        return;
    }
}

// `TruckGate.Api migrate` — used by the installer and for upgrades. The owner connection is given only
// for this run (environment variable ConnectionStrings__Owner) and is never stored for the service.
if (args.Length > 0 && args[0] == "migrate")
{
    Console.Error.WriteLine("migrate needs ConnectionStrings:Owner (e.g. environment variable ConnectionStrings__Owner).");
    Environment.ExitCode = 2;
    return;
}

if (args.Length > 0 && args[0] == "create-admin")
{
    await Setup.CreateFirstAdminAsync(app.Services.GetRequiredService<AuthService>());
    return;
}

// ------------------------------------------------------------------ pipeline

app.Use(async (ctx, next) =>
{
    try
    {
        await next(ctx);
    }
    catch (TruckGateException e)
    {
        ctx.Response.StatusCode = e.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };
        await ctx.Response.WriteAsJsonAsync(new { error = e.Code, message = e.Message });
    }
    catch (PostgresException e) when (e.MessageText.StartsWith("TG_", StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { error = e.MessageText.Split(':')[0], message = e.MessageText });
    }
});

// Cross-site request forgery: a state-changing call from the web app must carry this header,
// which another site cannot add. Phone endpoints use their own signatures and no cookie.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var phoneCall = path == "/api/enroll" || path.StartsWith("/api/devices/", StringComparison.Ordinal);
    if (path.StartsWith("/api/", StringComparison.Ordinal) && !phoneCall && !HttpMethods.IsGet(ctx.Request.Method) &&
        ctx.Request.Headers["X-TG-CSRF"] != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "CSRF", message = "Missing X-TG-CSRF header." });
        return;
    }
    await next(ctx);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Used by the installer, the gate-station launcher and the "change server address" tool to tell
// "server not reachable" apart from "server running but database down".
app.MapGet("/health", async (Db db, CancellationToken ct) =>
{
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await db.ReadAsync(c => Dapper.SqlMapper.ExecuteScalarAsync<int>(c, new Dapper.CommandDefinition("SELECT 1",
            cancellationToken: timeout.Token)), timeout.Token);
        return Results.Ok(new { status = "ok", service = "truckgate", version, database = "ok" });
    }
    catch (Exception e) when (e is NpgsqlException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
    {
        return Results.Json(new { status = "degraded", service = "truckgate", version, database = "unreachable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapAuthEndpoints();
app.MapGateEndpoints();
app.MapAdminEndpoints();
app.MapPhoneEndpoints();
app.MapExitEndpoints();
app.MapReviewEndpoints();
app.MapAlertEndpoints();
app.MapReportEndpoints();
app.MapFleetEndpoints();
app.MapWorkflowEndpoints();
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program
{
}
