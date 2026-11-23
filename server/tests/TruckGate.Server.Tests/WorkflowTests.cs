using System.Text.Json;
using Dapper;
using TruckGate.Application;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

/// <summary>
/// Workflow 1.1: a truck registered from the quarry phone, its approval by the supervisor (five fields,
/// price per truck), the gate with a provisional truck, burned receipts, investigation, trip labels,
/// payment per truck and adjustments, supervisor settings scope.
/// </summary>
[Collection("db")]
public class WorkflowTests(DatabaseFixture fx)
{
    private static readonly Actor T = DatabaseFixture.Tester;

    private sealed record Env(Db Db, GateService Gate, SyncService Sync, ExitService Exit, FleetService Fleet, ReviewService Review,
        ReceiptsService Receipts, ReportService Reports, SettingsService Settings, EnrollmentService Enrollment, AdminService Admin,
        short GateId, int DriverId, int OperatorId, QuarryPhone Phone)
    {
        public Task<long> NowAsync() => Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint"));

        public Task<TV> Scalar<TV>(string sql, object? args = null) => Db.ReadAsync(c => c.ExecuteScalarAsync<TV>(sql, args))!;

        public Task SetAsync(string key, string json) =>
            Db.InTransactionAsync(T, (c, tx) => c.ExecuteAsync("UPDATE tg.settings SET value = CAST(@json AS jsonb) WHERE key = @key",
                new { key, json }, tx));

        public async Task<int[][]> BlocksAsync()
        {
            var cfg = await Enrollment.GetSignedConfigAsync(Phone.DeviceId);
            using var doc = JsonDocument.Parse(cfg.Payload);
            return doc.RootElement.GetProperty("content").GetProperty("truckIdBlocks").Deserialize<int[][]>()!;
        }

        public async Task<long> EnterAsync(long seq, int truckId, long issuedAt, EntryDecision decision = EntryDecision.Full,
            string? observedPlate = null)
        {
            var scan = await Gate.ScanEntryAsync(new ScanEntryRequest(GateId, Phone.Ticket(seq, truckId, DriverId, issuedAt,
                operatorId: OperatorId), null), T);
            Assert.NotNull(scan.TripId);
            await Gate.RecordEntryDecisionAsync(scan.ScanId, decision, null, observedPlate, null, T);
            return scan.TripId!.Value;
        }

        public async Task ExitEmptyAsync(long tripId)
        {
            var exit = await Exit.ScanManualAsync(GateId, tripId, null, T);
            await Exit.RecordExitDecisionAsync(exit.ScanId, ExitDecision.Empty, null, null, T);
        }

        public Task<string> StateAsync(long tripId) => Scalar<string>("SELECT current_state FROM tg.trips WHERE id = @tripId", new { tripId });
    }

