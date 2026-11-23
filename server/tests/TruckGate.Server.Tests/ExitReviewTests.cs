using System.Security.Cryptography;
using Dapper;
using Npgsql;
using TruckGate.Application;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

/// <summary>Exit with the signed receipt, review decisions, labels, daily sample and watchdog — each test on its own database.</summary>
[Collection("db")]
public class ExitReviewTests(DatabaseFixture fx)
{
    private static readonly Actor T = DatabaseFixture.Tester;

    private sealed class Env : IDisposable
    {
        public required NpgsqlDataSource Admin { get; init; }
        public required Db Db { get; init; }
        public required GateService Gate { get; init; }
        public required ExitService Exit { get; init; }
        public required ReviewService Review { get; init; }
        public required AdminService AdminSvc { get; init; }
        public required ServerSigningKey Key { get; init; }
        public short GateId { get; set; }
        public int TruckId { get; set; }
        public int DriverId { get; set; }
        public required QuarryPhone Phone { get; init; }
        private long _seq;

        public long NextSeq() => ++_seq;

        public Task<long> NowAsync() => Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint"));

        public Task SetAsync(string key, string json) =>
            Db.InTransactionAsync(T, (c, tx) => c.ExecuteAsync("UPDATE tg.settings SET value = CAST(@json AS jsonb) WHERE key = @key",
                new { key, json }, tx));

        public Task<TV> Scalar<TV>(string sql, object? args = null) => Db.ReadAsync(c => c.ExecuteScalarAsync<TV>(sql, args))!;

        public void Dispose()
        {
            Phone.Dispose();
            Key.Dispose();
        }
    }

    private async Task<Env> NewEnvAsync()
    {
        var (admin, db) = await fx.CreateIsolatedDatabaseAsync();
        var key = ServerSigningKey.Ephemeral();
        var env = new Env
        {
            Admin = admin, Db = db, Gate = new GateService(db), Exit = new ExitService(db, key),
            Review = new ReviewService(db, new StorageOptions(Path.Combine(Path.GetTempPath(), "tg-rev-" + Guid.NewGuid().ToString("N")[..8]))),
            AdminSvc = new AdminService(db), Key = key, Phone = new QuarryPhone(7),
        };
        env.GateId = (short)await env.AdminSvc.CreateGateAsync("G1", "Main gate", null, T);
        env.DriverId = await env.AdminSvc.CreateDriverAsync("Driver", null, "EMPLOYEE", T);
        env.TruckId = 5;
        await env.AdminSvc.CreateTruckAsync(env.TruckId, "05", "Foton", env.DriverId, "01234 216 16", T);
        await env.AdminSvc.EnrollDeviceAsync(env.Phone.DeviceId, "QUARRY_PHONE", "Phone", env.Phone.PublicKey, T);
        return env;
    }

