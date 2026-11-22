using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using TruckGate.Application;
using TruckGate.Application.Cameras;
using TruckGate.Data;

namespace TruckGate.Api;

public static class Policies
{
    public const string Gate = "gate";
    public const string Admin = "admin";
    public const string Audit = "audit";
    public const string Evidence = "evidence";
}

internal static class HttpContextUser
{
    public static int UserId(this HttpContext ctx) =>
        int.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("not signed in"));

    public static Actor Actor(this HttpContext ctx) => Data.Actor.User(ctx.UserId());
}

// ------------------------------------------------------------------ request / response bodies

public sealed record LoginRequest(string Username, string Secret);
public sealed record ScanRequest(short GateId, string QrText);
public sealed record ScanView(ScanEntryResult Scan, TicketNames? Names, IReadOnlyList<SnapshotInfo> Snapshots);
public sealed record DecisionRequest(EntryDecision Decision, string? Note);
public sealed record RevokeRequest(string Reason);
public sealed record CreateSiteRequest(string Code, string Name, string Kind);
public sealed record CreateGateRequest(string Code, string Name, int? SiteId);
public sealed record CreateUserRequest(string Username, string FullName, string Role, string? Language, string? Secret);
public sealed record SecretRequest(string Secret);
public sealed record ActiveRequest(bool Active);
public sealed record CreateDriverRequest(string FullName, string? Phone, string? EmploymentType);
public sealed record CreateTruckRequest(int Id, string FleetNumber, string? Model, int? DefaultDriverId, string? Plate);
public sealed record PlateRequest(string Plate);
public sealed record DefaultDriverRequest(int? DriverId);
public sealed record GeofenceRequest(int[][] Points);
public sealed record InviteRequest(string Label);
public sealed record ProbeRequest(string OnvifUrl, string? Username, string? Password);
public sealed record TestSnapshotRequest(string SnapshotUrl, string? Username, string? Password);

