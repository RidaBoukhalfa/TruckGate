using System.Security.Claims;
using TruckGate.Application;
using TruckGate.Application.Cameras;

namespace TruckGate.Api;

public sealed record PriceRequest(decimal Price, string Reason);
public sealed record ReasonRequest(string Reason);
public sealed record MergeRequest(int TargetId, string? Reason);
public sealed record NoteRequest(string Note);
public sealed record AnnotationRequest(string[] Labels, string? Note);
public sealed record AdjustmentRequest(int TruckId, long? TripId, decimal Amount, string Reason);
public sealed record PaymentBatchRequest(DateOnly From, DateOnly To, string? Note, int[]? TruckIds);

internal static partial class Endpoints
{
    /// <summary>Supervisor work: trucks (approval, prices), drivers, operational settings, cameras.</summary>
    public static void MapFleetEndpoints(this WebApplication app)
    {
        var fleet = app.MapGroup("/api/fleet").RequireAuthorization(Policies.Supervise);

        fleet.MapGet("/trucks", async (string? status, string? search, FleetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(string.IsNullOrWhiteSpace(status) ? null : status, search, ct)));
        fleet.MapPost("/trucks", async (TruckCreate r, FleetService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.CreateAsync(r, ctx.UserId(), ctx.Actor(), ct) }));
        fleet.MapGet("/trucks/{id:int}", async (int id, FleetService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(id, ct)));
        fleet.MapGet("/trucks/{id:int}/history", async (int id, FleetService svc, CancellationToken ct) =>
            Results.Ok(await svc.HistoryAsync(id, ct)));
        fleet.MapPut("/trucks/{id:int}", async (int id, TruckEdit r, FleetService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.EditAsync(id, r, ctx.Actor(), ct);
            return Results.NoContent();
        });
        fleet.MapPut("/trucks/{id:int}/plate", async (int id, PlateRequest r, FleetService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetPlateAsync(id, r.Plate, ctx.Actor(), ct);
            return Results.NoContent();
        });
        fleet.MapPut("/trucks/{id:int}/price", async (int id, PriceRequest r, FleetService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetPriceAsync(id, r.Price, r.Reason, ctx.UserId(), ctx.Actor(), ct);
            return Results.NoContent();
        });
        fleet.MapPut("/trucks/{id:int}/active", async (int id, ActiveRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetTruckActiveAsync(id, r.Active, ctx.Actor(), ct);
            return Results.NoContent();
        });
        fleet.MapPost("/trucks/{id:int}/approve", async (int id, TruckApproval r, FleetService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.ApproveAsync(id, r, ctx.UserId(), ctx.Actor(), ct)));
        fleet.MapPost("/trucks/{id:int}/reject", async (int id, ReasonRequest r, FleetService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.RejectAsync(id, r.Reason, ctx.UserId(), ctx.Actor(), ct)));
        fleet.MapPost("/trucks/{id:int}/merge", async (int id, MergeRequest r, FleetService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.MergeAsync(id, r.TargetId, r.Reason, ctx.UserId(), ctx.Actor(), ct)));

