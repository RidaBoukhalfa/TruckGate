using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using TruckGate.Api;
using TruckGate.Application;
using TruckGate.Application.Alerts;
using TruckGate.Application.Cameras;
using TruckGate.Data;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton(_ => EnrollmentOptions.Default(
    builder.Configuration["Server:PublicUrl"] ?? throw new InvalidOperationException("Server:PublicUrl is required (the address phones use).")));
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<ExitService>();
builder.Services.AddSingleton<ReviewService>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<Watchdog>();
builder.Services.AddHttpClient("telegram", http => http.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<ITelegramSender>(sp => new TelegramSender(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("telegram"),
    builder.Configuration["Telegram:BaseUrl"] ?? "https://api.telegram.org"));
builder.Services.AddSingleton<ISmsSender, SerialSmsSender>();
builder.Services.AddSingleton<AlertService>();
if (builder.Configuration["Background:Disabled"] != "true")
{
    builder.Services.AddHostedService<WatchdogHost>();
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
    .AddPolicy(Policies.PayWrite, p => p.RequireRole("ACCOUNTANT", "ADMIN"));

var app = builder.Build();

// ------------------------------------------------------------------ migrations, then command line

if (!string.IsNullOrEmpty(ownerConnection))
{
    await using var owner = NpgsqlDataSource.Create(ownerConnection);
    foreach (var name in await DbMigrator.MigrateAsync(owner)) app.Logger.LogInformation("Applied migration {Migration}", name);
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapGateEndpoints();
app.MapAdminEndpoints();
app.MapPhoneEndpoints();
app.MapExitEndpoints();
app.MapReviewEndpoints();
app.MapAlertEndpoints();
app.MapReportEndpoints();
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program
{
}
