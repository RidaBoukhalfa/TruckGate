using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using TruckGate.Application;
using TruckGate.Data;

var builder = WebApplication.CreateBuilder(args);

// Step 2 has no login yet (it arrives with the gate station in step 4). Until then the API
// refuses to start outside Development, and Development binds to localhost only.
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException(
        "TruckGate API: authentication is not implemented yet (step 4). Run with ASPNETCORE_ENVIRONMENT=Development on localhost only.");

var appConnection = builder.Configuration.GetConnectionString("App")
                    ?? throw new InvalidOperationException("ConnectionStrings:App is required.");
var ownerConnection = builder.Configuration.GetConnectionString("Owner");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(appConnection));
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<GateService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton(_ => ServerSigningKey.LoadOrCreate(
    builder.Configuration["Signing:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "server-signing-key.pem")));
builder.Services.AddSingleton(_ => EnrollmentOptions.Default(
    builder.Configuration["Server:PublicUrl"] ?? throw new InvalidOperationException("Server:PublicUrl is required (the address phones use).")));
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
});

var app = builder.Build();

if (!string.IsNullOrEmpty(ownerConnection))
{
    await using var owner = NpgsqlDataSource.Create(ownerConnection);
    var applied = await DbMigrator.MigrateAsync(owner);
    foreach (var name in applied) app.Logger.LogInformation("Applied migration {Migration}", name);
}

// ------------------------------------------------------------------ errors

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
        // A database guard refused the change (append-only, immutable column, illegal state transition).
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { error = e.MessageText.Split(':')[0], message = e.MessageText });
    }
});

static Actor ActorOf(HttpContext ctx) =>
    Actor.Development(ctx.Request.Headers["X-Dev-Actor"].FirstOrDefault() is { Length: > 0 and <= 64 } name ? name : "api");