internal static class Endpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", async (LoginRequest r, AuthService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var user = await svc.LoginAsync(r.Username ?? "", r.Secret ?? "", ct);
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.FullName),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("username", user.Username),
                new Claim("lang", user.Language),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(user);
        });
        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        auth.MapGet("/me", async (HttpContext ctx, AuthService svc, CancellationToken ct) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var user = await svc.GetUserAsync(ctx.UserId(), ct);
            return user is null ? Results.Unauthorized() : Results.Ok(user);
        });
    }

    public static void MapGateEndpoints(this WebApplication app)
    {
        var gate = app.MapGroup("/api/gate").RequireAuthorization(Policies.Gate);

        gate.MapGet("/gates", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListGatesAsync(ct)));

        // The ticket is "burned" (checked and used) and the cameras of this gate take their pictures
        // at the same time. Pictures are kept for every scan, including rejected ones.
        gate.MapPost("/scan", async (ScanRequest r, GateService svc, CameraService cams, HttpContext ctx, CancellationToken ct) =>
        {
            var capture = cams.CaptureAsync(r.GateId, CancellationToken.None);
            ScanEntryResult result;
            try
            {
                result = await svc.ScanEntryAsync(new ScanEntryRequest(r.GateId, r.QrText ?? "", ctx.UserId()), ctx.Actor(), ct);
            }
            catch
            {
                try { await capture; } catch { /* the scan error is the one to report */ }
                throw;
            }
            var snapshots = await cams.StoreAsync(result.ScanId, await capture, CancellationToken.None);
            var names = result.Ticket is null ? null : await svc.NamesAsync(result.Ticket, ct);
            return Results.Ok(new ScanView(result, names, snapshots));
        });

        gate.MapPost("/scans/{scanId:long}/decision", async (long scanId, DecisionRequest r, GateService svc,
                HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.RecordEntryDecisionAsync(scanId, r.Decision, r.Note, ctx.UserId(), ctx.Actor(), ct)));

        gate.MapGet("/scans/{scanId:long}/snapshots", async (long scanId, CameraService cams, CancellationToken ct) =>
            Results.Ok(await cams.ListForScanAsync(scanId, ct)));

        gate.MapGet("/cameras/{id:int}/live", async (int id, CameraService cams, CancellationToken ct) =>
            Results.File(await cams.TestCameraAsync(id, ct), "image/jpeg"));

        app.MapGet("/api/snapshots/{id:long}", async (long id, CameraService cams, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(await cams.ReadSnapshotAsync(id, ct), "image/jpeg");
        }).RequireAuthorization(Policies.Evidence);
    }

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(Policies.Admin);

        // devices and enrollment
        admin.MapGet("/devices", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListDevicesAsync(ct)));
        admin.MapPost("/devices/{id:int}/revoke", async (int id, RevokeRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.RevokeDeviceAsync(id, r.Reason, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapPost("/enrollments", async (InviteRequest r, EnrollmentService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.CreateInviteAsync(r.Label, ctx.Actor(), ct)));

        // sites and gates
        admin.MapGet("/sites", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListSitesAsync(ct)));
        admin.MapPost("/sites", async (CreateSiteRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.CreateSiteAsync(r.Code, r.Name, r.Kind, ctx.Actor(), ct) }));
        admin.MapPut("/sites/{id:int}/geofence", async (int id, GeofenceRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetSiteGeofenceAsync(id, r.Points, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapGet("/gates", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListGatesAsync(ct)));
        admin.MapPost("/gates", async (CreateGateRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.CreateGateAsync(r.Code, r.Name, r.SiteId, ctx.Actor(), ct) }));

        // users
        admin.MapGet("/users", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListUsersAsync(ct)));
        admin.MapPost("/users", async (CreateUserRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var id = await svc.CreateUserAsync(r.Username, r.FullName, r.Role, r.Language ?? "ar", ctx.Actor(), ct);
            if (!string.IsNullOrEmpty(r.Secret)) await svc.SetUserPinAsync(id, r.Secret, ctx.Actor(), ct);
            return Results.Ok(new { id });
        });
        admin.MapPut("/users/{id:int}/secret", async (int id, SecretRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetUserPinAsync(id, r.Secret, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapPut("/users/{id:int}/active", async (int id, ActiveRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            if (id == ctx.UserId() && !r.Active)
                throw TruckGateException.Validation("SELF_DISABLE", "You cannot disable your own account.");
            await svc.SetUserActiveAsync(id, r.Active, ctx.Actor(), ct);
            return Results.NoContent();
        });

        // drivers and trucks
        admin.MapGet("/drivers", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListDriversAsync(ct)));
        admin.MapPost("/drivers", async (CreateDriverRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.CreateDriverAsync(r.FullName, r.Phone, r.EmploymentType ?? "EMPLOYEE", ctx.Actor(), ct) }));
        admin.MapGet("/trucks", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListTrucksAsync(ct)));
        admin.MapPost("/trucks", async (CreateTruckRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.CreateTruckAsync(r.Id, r.FleetNumber, r.Model, r.DefaultDriverId, r.Plate, ctx.Actor(), ct);
            return Results.Ok(new { r.Id });
        });
        admin.MapPut("/trucks/{id:int}/plate", async (int id, PlateRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetTruckPlateAsync(id, r.Plate, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapPut("/trucks/{id:int}/active", async (int id, ActiveRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetTruckActiveAsync(id, r.Active, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapPut("/trucks/{id:int}/default-driver", async (int id, DefaultDriverRequest r, AdminService svc, HttpContext ctx,
            CancellationToken ct) =>
        {
            await svc.SetDefaultDriverAsync(id, r.DriverId, ctx.Actor(), ct);
            return Results.NoContent();
        });

        // cameras: discover, probe, preview, choose
        admin.MapGet("/cameras", async (CameraService cams, CancellationToken ct) => Results.Ok(await cams.ListAsync(ct)));
        admin.MapPost("/cameras/discover", async (CameraService cams, CancellationToken ct) =>
            Results.Ok(await cams.DiscoverAsync(ct)));
        admin.MapPost("/cameras/probe", async (ProbeRequest r, CameraService cams, CancellationToken ct) =>
            Results.Ok(await cams.ProbeOnvifAsync(r.OnvifUrl, r.Username, r.Password, ct)));
        admin.MapPost("/cameras/test", async (TestSnapshotRequest r, CameraService cams, CancellationToken ct) =>
            Results.File(await cams.TestAsync(r.SnapshotUrl, r.Username, r.Password, ct), "image/jpeg"));
        admin.MapPost("/cameras", async (CameraInput r, CameraService cams, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await cams.CreateAsync(r, ctx.Actor(), ct) }));
        admin.MapPut("/cameras/{id:int}", async (int id, CameraUpdate r, CameraService cams, HttpContext ctx, CancellationToken ct) =>
        {
            await cams.UpdateAsync(id, r, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapGet("/cameras/{id:int}/test", async (int id, CameraService cams, CancellationToken ct) =>
            Results.File(await cams.TestCameraAsync(id, ct), "image/jpeg"));

        // integrity
        var audit = app.MapGroup("/api/audit").RequireAuthorization(Policies.Audit);
        audit.MapGet("/verify", async (AuditService svc, CancellationToken ct) => Results.Ok(await svc.VerifyAsync(ct)));
        audit.MapPost("/anchor", async (AuditService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.AnchorAsync(ctx.Actor(), ct)));
        audit.MapGet("/sequence-gaps", async (AuditService svc, CancellationToken ct) => Results.Ok(await svc.SequenceGapsAsync(ct)));
    }

    /// <summary>Quarry phones: no user session, their own signatures.</summary>
    public static void MapPhoneEndpoints(this WebApplication app)
    {
        app.MapPost("/api/enroll", async (EnrollRequest req, EnrollmentService svc, CancellationToken ct) =>
            Results.Ok(await svc.EnrollAsync(req, ct)));
        app.MapGet("/api/devices/{id:int}/config", async (int id, HttpContext ctx, EnrollmentService svc, CancellationToken ct) =>
        {
            var h = ctx.Request.Headers;
            if (!int.TryParse(h["X-TG-Device"].FirstOrDefault(), out var device) ||
                !long.TryParse(h["X-TG-Time"].FirstOrDefault(), out var time) ||
                h["X-TG-Signature"].FirstOrDefault() is not { Length: > 0 } signature)
                throw TruckGateException.Unauthorized("AUTH_MISSING", "Device signature headers are missing.");
            if (device != id) throw TruckGateException.Unauthorized("DEVICE_MISMATCH", "Signed device does not match the URL.");
            await svc.AuthenticateAsync(new DeviceAuthHeaders(device, time, signature), "GET", ctx.Request.Path.Value ?? "", [], ct);
            return Results.Ok(await svc.GetSignedConfigAsync(id, ct));
        });
    }
}

/// <summary>`dotnet TruckGate.Api.dll create-admin` — creates the first administrator on the server console.</summary>
internal static class Setup
{
    public static async Task CreateFirstAdminAsync(AuthService auth)
    {
        Console.Write("Username: ");
        var username = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Full name: ");
        var fullName = Console.ReadLine()?.Trim() ?? "";
        var password = ReadHidden("Password (10+ characters): ");
        if (password != ReadHidden("Repeat password: "))
        {
            Console.Error.WriteLine("Passwords differ.");
            Environment.ExitCode = 1;
            return;
        }
        try
        {
            var id = await auth.CreateFirstAdminAsync(username, fullName, password);
            Console.WriteLine($"Administrator created (id {id}).");
        }
        catch (TruckGateException e)
        {
            Console.Error.WriteLine(e.Message);
            Environment.ExitCode = 1;
        }
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
