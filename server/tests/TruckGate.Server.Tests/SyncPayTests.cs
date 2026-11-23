using System.Security.Cryptography;
using Dapper;
using TruckGate.Application;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

/// <summary>Phone sync (tickets, voids, photos), manual short-code entry, accountant statements and payment.</summary>
[Collection("db")]
public class SyncPayTests(DatabaseFixture fx)
{
    private static readonly Actor T = DatabaseFixture.Tester;

    private sealed record Env(Db Db, GateService Gate, SyncService Sync, ReportService Reports, ExitService Exit,
        AdminService Admin, short GateId, int TruckId, int DriverId, QuarryPhone Phone, string Storage)
    {
        public Task<long> NowAsync() => Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint"));

        public Task<TV> Scalar<TV>(string sql, object? args = null) => Db.ReadAsync(c => c.ExecuteScalarAsync<TV>(sql, args))!;

        public Task SetAsync(string key, string json) =>
            Db.InTransactionAsync(T, (c, tx) => c.ExecuteAsync("UPDATE tg.settings SET value = CAST(@json AS jsonb) WHERE key = @key",
                new { key, json }, tx));
    }

    private async Task<Env> NewEnvAsync()
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var storage = Path.Combine(Path.GetTempPath(), "tg-sync-" + Guid.NewGuid().ToString("N")[..8]);
        var admin = new AdminService(db);
        var gateId = (short)await admin.CreateGateAsync("G1", "Main gate", null, T);
        var driver = await admin.CreateDriverAsync("سائق 1", null, "CONTRACTOR", T);
        await admin.CreateTruckAsync(5, "05", "Foton", driver, "01234 216 16", T);
        var phone = new QuarryPhone(3);
        await admin.EnrollDeviceAsync(phone.DeviceId, "QUARRY_PHONE", "Phone", phone.PublicKey, T);
        return new Env(db, new GateService(db), new SyncService(db, new StorageOptions(storage)), new ReportService(db),
            new ExitService(db, ServerSigningKey.Ephemeral()), admin, gateId, 5, driver, phone, storage);
    }

    private static SyncTicket Sync(byte[] raw, int prints = 1, string? driverReason = null, SyncVoid? v = null) =>
        new(Convert.ToBase64String(raw), prints, driverReason, v);

    [Fact]
    public async Task Synced_ticket_is_stored_once_with_its_events_and_photo()
    {
        var env = await NewEnvAsync();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9 };
        var now = await env.NowAsync();
        var raw = env.Phone.TicketBytes(1, env.TruckId, env.DriverId, now - 60, photoSha256: SHA256.HashData(jpeg));

        var first = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([Sync(raw, 1, "usual driver sick")]));
        var second = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([Sync(raw, 2, "usual driver sick")]));

        var r = Assert.Single(first.Results);
        Assert.Equal("OK", r.Status);
        Assert.True(r.PhotoWanted);
        Assert.Equal("Q3-000001", r.TicketId);
        Assert.Equal("OK", second.Results[0].Status);
        Assert.Equal("SYNC", await env.Scalar<string>("SELECT first_seen_via FROM tg.tickets WHERE ticket_id = 'Q3-000001'"));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.ticket_events WHERE type = 'SYNCED'"));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.ticket_events WHERE type = 'PRINTED'"));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.ticket_events WHERE type = 'REPRINTED'"));
        Assert.NotNull(await env.Scalar<DateTime?>("SELECT last_sync_at FROM tg.devices WHERE id = 3"));

        var wrong = await Assert.ThrowsAsync<TruckGateException>(() =>
            env.Sync.StorePhotoAsync(env.Phone.DeviceId, 1, [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9, 9]));
        Assert.Equal("PHOTO_MISMATCH", wrong.Code);
        await env.Sync.StorePhotoAsync(env.Phone.DeviceId, 1, jpeg);
        await env.Sync.StorePhotoAsync(env.Phone.DeviceId, 1, jpeg); // idempotent
        Assert.Equal(jpeg, await env.Sync.ReadPhotoAsync(env.Phone.DeviceId, 1));
        var third = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([Sync(raw, 2)]));
        Assert.False(third.Results[0].PhotoWanted);
    }

    [Fact]
    public async Task Signed_void_blocks_the_ticket_at_the_gate_and_a_forged_void_alerts()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        var raw1 = env.Phone.TicketBytes(1, env.TruckId, env.DriverId, now - 900);
        var raw2 = env.Phone.TicketBytes(2, env.TruckId, env.DriverId, now - 900);
        var good = new SyncVoid(now - 800, "طابعة معطلة", env.Phone.SignVoidB64("Q3-000001", now - 800, "طابعة معطلة"));
        var forged = new SyncVoid(now - 800, "x", env.Phone.SignVoidB64("Q3-000002", now - 800, "y"));

        var res = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([Sync(raw1, v: good), Sync(raw2, v: forged)]));

        Assert.Equal("OK", res.Results[0].Status);
        Assert.Equal("REJECTED", res.Results[1].Status);
        Assert.Equal("BAD_VOID", res.Results[1].Error);
        Assert.Equal("CRITICAL", await env.Scalar<string>("SELECT severity FROM tg.alerts WHERE type = 'SYNC_BAD_VOID'"));
        var scan = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, TicketCodec.ToQrText(raw1), null), T);
        Assert.Equal("REJECT", scan.Outcome);
        Assert.Contains(GateReasons.Voided, scan.Reasons);
    }

    [Fact]
    public async Task Ticket_from_another_key_or_with_changed_content_is_refused()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        using var stranger = new QuarryPhone(3);
        var foreign = stranger.TicketBytes(1, env.TruckId, env.DriverId, now - 60);
        var original = env.Phone.TicketBytes(2, env.TruckId, env.DriverId, now - 60);
        var other = env.Phone.TicketBytes(2, env.TruckId, env.DriverId, now - 30);
        var otherDevice = env.Phone.TicketBytes(3, env.TruckId, env.DriverId, now - 60, claimDeviceId: 4);

        var res = await env.Sync.SyncAsync(env.Phone.DeviceId,
            new SyncRequest([Sync(foreign), Sync(original), Sync(other), Sync(otherDevice), new SyncTicket("not base64!", 1, null, null)]));

        Assert.Equal(new[] { "BAD_SIGNATURE", null, "TICKET_CONFLICT", "WRONG_DEVICE", "MALFORMED" }, res.Results.Select(r => r.Error));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'SYNC_BAD_SIGNATURE'"));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'SYNC_TICKET_CONFLICT'"));
    }

    [Fact]
    public async Task Damaged_qr_entry_by_short_code_needs_the_synced_copy_and_is_never_better_than_pending()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        var raw = env.Phone.TicketBytes(1, env.TruckId, env.DriverId, now - 900);
        var code = TicketCodec.ShortCode(raw);

        var before = await env.Gate.ScanManualAsync(env.GateId, "Q3-000001", code, null, T);
        Assert.Equal(new[] { "NO_SYNCED_COPY" }, before.Reasons);

        await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([Sync(raw)]));
        var wrong = await env.Gate.ScanManualAsync(env.GateId, "Q3-000001", "ZZZ-ZZZ", null, T);
        Assert.Equal(new[] { "BAD_SHORT_CODE" }, wrong.Reasons);

        var typed = code.ToLowerInvariant().Replace("-", " ").Replace('0', 'o');
        var ok = await env.Gate.ScanManualAsync(env.GateId, " q3-000001 ", typed, null, T);
        Assert.Equal("PENDING", ok.Outcome);
        Assert.Equal(new[] { "MANUAL_ENTRY" }, ok.Reasons);
        Assert.NotNull(ok.TripId);
        var decision = await env.Gate.RecordEntryDecisionAsync(ok.ScanId, EntryDecision.Full, null, null, T);
        Assert.Equal("PENDING_REVIEW", decision.State);

        var replay = await env.Gate.ScanManualAsync(env.GateId, "Q3-000001", code, null, T);
        Assert.Equal("REJECT", replay.Outcome);
        Assert.Contains(GateReasons.AlreadyUsed, replay.Reasons);
    }

    [Fact]
    public async Task Statement_counts_closed_trips_and_payment_marks_them_paid_once()
    {
        var env = await NewEnvAsync();
        await env.SetAsync("gate.min_unload_s", "0");
        await env.SetAsync("gate.min_truck_gap_s", "0");
        await env.SetAsync("trip.partial_pay_percent", "50");
        var now = await env.NowAsync();
        long seq = 0;
        foreach (var decision in new[] { EntryDecision.Full, EntryDecision.Partial, EntryDecision.Full })
        {
            var scan = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId,
                env.Phone.Ticket(++seq, env.TruckId, env.DriverId, now - 900), null), T);
            await env.Gate.RecordEntryDecisionAsync(scan.ScanId, decision, null, null, T);
            if (seq == 3) break; // the last truck is still inside: not payable yet
            var exit = await env.Exit.ScanManualAsync(env.GateId, scan.TripId!.Value, null, T);
            await env.Exit.RecordExitDecisionAsync(exit.ScanId, ExitDecision.Empty, null, null, T);
        }
        var today = DateOnly.FromDateTime(await env.Db.ReadAsync(c =>
            c.ExecuteScalarAsync<DateTime>("SELECT (now() AT TIME ZONE 'Africa/Algiers')::date::timestamp")));

        var noPrice = await Assert.ThrowsAsync<TruckGateException>(() => env.Reports.CreatePaymentBatchAsync(today, today, null, null, T));
        Assert.Equal("PRICE_NOT_SET", noPrice.Code);
        // the price is per truck (1.1); the global value is no longer used for payments
        await env.SetAsync("trip.price_per_trip", "9999");
        Assert.Equal(2, (await env.Reports.StatementAsync(today, today)).Excluded.Count(e => e.Reason == "NO_PRICE"));
        await new FleetService(env.Db).SetPriceAsync(env.TruckId, 1500m, "contract", null, T);

        var st = await env.Reports.StatementAsync(today, today);
        var line = Assert.Single(st.Lines);
        Assert.Equal(2, line.Trips);
        Assert.Equal(1, line.Partial);
        Assert.Equal(2250m, line.Amount);
        Assert.Equal(1, st.NotYetPayable);
        var truckLine = Assert.Single(st.Trucks);
        Assert.Equal(1500m, truckLine.PricePerTrip);
        Assert.Equal(2250m, truckLine.Total);
        Assert.Equal("UNPAID", truckLine.PaymentStatus);
        var csv = System.Text.Encoding.UTF8.GetString(ReportService.StatementCsv(st, "ar"));
        Assert.Contains("سائق 1", csv);
        Assert.Contains("2250.00", csv);

        var batch = await env.Reports.CreatePaymentBatchAsync(today, today, "week 47", null, T);
        Assert.Equal(2L, await env.Scalar<long>("SELECT count(*) FROM tg.trips WHERE current_state = 'PAID'"));
        Assert.Equal(2250m, (await env.Reports.BatchStatementAsync(batch)).Total);
        var again = await Assert.ThrowsAsync<TruckGateException>(() => env.Reports.CreatePaymentBatchAsync(today, today, null, null, T));
        Assert.Equal("NOTHING_TO_PAY", again.Code);
        Assert.Single(await env.Reports.BatchesAsync());
        Assert.True((await new AuditService(env.Db).VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Dashboard_counts_today()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, env.Phone.Ticket(1, env.TruckId, env.DriverId, now - 900), null), T);
        await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, "TG1:garbage", null), T);

        var d = await env.Reports.DashboardAsync();

        Assert.Equal(2, d.Entries);
        Assert.Equal(1, d.Accepted);
        Assert.Equal(1, d.Rejected);
        Assert.Equal(1, Assert.Single(d.ByTruck).Count);
        Assert.Equal(2, d.Recent.Count);
    }
}