    private async Task<Env> NewEnvAsync()
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var admin = new AdminService(db);
        var gateId = (short)await admin.CreateGateAsync("G1", "Main gate", null, T);
        var driver = await admin.CreateDriverAsync("سائق 1", null, "CONTRACTOR", T);
        var op = await admin.CreateUserAsync("op1", "عامل المقلع", "QUARRY_OPERATOR", "ar", T);
        await admin.SetUserPinAsync(op, "4821", T);
        await admin.CreateTruckAsync(5, "05", "Foton", driver, "01234 216 16", T);
        var fleet = new FleetService(db);
        await fleet.SetPriceAsync(5, 1400m, "contract", null, T);
        var phone = new QuarryPhone(3);
        await admin.EnrollDeviceAsync(phone.DeviceId, "QUARRY_PHONE", "Phone", phone.PublicKey, T);
        var storage = new StorageOptions(Path.Combine(Path.GetTempPath(), "tg-wf-" + Guid.NewGuid().ToString("N")[..8]));
        var env = new Env(db, new GateService(db), new SyncService(db, storage), new ExitService(db, ServerSigningKey.Ephemeral()),
            fleet, new ReviewService(db, storage), new ReceiptsService(db), new ReportService(db), new SettingsService(db),
            new EnrollmentService(db, ServerSigningKey.Ephemeral(), EnrollmentOptions.Default("http://192.168.1.10:5080")), admin,
            gateId, driver, op, phone);
        await env.SetAsync("gate.min_unload_s", "0");
        await env.SetAsync("gate.min_truck_gap_s", "0");
        return env;
    }

    private static DateOnly Today(Env env) => DateOnly.FromDateTime(env.Db.ReadAsync(c =>
        c.ExecuteScalarAsync<DateTime>("SELECT (now() AT TIME ZONE 'Africa/Algiers')::date::timestamp")).GetAwaiter().GetResult());

    [Fact]
    public void Plate_rules_accept_arabic_digits_and_formatting_and_refuse_nonsense()
    {
        Assert.Equal("09876 112 31", PlateRules.Normalize("٠٩٨٧٦-١١٢-٣١"));
        Assert.Equal("123456 116 16", PlateRules.Normalize("12345611616"));
        Assert.Equal("1234567", PlateRules.Normalize("1234567"));
        Assert.Null(PlateRules.Normalize("12AB"));
        Assert.Null(PlateRules.Normalize("12345"));
        Assert.Null(PlateRules.Normalize(""));
        Assert.Equal("0987611231", PlateRules.Key("09876 112 31"));
    }

    [Fact]
    public async Task New_truck_from_phone_is_pending_then_approved_and_its_trip_is_paid_at_its_own_price()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();

        // the phone holds a block of reserved numbers in its signed configuration
        var blocks = await env.BlocksAsync();
        var block = Assert.Single(blocks);
        Assert.Equal(ProvisionalTruckIds.First, block[0]);
        Assert.Equal(ProvisionalTruckIds.First + ProvisionalTruckIds.BlockSize - 1, block[1]);
        int newId = block[0];

        // worker types the plate with Arabic digits and dashes
        var sync = await env.Sync.SyncAsync(env.Phone.DeviceId,
            new SyncRequest([], [new SyncTruck(newId, "٠٩٨٧٦-١١٢-٣١", now, env.OperatorId, null)]));
        Assert.Equal("OK", Assert.Single(sync.Trucks!).Status);
        Assert.Equal("PENDING_APPROVAL", await env.Scalar<string>("SELECT approval_status FROM tg.trucks WHERE id = @newId", new { newId }));
        Assert.Equal("09876 112 31", await env.Scalar<string>("SELECT reported_plate FROM tg.trucks WHERE id = @newId", new { newId }));
        Assert.Equal("MEDIUM", await env.Scalar<string>("SELECT severity FROM tg.alerts WHERE type = 'TRUCK_REGISTERED'"));
        Assert.Equal(1, await env.Scalar<int>("SELECT new_trucks FROM tg.sync_runs ORDER BY id DESC LIMIT 1"));

        // sent again (no answer reached the phone): accepted once
        var again = await env.Sync.SyncAsync(env.Phone.DeviceId,
            new SyncRequest([], [new SyncTruck(newId, "09876 112 31", now, env.OperatorId, null)]));
        Assert.Equal("OK", again.Trucks![0].Status);
        Assert.Equal(1L, await env.Scalar<long>("SELECT count(*) FROM tg.trucks WHERE created_via = 'PHONE'"));

        // the other phones see it after their next configuration, with its status
        var cfg = await env.Enrollment.GetSignedConfigAsync(env.Phone.DeviceId);
        using (var doc = JsonDocument.Parse(cfg.Payload))
        {
            var t = doc.RootElement.GetProperty("content").GetProperty("trucks").EnumerateArray()
                .Single(x => x.GetProperty("id").GetInt32() == newId);
            Assert.Equal("PENDING_APPROVAL", t.GetProperty("status").GetString());
            Assert.True(doc.RootElement.GetProperty("content").GetProperty("params").GetProperty("photoRequired").GetBoolean());
        }

        // at the gate: may enter, waits for the supervisor; the gatekeeper notes the plate he sees
        var scan = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId,
            env.Phone.Ticket(1, newId, env.DriverId, now - 900, operatorId: env.OperatorId), null), T);
        Assert.Equal("PENDING", scan.Outcome);
        Assert.Equal(new[] { GateService.TruckPendingApproval }, scan.Reasons);
        var names = await env.Gate.NamesAsync(scan.Ticket!);
        Assert.Equal("PENDING_APPROVAL", names.TruckStatus);
        Assert.Equal("عامل المقلع", names.OperatorName);
        var decision = await env.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Full, null, "09876-112-31", null, T);
        Assert.Equal("PENDING_REVIEW", decision.State);
        Assert.Equal("09876 112 31", await env.Scalar<string>("SELECT observed_plate FROM tg.gate_decisions WHERE gate_scan_id = @id",
            new { id = scan.ScanId }));

        // not payable while the truck is not approved
        var tripId = scan.TripId!.Value;
        var today = Today(env);

        // the five fields are required
        var missing = await Assert.ThrowsAsync<TruckGateException>(() => env.Fleet.ApproveAsync(newId,
            new TruckApproval("C-77", "", "محمد", "09876 112 31", 1500m, null, null), null, T));
        Assert.Equal("OWNER_LAST_NAME_REQUIRED", missing.Code);
        var badPrice = await Assert.ThrowsAsync<TruckGateException>(() => env.Fleet.ApproveAsync(newId,
            new TruckApproval("C-77", "بن علي", "محمد", "09876 112 31", 0m, null, null), null, T));
        Assert.Equal("PRICE_INVALID", badPrice.Code);
        var approved = await env.Fleet.ApproveAsync(newId,
            new TruckApproval("C-77", "بن علي", "محمد", "09876 112 31", 1500m, null, env.DriverId), null, T);
        Assert.Equal(1, approved.ReviewsResolved);
        Assert.Equal("ENTERED", await env.StateAsync(tripId));
        var twice = await Assert.ThrowsAsync<TruckGateException>(() => env.Fleet.RejectAsync(newId, "x", null, T));
        Assert.Equal("TRUCK_ALREADY_DECIDED", twice.Code);
        var truck = await env.Fleet.GetAsync(newId);
        Assert.Equal("APPROVED", truck.ApprovalStatus);
        Assert.Equal("C-77", truck.FleetNumber);
        Assert.Equal(1500m, truck.Price);
        Assert.True(truck.Complete);
        Assert.Contains("09876 112 31", truck.ObservedPlates);

        await env.ExitEmptyAsync(tripId);
        Assert.Equal("CLOSED", await env.StateAsync(tripId));

        // burned receipts: kept, searchable by plate digits
        var burned = await env.Receipts.ListAsync(new BurnedFilter(today, today, null, null, null, null, null, "0987611231"));
        var row = Assert.Single(burned.Items);
        Assert.Equal("Q3-000001", row.TicketId);
        Assert.Equal("CLOSED", row.Lifecycle);
        Assert.Equal("DECIDED", row.ReviewStatus);
        Assert.Equal("عامل المقلع", row.OperatorName);

        // statement: C-77 at its own price
        var st = await env.Reports.StatementAsync(today, today);
        var line = Assert.Single(st.Trucks);
        Assert.Equal("C-77", line.FleetNumber);
        Assert.Equal("بن علي", line.OwnerLastName);
        Assert.Equal(1500m, line.PricePerTrip);
        Assert.Equal(1500m, line.Total);
        Assert.Equal("09876 112 31", line.Plate);

        // the truck history shows its price and the audit trail
        var history = await env.Fleet.HistoryAsync(newId);
        Assert.Single(history.Prices);
        Assert.Single(history.Trips);
        Assert.Contains(history.Audit, a => a.Entity == "trucks" && a.Action == "update");
        Assert.True((await new AuditService(env.Db).VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Provisional_number_not_synced_yet_is_pending_and_a_foreign_number_is_unknown()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        var blocks = await env.BlocksAsync();

        var reserved = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId,
            env.Phone.Ticket(1, blocks[0][0] + 1, env.DriverId, now - 900), null), T);
        Assert.Equal("PENDING", reserved.Outcome);
        Assert.Equal(new[] { GateService.TruckPendingApproval }, reserved.Reasons);
        Assert.Contains(GateService.TruckNotSynced, reserved.Notes);

        var foreign = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId,
            env.Phone.Ticket(2, 60000, env.DriverId, now - 900), null), T);
        Assert.Equal("PENDING", foreign.Outcome);
        Assert.Contains(GateReasons.UnknownTruck, foreign.Reasons);

        // the trip closes after the supervisor accepts it, but it is not paid: no approved truck
        await env.Gate.RecordEntryDecisionAsync(reserved.ScanId, EntryDecision.Full, null, "11111 222 33", null, T);
        await env.Review.DecideAsync(reserved.TripId!.Value, ReviewDecision.Accept, "seen at the gate", null, T);
        await env.ExitEmptyAsync(reserved.TripId!.Value);
        Assert.Equal("CLOSED", await env.StateAsync(reserved.TripId!.Value));
        var st = await env.Reports.StatementAsync(Today(env), Today(env));
        Assert.Empty(st.Trips);
        Assert.Equal("TRUCK_UNKNOWN", Assert.Single(st.Excluded).Reason);

        // a phone cannot register a number outside its blocks
        var bad = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([], [new SyncTruck(50000, "11111 222 33", now, null, null)]));
        Assert.Equal("TRUCK_ID_NOT_ALLOWED", bad.Trucks![0].Error);
        Assert.Equal("CRITICAL", await env.Scalar<string>("SELECT severity FROM tg.alerts WHERE type = 'SYNC_TRUCK_ID_NOT_ALLOWED'"));
    }

    [Fact]
    public async Task Duplicate_truck_is_pointed_out_merged_and_paid_for_the_existing_truck_rejected_one_disappears()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        var blocks = await env.BlocksAsync();
        int a = blocks[0][0], b = blocks[0][0] + 1;
        var res = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([], [
            new SyncTruck(a, "01234-216-16", now, env.OperatorId, null), // same plate as truck 05
            new SyncTruck(b, "55555 555 55", now, env.OperatorId, null),
        ]));
        Assert.All(res.Trucks!, r => Assert.Equal("OK", r.Status));
        Assert.Equal(5, (await env.Fleet.GetAsync(a)).PossibleDuplicateOf);

        // same number with another plate: conflict
        var conflict = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([], [new SyncTruck(a, "99999 999 99", now, null, null)]));
        Assert.Equal("TRUCK_CONFLICT", conflict.Trucks![0].Error);

        var tripA = await env.EnterAsync(1, a, now - 900);
        Assert.Equal("PENDING_REVIEW", await env.StateAsync(tripA));

        var noTarget = await Assert.ThrowsAsync<TruckGateException>(() => env.Fleet.MergeAsync(a, b, null, null, T));
        Assert.Equal("MERGE_TARGET", noTarget.Code);
        var merged = await env.Fleet.MergeAsync(a, 5, "same truck as 05", null, T);
        Assert.Equal(1, merged.ReviewsResolved);
        var reasonless = await Assert.ThrowsAsync<TruckGateException>(() => env.Fleet.RejectAsync(b, " ", null, T));
        Assert.Equal("REASON_REQUIRED", reasonless.Code);
        await env.Fleet.RejectAsync(b, "plate typed by mistake", null, T);

        await env.ExitEmptyAsync(tripA);
        var st = await env.Reports.StatementAsync(Today(env), Today(env));
        var line = Assert.Single(st.Trucks);
        Assert.Equal(5, line.TruckId);
        Assert.Equal(1400m, line.Total);
        Assert.Equal(a, Assert.Single(st.Trips).TruckId);

        // the gate counts the merged truck as 05 (truck gap uses its trips)
        await env.SetAsync("gate.min_truck_gap_s", "1200");
        var gap = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, env.Phone.Ticket(2, 5, env.DriverId, now - 900), null), T);
        Assert.Contains(GateReasons.TruckGap, gap.Reasons);

        // phones no longer list merged or rejected trucks
        var cfg = await env.Enrollment.GetSignedConfigAsync(env.Phone.DeviceId);
        using var doc = JsonDocument.Parse(cfg.Payload);
        var ids = doc.RootElement.GetProperty("content").GetProperty("trucks").EnumerateArray().Select(x => x.GetProperty("id").GetInt32());
        Assert.Equal(new[] { 5 }, ids);
        // and a rejected truck's ticket is an unknown truck at the gate
        var rejected = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, env.Phone.Ticket(3, b, env.DriverId, now - 900), null), T);
        Assert.Contains(GateReasons.UnknownTruck, rejected.Reasons);
    }

    [Fact]
    public async Task Investigation_keeps_the_review_open_labels_are_stored_and_adjustments_are_paid_later()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        var trip = await env.EnterAsync(1, 5, now - 900, EntryDecision.Partial);
        await env.ExitEmptyAsync(trip);
        var reviewed = await env.EnterAsync(2, 5, now - 800, EntryDecision.Empty); // GATE_EMPTY → review
        await env.Review.InvestigateAsync(reviewed, "check the quarry photo", null, T);
        var item = Assert.Single(await env.Review.QueueAsync(true));
        Assert.True(item.Investigating);
        Assert.Equal("check the quarry photo", item.LastNote);
        var burned = await env.Receipts.ListAsync(new BurnedFilter(null, null, null, null, null, null, "INVESTIGATING", null));
        Assert.Equal(reviewed, Assert.Single(burned.Items).TripId);

        await env.Review.AnnotateAsync(reviewed, ["empty_truck", "NEEDS_REVIEW"], "no cargo on camera", null, T);
        var bad = await Assert.ThrowsAsync<TruckGateException>(() => env.Review.AnnotateAsync(reviewed, ["CAT"], null, null, T));
        Assert.Equal("LABEL_INVALID", bad.Code);
        var detail = await env.Review.TripAsync(reviewed);
        Assert.Equal(new[] { "EMPTY_TRUCK", "NEEDS_REVIEW" }, detail.Annotations[0].Labels);
        Assert.Equal("INVESTIGATE", Assert.Single(detail.Notes).Kind);

        // pay the partial trip (100 % by default), then correct it with an adjustment
        var today = Today(env);
        var batch = await env.Reports.CreatePaymentBatchAsync(today, today, null, null, T);
        Assert.Equal("PAID", await env.StateAsync(trip));
        var adj = await env.Reports.AddAdjustmentAsync(5, trip, -200m, "partial load: 1200 agreed", null, T);
        Assert.True(adj > 0);
        var zero = await Assert.ThrowsAsync<TruckGateException>(() =>
            env.Reports.AddAdjustmentAsync(5, trip, 0m, "x", null, T));
        Assert.Equal("AMOUNT_INVALID", zero.Code);
        var st = await env.Reports.StatementAsync(today, today);
        Assert.Equal(-200m, st.AdjustmentsTotal);
        Assert.Equal("UNPAID", Assert.Single(st.Trucks).PaymentStatus);
        var second = await env.Reports.CreatePaymentBatchAsync(today, today, "correction", null, T);
        Assert.Equal(-200m, (await env.Reports.BatchStatementAsync(second)).Total);
        Assert.Equal(1400m, (await env.Reports.BatchStatementAsync(batch)).Total); // the paid batch never changes
        Assert.Empty(await env.Reports.AdjustmentsAsync(pendingOnly: true));
        Assert.True((await new AuditService(env.Db).VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Supervisor_settings_scope_photo_policy_and_ticket_without_photo()
    {
        var env = await NewEnvAsync();
        var forbidden = await Assert.ThrowsAsync<TruckGateException>(() =>
            env.Settings.SetAsync("alerts.sms_enabled", JsonDocument.Parse("true").RootElement, false, T));
        Assert.Equal(ErrorKind.Forbidden, forbidden.Kind);
        await env.Settings.SetAsync("quarry.photo_required", JsonDocument.Parse("false").RootElement, false, T);
        await env.Settings.SetAsync("video.pre_s", JsonDocument.Parse("30").RootElement, false, T);
        var tooLong = await Assert.ThrowsAsync<TruckGateException>(() =>
            env.Settings.SetAsync("video.post_s", JsonDocument.Parse("600").RootElement, false, T));
        Assert.Equal("SETTING_INVALID", tooLong.Code);
        Assert.Contains(await env.Settings.ListAsync(), s => s.Key == "alerts.sms_enabled" && s.AdminOnly);

        var cfg = await env.Enrollment.GetSignedConfigAsync(env.Phone.DeviceId);
        using (var doc = JsonDocument.Parse(cfg.Payload))
            Assert.False(doc.RootElement.GetProperty("content").GetProperty("params").GetProperty("photoRequired").GetBoolean());

        // 32 zero bytes = issued without a photo: the server does not ask for one
        var now = await env.NowAsync();
        var raw = env.Phone.TicketBytes(1, 5, env.DriverId, now - 60, photoSha256: new byte[32]);
        var r = await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([new SyncTicket(Convert.ToBase64String(raw), 1, null, null)]));
        Assert.False(r.Results[0].PhotoWanted);
        var scan = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, TicketCodec.ToQrText(raw), null), T);
        Assert.False((await env.Gate.NamesAsync(scan.Ticket!)).PhotoExpected);
    }

    [Fact]
    public async Task Dashboard_counts_pending_trucks_and_previous_passages_are_listed()
    {
        var env = await NewEnvAsync();
        var now = await env.NowAsync();
        var blocks = await env.BlocksAsync();
        await env.Sync.SyncAsync(env.Phone.DeviceId, new SyncRequest([], [new SyncTruck(blocks[0][0], "22222 333 44", now, null, null)]));
        var first = await env.EnterAsync(1, 5, now - 2000);
        await env.ExitEmptyAsync(first);
        var second = await env.EnterAsync(2, 5, now - 900);

        var d = await env.Reports.DashboardAsync();
        Assert.Equal(1, d.TrucksAwaitingApproval);
        Assert.Equal(2, d.TripsToday);
        Assert.Equal(1, d.CompletedToday);
        Assert.Equal(1, d.UnclosedTrips);

        var passages = await env.Gate.PassagesAsync(5, second);
        Assert.Equal(first, Assert.Single(passages).TripId);
        var activity = await env.Receipts.ActivityAsync(24, 100);
        Assert.Contains(activity, x => x.Kind == "TRUCK" && x.Code == "REGISTERED");
        Assert.Contains(activity, x => x.Kind == "SCAN_EXIT");
    }
}