    private static async Task<(ScanEntryResult Scan, long TripId)> EnterAsync(Env env, EntryDecision? decision = EntryDecision.Full,
        int? truckId = null)
    {
        var now = await env.NowAsync();
        var scan = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId,
            env.Phone.Ticket(env.NextSeq(), truckId ?? env.TruckId, env.DriverId, now - 900), null), T);
        Assert.NotNull(scan.TripId);
        if (decision is not null) await env.Gate.RecordEntryDecisionAsync(scan.ScanId, decision.Value, null, null, T);
        return (scan, scan.TripId!.Value);
    }

    [Fact]
    public async Task Receipt_exit_closes_the_trip_and_a_second_use_is_refused()
    {
        using var env = await NewEnvAsync();
        await env.SetAsync("gate.min_unload_s", "0");
        var (_, tripId) = await EnterAsync(env);

        var receipt = await env.Exit.PrintReceiptAsync(tripId, null, T);
        Assert.StartsWith("TR1:", receipt.QrText);
        Assert.Equal("05", receipt.FleetNumber);
        Assert.Equal(1, receipt.PrintCount);
        Assert.Equal(2, (await env.Exit.PrintReceiptAsync(tripId, null, T)).PrintCount);
        Assert.Contains(await env.Exit.InsideAsync(), c => c.TripId == tripId);

        var exit = await env.Exit.ScanReceiptAsync(env.GateId, receipt.QrText, null, T);
        Assert.Equal("ACCEPT", exit.Outcome);
        Assert.Equal(tripId, exit.Trip!.TripId);
        var decided = await env.Exit.RecordExitDecisionAsync(exit.ScanId, ExitDecision.Empty, null, null, T);
        Assert.Equal("CLOSED", decided.State);
        Assert.DoesNotContain(await env.Exit.InsideAsync(), c => c.TripId == tripId);
        await Assert.ThrowsAsync<TruckGateException>(() => env.Exit.RecordExitDecisionAsync(exit.ScanId, ExitDecision.Empty, null, null, T));

        var again = await env.Exit.ScanReceiptAsync(env.GateId, receipt.QrText, null, T);
        Assert.Equal("REJECT", again.Outcome);
        Assert.Equal(new[] { "ALREADY_EXITED" }, again.Reasons);
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'GATE_ALREADY_EXITED'"));
        Assert.True((await new AuditService(env.Db).VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Receipt_signed_by_another_key_is_refused_as_forged()
    {
        using var env = await NewEnvAsync();
        var (_, tripId) = await EnterAsync(env);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = GateReceipt.ToQrText(GateReceipt.Build(new ReceiptFields((uint)tripId, (ushort)env.GateId, 1_800_000_000,
                (ushort)env.TruckId, (ushort)env.Phone.DeviceId, 1, ReceiptFields.LoadFull),
            d => other.SignData(d, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));

        var exit = await env.Exit.ScanReceiptAsync(env.GateId, forged, null, T);

        Assert.Equal("REJECT", exit.Outcome);
        Assert.Equal(new[] { GateReasons.BadSignature }, exit.Reasons);
        Assert.Equal("CRITICAL", await env.Scalar<string>("SELECT severity FROM tg.alerts WHERE type = 'GATE_FORGED_RECEIPT'"));
        Assert.Null(await env.Scalar<long?>("SELECT exit_scan_id FROM tg.trips WHERE id = @tripId", new { tripId }));
    }

    [Fact]
    public async Task Fast_or_loaded_exit_goes_to_review_and_acceptance_closes_it()
    {
        using var env = await NewEnvAsync();
        var (_, tripId) = await EnterAsync(env);
        var receipt = await env.Exit.PrintReceiptAsync(tripId, null, T);

        var exit = await env.Exit.ScanReceiptAsync(env.GateId, receipt.QrText, null, T);
        Assert.Equal("PENDING", exit.Outcome);
        Assert.Equal(new[] { "EXIT_TOO_SOON" }, exit.Reasons);
        var decided = await env.Exit.RecordExitDecisionAsync(exit.ScanId, ExitDecision.Loaded, "still full", null, T);

        Assert.Equal("PENDING_REVIEW", decided.State);
        Assert.Equal(new[] { "EXIT_TOO_SOON", "EXIT_LOADED" }, decided.Reasons);
        Assert.Equal("HIGH", await env.Scalar<string>("SELECT priority FROM tg.reviews WHERE id = @id", new { id = decided.ReviewId }));
        Assert.Equal("CRITICAL", await env.Scalar<string>("SELECT severity FROM tg.alerts WHERE type = 'GATE_EXIT_LOADED'"));

        var queue = await env.Review.QueueAsync(false);
        var item = Assert.Single(queue);
        Assert.Equal("HIGH", item.Priority);
        Assert.True(item.Exited);
        Assert.Contains("EXIT_LOADED", item.Reasons);

        var detail = await env.Review.TripAsync(tripId);
        Assert.Equal(2, detail.Scans.Count);
        Assert.NotNull(detail.Ticket);

        var reviewed = await env.Review.DecideAsync(tripId, ReviewDecision.Accept, "unloaded, gatekeeper mistake", null, T);
        Assert.Equal("CLOSED", reviewed.State);
        Assert.Empty(await env.Review.QueueAsync(true));
    }

    [Fact]
    public async Task Exit_and_receipt_need_the_entry_decision_first()
    {
        using var env = await NewEnvAsync();
        var (_, tripId) = await EnterAsync(env, decision: null);

        var e = await Assert.ThrowsAsync<TruckGateException>(() => env.Exit.PrintReceiptAsync(tripId, null, T));
        Assert.Equal("ENTRY_NOT_DECIDED", e.Code);
        var exit = await env.Exit.ScanManualAsync(env.GateId, tripId, null, T);
        Assert.Equal("REJECT", exit.Outcome);
        Assert.Equal(new[] { "ENTRY_NOT_DECIDED" }, exit.Reasons);
    }

    [Fact]
    public async Task Lost_receipt_exit_is_picked_from_the_trucks_inside_and_noted()
    {
        using var env = await NewEnvAsync();
        await env.SetAsync("gate.min_unload_s", "0");
        var (_, tripId) = await EnterAsync(env, EntryDecision.Partial);

        var exit = await env.Exit.ScanManualAsync(env.GateId, tripId, null, T);

        Assert.Equal("ACCEPT", exit.Outcome);
        Assert.Equal(new[] { ExitService.ReceiptMissing }, exit.Notes);
        Assert.Equal("MANUAL_SELECT", await env.Scalar<string>("SELECT scan_type FROM tg.gate_scans WHERE id = @id", new { id = exit.ScanId }));
        Assert.Equal("CLOSED", (await env.Exit.RecordExitDecisionAsync(exit.ScanId, ExitDecision.Empty, null, null, T)).State);
    }

    [Fact]
    public async Task Supervisor_rejection_needs_a_reason_and_is_final()
    {
        using var env = await NewEnvAsync();
        var (_, tripId) = await EnterAsync(env, EntryDecision.DifferentTruck);

        var e = await Assert.ThrowsAsync<TruckGateException>(() => env.Review.DecideAsync(tripId, ReviewDecision.Reject, " ", null, T));
        Assert.Equal("REASON_REQUIRED", e.Code);
        var rejected = await env.Review.DecideAsync(tripId, ReviewDecision.Reject, "plate differs from ticket", null, T);
        Assert.Equal("REJECTED", rejected.State);
        var again = await Assert.ThrowsAsync<TruckGateException>(() => env.Review.DecideAsync(tripId, ReviewDecision.Accept, null, null, T));
        Assert.Equal("NO_OPEN_REVIEW", again.Code);
        // the decided review cannot be edited, even by the application role
        await Assert.ThrowsAsync<PostgresException>(() => env.Db.InTransactionAsync(T, (c, tx) =>
            c.ExecuteAsync("UPDATE tg.reviews SET decision = 'ACCEPT' WHERE trip_id = @tripId", new { tripId }, tx)));
    }

    [Fact]
    public async Task Accepted_while_inside_returns_to_entered_and_partial_acceptance_changes_the_load()
    {
        using var env = await NewEnvAsync();
        await env.SetAsync("gate.min_unload_s", "0");
        var (_, tripId) = await EnterAsync(env, EntryDecision.PlateUnreadable);

        var reviewed = await env.Review.DecideAsync(tripId, ReviewDecision.AcceptPartial, null, null, T);
        Assert.Equal("ENTERED", reviewed.State);
        Assert.Equal("PARTIAL", await env.Scalar<string>("SELECT load_status FROM tg.trips WHERE id = @tripId", new { tripId }));

        var exit = await env.Exit.ScanReceiptAsync(env.GateId, (await env.Exit.PrintReceiptAsync(tripId, null, T)).QrText, null, T);
        Assert.Equal("CLOSED", (await env.Exit.RecordExitDecisionAsync(exit.ScanId, ExitDecision.Empty, null, null, T)).State);
    }

    [Fact]
    public async Task Pictures_get_labels_and_the_latest_label_wins()
    {
        using var env = await NewEnvAsync();
        var (scan, tripId) = await EnterAsync(env);
        long snapshotId;
        await using (var c = await env.Admin.OpenConnectionAsync())
        {
            var cam = await c.ExecuteScalarAsync<int>("""
                INSERT INTO tg.cameras (label, gate_id, role, capture_on_scan, frames, frame_interval_ms, source, snapshot_url)
                VALUES ('cargo', @g, 'CARGO', true, 1, 0, 'URL', 'http://cam/snap.jpg') RETURNING id
                """, new { g = env.GateId });
            snapshotId = await c.ExecuteScalarAsync<long>("""
                INSERT INTO tg.snapshots (gate_scan_id, camera_id, camera_role, camera_config_version, frame_index, captured_at,
                                          path, sha256, size_bytes)
                VALUES (@s, @cam, 'CARGO', 1, 0, now(), 'x.jpg', @sha, 10) RETURNING id
                """, new { s = scan.ScanId, cam, sha = new byte[32] });
        }

        await env.Review.LabelAsync(snapshotId, "load", "partial", null, T);
        await env.Review.LabelAsync(snapshotId, "LOAD", "FULL", null, T);
        await env.Review.LabelAsync(snapshotId, "PLATE", " 01234 216 16 ", null, T);
        await Assert.ThrowsAsync<TruckGateException>(() => env.Review.LabelAsync(snapshotId, "LOAD", "HEAPED", null, T));
        await Assert.ThrowsAsync<TruckGateException>(() => env.Review.LabelAsync(999_999, "LOAD", "FULL", null, T));

        var picture = Assert.Single((await env.Review.TripAsync(tripId)).Snapshots);
        Assert.Equal("FULL", picture.LoadLabel);
        Assert.Equal("01234 216 16", picture.PlateLabel);
        Assert.Equal(3L, await env.Scalar<long>("SELECT count(*) FROM tg.snapshot_labels"));
    }

    [Fact]
    public async Task Daily_sample_reviews_some_accepted_trips_once()
    {
        using var env = await NewEnvAsync();
        await env.SetAsync("gate.min_truck_gap_s", "0");
        for (int i = 0; i < 4; i++) await EnterAsync(env);
        var today = DateOnly.FromDateTime(await env.Db.ReadAsync(c => c.ExecuteScalarAsync<DateTime>("SELECT (now() AT TIME ZONE 'UTC')::date::timestamp")));

        var n = await env.Db.InTransactionAsync(T, (c, tx) => ReviewService.CreateDailySampleAsync(c, tx, today, "UTC", 25));
        var again = await env.Db.InTransactionAsync(T, (c, tx) => ReviewService.CreateDailySampleAsync(c, tx, today, "UTC", 25));

        Assert.Equal(1, n);
        Assert.Equal(0, again);
        var item = Assert.Single(await env.Review.QueueAsync(true));
        Assert.True(item.IsSample);
        Assert.Equal("LOW", item.Priority);
        Assert.Empty(await env.Review.QueueAsync(false));
        Assert.Equal("ENTERED", (await env.Review.DecideAsync(item.TripId, ReviewDecision.Accept, null, null, T)).State);
    }

    [Fact]
    public async Task Watchdog_flags_undecided_entries_and_trucks_that_never_left_once()
    {
        using var env = await NewEnvAsync();
        await env.SetAsync("gate.min_truck_gap_s", "0");
        await env.SetAsync("gate.entry_decision_alert_s", "0");
        await env.SetAsync("trip.exit_deadline_s", "0");
        var (_, undecided) = await EnterAsync(env, decision: null);
        var (_, inside) = await EnterAsync(env);
        var watchdog = new Watchdog(env.Db);

        var first = await watchdog.RunOnceAsync();
        var second = await watchdog.RunOnceAsync();

        Assert.Equal(1, first.UndecidedEntries);
        Assert.Equal(1, first.NoExit);
        Assert.Equal(0, second.UndecidedEntries);
        Assert.Equal(0, second.NoExit);
        Assert.Equal("PENDING_REVIEW", await env.Scalar<string>("SELECT current_state FROM tg.trips WHERE id = @inside", new { inside }));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'ENTRY_NOT_DECIDED' AND entity_id = @id",
            new { id = undecided.ToString() }));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'NO_EXIT_SCAN'"));
        // the phone enrolled minutes ago is not overdue
        Assert.Equal(0L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'SYNC_OVERDUE'"));

        // accepting a truck that never scanned out closes the trip (the supervisor confirmed the unloading)
        Assert.Equal("CLOSED", (await env.Review.DecideAsync(inside, ReviewDecision.Accept, "exit camera checked", null, T)).State);
    }

    [Fact]
    public async Task Repeated_partial_loads_alert_once_per_day_for_driver_and_operator()
    {
        using var env = await NewEnvAsync();
        await env.SetAsync("gate.min_truck_gap_s", "0");
        await env.SetAsync("trip.partial_alert_threshold", "2");
        for (int i = 0; i < 3; i++) await EnterAsync(env, EntryDecision.Partial);

        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'PARTIAL_LOADS_DRIVER'"));
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.alerts WHERE type = 'PARTIAL_LOADS_OPERATOR'"));
    }
}
