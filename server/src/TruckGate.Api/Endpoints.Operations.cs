using System.Text.Json;
using TruckGate.Application;
using TruckGate.Application.Alerts;
using TruckGate.Application.Cameras;

namespace TruckGate.Api;

public sealed record ManualExitRequest(short GateId, long TripId);
public sealed record ExitDecisionRequest(ExitDecision Decision, string? Note);
public sealed record ReviewDecisionRequest(ReviewDecision Decision, string? Reason);
public sealed record LabelRequest(string Type, string Value);
public sealed record SettingRequest(JsonElement Value);
public sealed record TokenRequest(string Token);
public sealed record ChannelTestRequest(string Channel);
public sealed record PaymentRequest(DateOnly From, DateOnly To, string? Note);

internal static partial class Endpoints
{
    public static void MapExitEndpoints(this WebApplication app)
    {
        var gate = app.MapGroup("/api/gate").RequireAuthorization(Policies.Gate);

        gate.MapGet("/inside", async (ExitService svc, CancellationToken ct) => Results.Ok(await svc.InsideAsync(ct)));

        // Receipt lost: the gatekeeper picks the truck among the trucks inside (recorded as such).
        gate.MapPost("/exit/manual", async (ManualExitRequest r, ExitService svc, CameraService cams, HttpContext ctx,
            CancellationToken ct) =>
        {
            var (exit, pictures) = await WithPicturesAsync(cams, r.GateId,
                () => svc.ScanManualAsync(r.GateId, r.TripId, ctx.UserId(), ctx.Actor(), ct), x => x.ScanId);
            return Results.Ok(new ExitView("EXIT", exit, pictures));
        });

        gate.MapPost("/exit/scans/{scanId:long}/decision", async (long scanId, ExitDecisionRequest r, ExitService svc,
                HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.RecordExitDecisionAsync(scanId, r.Decision, r.Note, ctx.UserId(), ctx.Actor(), ct)));

        gate.MapPost("/trips/{tripId:long}/receipt", async (long tripId, ExitService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.PrintReceiptAsync(tripId, ctx.UserId(), ctx.Actor(), ct)));
    }

    public static void MapReviewEndpoints(this WebApplication app)
    {
        var review = app.MapGroup("/api/review").RequireAuthorization(Policies.Review);
        review.MapGet("/queue", async (bool? samples, ReviewService svc, CancellationToken ct) =>
            Results.Ok(await svc.QueueAsync(samples ?? true, ct)));
        review.MapPost("/trips/{tripId:long}/decision", async (long tripId, ReviewDecisionRequest r, ReviewService svc,
                HttpContext ctx, CancellationToken ct) =>
            Results.Ok(await svc.DecideAsync(tripId, r.Decision, r.Reason, ctx.UserId(), ctx.Actor(), ct)));
        review.MapPost("/snapshots/{id:long}/labels", async (long id, LabelRequest r, ReviewService svc, HttpContext ctx,
            CancellationToken ct) =>
        {
            await svc.LabelAsync(id, r.Type, r.Value, ctx.UserId(), ctx.Actor(), ct);
            return Results.NoContent();
        });
        review.MapGet("/dataset", async (DateOnly from, DateOnly to, ReviewService svc, SettingsService settings,
            HttpContext ctx, CancellationToken ct) =>
        {
            var tz = (await settings.ListAsync(ct)).FirstOrDefault(s => s.Key == "site.timezone")?.Value.Trim('"') ?? "Africa/Algiers";
            var path = await svc.ExportDatasetAsync(from, to, tz, ct);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
            return Results.File(stream, "application/zip", $"truckgate-dataset-{from:yyyyMMdd}-{to:yyyyMMdd}.zip");
        });

        // Trip detail and pictures are also open to the manager and the auditor.
        app.MapGet("/api/trips/{tripId:long}", async (long tripId, ReviewService svc, CancellationToken ct) =>
            Results.Ok(await svc.TripAsync(tripId, ct))).RequireAuthorization(Policies.Evidence);
        app.MapGet("/api/tickets/{deviceId:int}/{seq:long}/photo", async (int deviceId, long seq, SyncService svc,
            HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(await svc.ReadPhotoAsync(deviceId, seq, ct), "image/jpeg");
        }).RequireAuthorization(Policies.Evidence);
    }

    public static void MapAlertEndpoints(this WebApplication app)
    {
        var alerts = app.MapGroup("/api/alerts").RequireAuthorization(Policies.Alerts);
        alerts.MapGet("", async (bool? open, int? limit, AlertService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(open ?? false, limit ?? 200, ct)));
        alerts.MapPost("/{id:long}/ack", async (long id, AlertService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.AcknowledgeAsync(id, ctx.UserId(), ctx.Actor(), ct);
            return Results.NoContent();
        });

        var admin = app.MapGroup("/api/admin").RequireAuthorization(Policies.Admin);
        admin.MapGet("/settings", async (SettingsService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        admin.MapPut("/settings/{key}", async (string key, SettingRequest r, SettingsService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetAsync(key, r.Value, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapGet("/channels", async (AlertService svc, CancellationToken ct) => Results.Ok(await svc.ChannelsAsync(ct)));
        admin.MapPut("/channels/telegram-token", async (TokenRequest r, AlertService svc, HttpContext ctx, CancellationToken ct) =>
        {
            await svc.SetTelegramTokenAsync(r.Token, ctx.Actor(), ct);
            return Results.NoContent();
        });
        admin.MapPost("/channels/test", async (ChannelTestRequest r, AlertService svc, CancellationToken ct) =>
            Results.Ok(new { errors = await svc.TestAsync((r.Channel ?? "").ToUpperInvariant(), ct) }));
    }

    public static void MapReportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/reports/dashboard", async (ReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.DashboardAsync(ct))).RequireAuthorization(Policies.Reports);

        var pay = app.MapGroup("/api/pay").RequireAuthorization(Policies.Pay);
        pay.MapGet("/statement", async (DateOnly from, DateOnly to, ReportService svc, CancellationToken ct) =>
            Results.Ok(await svc.StatementAsync(from, to, ct)));
        pay.MapGet("/statement.csv", async (DateOnly from, DateOnly to, string? lang, ReportService svc, CancellationToken ct) =>
            Results.File(ReportService.StatementCsv(await svc.StatementAsync(from, to, ct), lang ?? "ar"), "text/csv",
                $"statement-{from:yyyyMMdd}-{to:yyyyMMdd}.csv"));
        pay.MapGet("/batches", async (ReportService svc, CancellationToken ct) => Results.Ok(await svc.BatchesAsync(ct)));
        pay.MapGet("/batches/{id:long}.csv", async (long id, string? lang, ReportService svc, CancellationToken ct) =>
            Results.File(ReportService.StatementCsv(await svc.BatchStatementAsync(id, ct), lang ?? "ar"), "text/csv",
                $"payment-{id}.csv"));
        app.MapPost("/api/pay/batches", async (PaymentRequest r, ReportService svc, HttpContext ctx, CancellationToken ct) =>
            Results.Ok(new { id = await svc.CreatePaymentBatchAsync(r.From, r.To, r.Note, ctx.UserId(), ctx.Actor(), ct) }))
            .RequireAuthorization(Policies.PayWrite);
    }
}