// ------------------------------------------------------------------ endpoints

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var gate = app.MapGroup("/api/gate");
gate.MapPost("/scan", async (ScanEntryRequest req, GateService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(await svc.ScanEntryAsync(req, ActorOf(ctx), ct)));
gate.MapPost("/scans/{scanId:long}/decision", async (long scanId, DecisionRequest req, GateService svc,
        HttpContext ctx, CancellationToken ct) =>
    Results.Ok(await svc.RecordEntryDecisionAsync(scanId, req.Decision, req.Note, req.DecidedBy, ActorOf(ctx), ct)));

var admin = app.MapGroup("/api/admin");
admin.MapGet("/devices", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListDevicesAsync(ct)));
admin.MapPost("/devices", async (EnrollDeviceRequest req, AdminService svc, HttpContext ctx, CancellationToken ct) =>
{
    byte[] key;
    try { key = Convert.FromBase64String(req.PublicKeySpkiBase64); }
    catch (FormatException) { throw TruckGateException.Validation("PUBLIC_KEY_INVALID", "Public key is not base64."); }
    await svc.EnrollDeviceAsync(req.Id, req.Kind, req.Label, key, ActorOf(ctx), ct);
    return Results.Created($"/api/admin/devices/{req.Id}", new { req.Id });
});
admin.MapPost("/devices/{id:int}/revoke", async (int id, RevokeRequest req, AdminService svc, HttpContext ctx,
        CancellationToken ct) =>
{
    await svc.RevokeDeviceAsync(id, req.Reason, ActorOf(ctx), ct);
    return Results.NoContent();
});
admin.MapPost("/sites", async (CreateSiteRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(new { id = await svc.CreateSiteAsync(r.Code, r.Name, r.Kind, ActorOf(ctx), ct) }));
admin.MapPost("/gates", async (CreateGateRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(new { id = await svc.CreateGateAsync(r.Code, r.Name, r.SiteId, ActorOf(ctx), ct) }));
admin.MapPost("/users", async (CreateUserRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(new { id = await svc.CreateUserAsync(r.Username, r.FullName, r.Role, r.Language ?? "ar", ActorOf(ctx), ct) }));
admin.MapPost("/drivers", async (CreateDriverRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(new { id = await svc.CreateDriverAsync(r.FullName, r.Phone, r.EmploymentType ?? "EMPLOYEE", ActorOf(ctx), ct) }));
admin.MapPost("/trucks", async (CreateTruckRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
{
    await svc.CreateTruckAsync(r.Id, r.FleetNumber, r.Model, r.DefaultDriverId, r.Plate, ActorOf(ctx), ct);
    return Results.Created($"/api/admin/trucks/{r.Id}", new { r.Id });
});
admin.MapPut("/trucks/{id:int}/plate", async (int id, PlateRequest r, AdminService svc, HttpContext ctx,
        CancellationToken ct) =>
{
    await svc.SetTruckPlateAsync(id, r.Plate, ActorOf(ctx), ct);
    return Results.NoContent();
});
admin.MapPut("/trucks/{id:int}/active", async (int id, ActiveRequest r, AdminService svc, HttpContext ctx,
        CancellationToken ct) =>
{
    await svc.SetTruckActiveAsync(id, r.Active, ActorOf(ctx), ct);
    return Results.NoContent();
});

admin.MapPut("/sites/{id:int}/geofence", async (int id, GeofenceRequest r, AdminService svc, HttpContext ctx,
        CancellationToken ct) =>
{
    await svc.SetSiteGeofenceAsync(id, r.Points, ActorOf(ctx), ct);
    return Results.NoContent();
});
admin.MapPut("/users/{id:int}/pin", async (int id, PinRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
{
    await svc.SetUserPinAsync(id, r.Pin, ActorOf(ctx), ct);
    return Results.NoContent();
});
admin.MapPost("/enrollments", async (InviteRequest r, EnrollmentService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(await svc.CreateInviteAsync(r.Label, ActorOf(ctx), ct)));

// ------------------------------------------------------------------ phone endpoints (no user session)

app.MapPost("/api/enroll", async (EnrollRequest req, EnrollmentService svc, CancellationToken ct) =>
    Results.Ok(await svc.EnrollAsync(req, ct)));
app.MapGet("/api/devices/{id:int}/config", async (int id, HttpContext ctx, EnrollmentService svc, CancellationToken ct) =>
{
    var headers = ReadDeviceAuth(ctx) ?? throw TruckGateException.Unauthorized("AUTH_MISSING", "Device signature headers are missing.");
    if (headers.DeviceId != id) throw TruckGateException.Unauthorized("DEVICE_MISMATCH", "Signed device does not match the URL.");
    await svc.AuthenticateAsync(headers, "GET", ctx.Request.Path.Value ?? "", [], ct);
    return Results.Ok(await svc.GetSignedConfigAsync(id, ct));
});

static DeviceAuthHeaders? ReadDeviceAuth(HttpContext ctx)
{
    var h = ctx.Request.Headers;
    if (!int.TryParse(h["X-TG-Device"].FirstOrDefault(), out var device)) return null;
    if (!long.TryParse(h["X-TG-Time"].FirstOrDefault(), out var time)) return null;
    var signature = h["X-TG-Signature"].FirstOrDefault();
    return string.IsNullOrEmpty(signature) ? null : new DeviceAuthHeaders(device, time, signature);
}

var audit = app.MapGroup("/api/audit");
audit.MapGet("/verify", async (AuditService svc, CancellationToken ct) => Results.Ok(await svc.VerifyAsync(ct)));
audit.MapPost("/anchor", async (AuditService svc, HttpContext ctx, CancellationToken ct) =>
    Results.Ok(await svc.AnchorAsync(ActorOf(ctx), ct)));
audit.MapGet("/sequence-gaps", async (AuditService svc, CancellationToken ct) =>
    Results.Ok(await svc.SequenceGapsAsync(ct)));

app.Run();

// ------------------------------------------------------------------ request bodies

internal sealed record DecisionRequest(EntryDecision Decision, string? Note, int? DecidedBy);
internal sealed record EnrollDeviceRequest(int Id, string Kind, string Label, string PublicKeySpkiBase64);
internal sealed record RevokeRequest(string Reason);
internal sealed record CreateSiteRequest(string Code, string Name, string Kind);
internal sealed record CreateGateRequest(string Code, string Name, int? SiteId);
internal sealed record CreateUserRequest(string Username, string FullName, string Role, string? Language);
internal sealed record CreateDriverRequest(string FullName, string? Phone, string? EmploymentType);
internal sealed record CreateTruckRequest(int Id, string FleetNumber, string? Model, int? DefaultDriverId, string? Plate);
internal sealed record PlateRequest(string Plate);
internal sealed record ActiveRequest(bool Active);
internal sealed record GeofenceRequest(int[][] Points);
internal sealed record PinRequest(string Pin);
internal sealed record InviteRequest(string Label);