        fleet.MapGet("/drivers", async (AdminService svc, CancellationToken ct) => Results.Ok(await svc.ListDriversAsync(ct)));
        fleet.MapPost("/drivers", async (CreateDriverRequest r, AdminService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.CreateDriverAsync(r.FullName, r.Phone, r.EmploymentType ?? "EMPLOYEE", ctx.Actor(), ct) }));

        // Operational settings: supervisors and the administrator; alert channels, sign-in rules and the
        // time zone stay with the administrator (checked again in the service).
        var settings = app.MapGroup("/api/settings").RequireAuthorization(Policies.Supervise);
        settings.MapGet("", async (SettingsService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        settings.MapPut("/{key}", async (string key, SettingRequest r, SettingsService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetAsync(key, r.Value, ctx.User.IsInRole("ADMIN"), ctx.Actor(), ct);
            return Results.NoContent();
        });

        // Cameras are configured by supervisors as well (same addresses as before).
        var cameras = app.MapGroup("/api/admin/cameras").RequireAuthorization(Policies.Supervise);
        cameras.MapGet("", async (CameraService cams, CancellationToken ct) => Results.Ok(await cams.ListAsync(ct)));
        cameras.MapPost("/discover", async (CameraService cams, CancellationToken ct) => Results.Ok(await cams.DiscoverAsync(ct)));
        cameras.MapPost("/probe", async (ProbeRequest r, CameraService cams, CancellationToken ct) =>
            Results.Ok(await cams.ProbeOnvifAsync(r.OnvifUrl, r.Username, r.Password, ct)));
        cameras.MapPost("/test", async (TestSnapshotRequest r, CameraService cams, CancellationToken ct) =>
            Results.File(await cams.TestAsync(r.SnapshotUrl, r.Username, r.Password, ct), "image/jpeg"));
        cameras.MapPost("", async (CameraInput r, CameraService cams, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await cams.CreateAsync(r, ctx.Actor(), ct) }));
        cameras.MapPut("/{id:int}", async (int id, CameraUpdate r, CameraService cams, HttpContext ctx, CancellationToken ct) =>
        {
            await cams.UpdateAsync(id, r, ctx.Actor(), ct);
            return Results.NoContent();
        });
        cameras.MapGet("/{id:int}/test", async (int id, CameraService cams, CancellationToken ct) =>
            Results.File(await cams.TestCameraAsync(id, ct), "image/jpeg"));
    }

    /// <summary>Burned receipts, activity feed, review additions, payments per truck.</summary>
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/receipts/burned", async (DateOnly? from, DateOnly? to, int? truckId, int? driverId, int? operatorId,
                string? lifecycle, string? review, string? search, int? limit, int? offset, ReceiptsService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(new BurnedFilter(from, to, truckId, driverId, operatorId,
                string.IsNullOrWhiteSpace(lifecycle) ? null : lifecycle, string.IsNullOrWhiteSpace(review) ? null : review,
                search, limit ?? 200, offset ?? 0), ct)))
            .RequireAuthorization(Policies.Reports);

        app.MapGet("/api/activity", async (int? hours, int? limit, ReceiptsService svc, CancellationToken ct) =>
            Results.Ok(await svc.ActivityAsync(hours ?? 24, limit ?? 200, ct))).RequireAuthorization(Policies.Alerts);

        var review = app.MapGroup("/api/review").RequireAuthorization(Policies.Review);
        review.MapPost("/trips/{tripId:long}/investigate", async (long tripId, NoteRequest r, ReviewService svc, HttpContext ctx,
            CancellationToken ct) =>
        {
            await svc.InvestigateAsync(tripId, r.Note, ctx.UserId(), ctx.Actor(), ct);
            return Results.NoContent();
        });
        review.MapPost("/trips/{tripId:long}/notes", async (long tripId, NoteRequest r, ReviewService svc, HttpContext ctx,
            CancellationToken ct) =>
        {
            await svc.NoteAsync(tripId, r.Note, ctx.UserId(), ctx.Actor(), ct);
            return Results.NoContent();
        });
        review.MapPost("/trips/{tripId:long}/annotations", async (long tripId, AnnotationRequest r, ReviewService svc, HttpContext ctx,
            CancellationToken ct) =>
        {
            await svc.AnnotateAsync(tripId, r.Labels ?? [], r.Note, ctx.UserId(), ctx.Actor(), ct);
            return Results.NoContent();
        });

        var pay = app.MapGroup("/api/pay");
        pay.MapGet("/adjustments", async (bool? pending, ReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.AdjustmentsAsync(pending ?? false, ct))).RequireAuthorization(Policies.Pay);
        pay.MapPost("/adjustments", async (AdjustmentRequest r, ReportService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.AddAdjustmentAsync(r.TruckId, r.TripId, r.Amount, r.Reason, ctx.UserId(), ctx.Actor(), ct) }))
            .RequireAuthorization(Policies.PayWrite);
        pay.MapPost("/batches/by-truck", async (PaymentBatchRequest r, ReportService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.CreatePaymentBatchAsync(r.From, r.To, r.Note, ctx.UserId(), ctx.Actor(), ct, r.TruckIds) }))
            .RequireAuthorization(Policies.PayWrite);
        pay.MapGet("/batches/{id:long}", async (long id, ReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.BatchStatementAsync(id, ct))).RequireAuthorization(Policies.Pay);
    }
}
