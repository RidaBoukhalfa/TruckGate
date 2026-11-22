using System.Security.Cryptography;
using Dapper;
using TruckGate.Application;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

[Collection("db")]
public class GateFlowTests(DatabaseFixture fx)
{
    private static readonly Actor Gatekeeper = DatabaseFixture.Tester;

    private Task<ScanEntryResult> Scan(string qr) =>
        fx.Gate.ScanEntryAsync(new ScanEntryRequest(fx.GateId, qr, null), Gatekeeper);

    private Task<T> Scalar<T>(string sql, object? args = null) =>
        fx.Db.ReadAsync(c => c.ExecuteScalarAsync<T>(sql, args))!;

    [Fact]
    public async Task Valid_ticket_opens_a_trip_and_a_full_decision_enters_it()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();

        var scan = await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900));

        Assert.Equal("ACCEPT", scan.Outcome);
        Assert.Empty(scan.Reasons);
        Assert.NotNull(scan.TripId);
        Assert.Equal($"Q{fleet.Phone.DeviceId}-000001", scan.Ticket!.TicketId);
        Assert.InRange(scan.Ticket.AgeSeconds, 900, 960);

        var decision = await fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Full, null, null, Gatekeeper);

        Assert.Equal("ENTERED", decision.State);
        Assert.Equal("FULL", decision.LoadStatus);
        Assert.Null(decision.ReviewId);
        Assert.Equal("ENTERED", await Scalar<string>("SELECT current_state FROM tg.trips WHERE id = @id", new { id = scan.TripId }));
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM tg.trip_state_events WHERE trip_id = @id", new { id = scan.TripId }));
    }

    [Fact]
    public async Task Partial_load_is_accepted_with_its_status()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        var scan = await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900));

        var decision = await fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Partial, "half full", null, Gatekeeper);

        Assert.Equal("ENTERED", decision.State);
        Assert.Equal("PARTIAL", decision.LoadStatus);
    }

    [Fact]
    public async Task Replayed_ticket_is_rejected_and_raises_a_critical_alert()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        var qr = fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900);

        var first = await Scan(qr);
        var second = await Scan(qr);

        Assert.Equal("ACCEPT", first.Outcome);
        Assert.Equal("REJECT", second.Outcome);
        Assert.Equal(new[] { GateReasons.AlreadyUsed }, second.Reasons);
        Assert.Null(second.TripId);
        Assert.Equal(1L, await Scalar<long>(
            "SELECT count(*) FROM tg.trips WHERE ticket_device_id = @d", new { d = fleet.Phone.DeviceId }));
        Assert.Equal("CRITICAL", await Scalar<string>(
            "SELECT severity FROM tg.alerts WHERE type = 'GATE_ALREADY_USED' AND entity_id = @s",
            new { s = second.ScanId.ToString() }));
    }

    [Fact]
    public async Task Simultaneous_scans_of_one_ticket_create_exactly_one_trip()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        var qr = fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Scan(qr))));

        Assert.Single(results, r => r.Outcome != "REJECT");
        Assert.All(results.Where(r => r.Outcome == "REJECT"), r => Assert.Equal(new[] { GateReasons.AlreadyUsed }, r.Reasons));
        Assert.Equal(1L, await Scalar<long>(
            "SELECT count(*) FROM tg.trips WHERE ticket_device_id = @d", new { d = fleet.Phone.DeviceId }));
        Assert.Equal(8L, await Scalar<long>(
            "SELECT count(*) FROM tg.gate_scans WHERE raw_text = @qr", new { qr }));
    }

    [Fact]
    public async Task Forged_ticket_is_rejected_recorded_and_never_stored_as_a_ticket()
    {
        var fleet = await fx.NewFleetAsync();
        using var attacker = new QuarryPhone(fleet.Phone.DeviceId);
        var now = await fx.DbNowAsync();

        var scan = await Scan(attacker.Ticket(7, fleet.TruckId, fleet.DriverId, now - 900));

        Assert.Equal("REJECT", scan.Outcome);
        Assert.Equal(new[] { GateReasons.BadSignature }, scan.Reasons);
        Assert.Equal(0L, await Scalar<long>(
            "SELECT count(*) FROM tg.tickets WHERE device_id = @d", new { d = fleet.Phone.DeviceId }));
        Assert.Equal(1L, await Scalar<long>(
            "SELECT count(*) FROM tg.gate_scans WHERE id = @id AND ticket_device_id IS NULL", new { id = scan.ScanId }));
    }

    [Fact]
    public async Task Unknown_phone_is_rejected()
    {
        using var stranger = new QuarryPhone(fx.NextId());
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();

        var scan = await Scan(stranger.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900));

        Assert.Equal(new[] { GateReasons.UnknownDevice }, scan.Reasons);
    }

    [Fact]
    public async Task Same_truck_twice_within_twenty_minutes_is_rejected()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        var first = await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 1500));
        await fx.Gate.RecordEntryDecisionAsync(first.ScanId, EntryDecision.Full, null, null, Gatekeeper);

        var second = await Scan(fleet.Phone.Ticket(2, fleet.TruckId, fleet.DriverId, now - 700));

        Assert.Equal("REJECT", second.Outcome);
        Assert.Equal(new[] { GateReasons.TruckGap }, second.Reasons);
    }

    [Fact]
    public async Task Tickets_from_a_revoked_phone_are_rejected()
    {
        var fleet = await fx.NewFleetAsync();
        await fx.AdminSvc.RevokeDeviceAsync(fleet.Phone.DeviceId, "phone lost", Gatekeeper);
        var now = await fx.DbNowAsync();

        var scan = await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now + 60));

        Assert.Equal("REJECT", scan.Outcome);
        Assert.Equal(new[] { GateReasons.DeviceRevoked }, scan.Reasons);
        await Assert.ThrowsAsync<TruckGateException>(() =>
            fx.AdminSvc.RevokeDeviceAsync(fleet.Phone.DeviceId, "again", Gatekeeper));
    }

    [Fact]
    public async Task Early_ticket_goes_to_high_priority_review_after_the_decision()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();

        var scan = await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 60));
        Assert.Equal("PENDING", scan.Outcome);
        Assert.Equal(new[] { GateReasons.TooEarly }, scan.Reasons);
        Assert.NotNull(scan.TripId);

        var decision = await fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Full, null, null, Gatekeeper);

        Assert.Equal("PENDING_REVIEW", decision.State);
        Assert.Equal(new[] { GateReasons.TooEarly }, decision.ReviewReasons);
        Assert.Equal("HIGH", await Scalar<string>("SELECT priority FROM tg.reviews WHERE id = @id", new { id = decision.ReviewId }));
    }

    [Fact]
    public async Task Different_truck_decision_blocks_the_trip_and_alerts()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        var scan = await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900));

        var decision = await fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.DifferentTruck,
            "plate does not match", null, Gatekeeper);

        Assert.Equal("PENDING_REVIEW", decision.State);
        Assert.Equal(new[] { "GATE_DIFFERENT_TRUCK" }, decision.ReviewReasons);
        Assert.Equal("CRITICAL", await Scalar<string>(
            "SELECT severity FROM tg.alerts WHERE type = 'GATE_DIFFERENT_TRUCK' AND entity_id = @t",
            new { t = decision.TripId.ToString() }));
    }

    [Fact]
    public async Task A_decision_is_recorded_once_only()
    {
        var fleet = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        var scan = await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900));
        await fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Full, null, null, Gatekeeper);

        var e = await Assert.ThrowsAsync<TruckGateException>(() =>
            fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Partial, null, null, Gatekeeper));
        Assert.Equal("ALREADY_DECIDED", e.Code);
    }

    [Fact]
    public async Task A_rejected_scan_has_no_trip_to_decide()
    {
        var scan = await Scan("TG1:THIS-IS-NOT-A-TICKET");

        Assert.Equal(new[] { GateReasons.Malformed }, scan.Reasons);
        var e = await Assert.ThrowsAsync<TruckGateException>(() =>
            fx.Gate.RecordEntryDecisionAsync(scan.ScanId, EntryDecision.Full, null, null, Gatekeeper));
        Assert.Equal("SCAN_REJECTED", e.Code);
    }

    [Fact]
    public async Task Missing_ticket_numbers_are_reported()
    {
        var fleet = await fx.NewFleetAsync();
        var other = await fx.NewFleetAsync();
        var now = await fx.DbNowAsync();
        await Scan(fleet.Phone.Ticket(1, fleet.TruckId, fleet.DriverId, now - 900));
        await Scan(fleet.Phone.Ticket(4, other.TruckId, other.DriverId, now - 900));

        var gaps = await fx.Audit.SequenceGapsAsync();

        Assert.Contains(new SequenceGap(fleet.Phone.DeviceId, 2, 3), gaps);
    }

    [Fact]
    public async Task Enrollment_refuses_a_key_that_is_not_P256()
    {
        using var rsa = RSA.Create(2048);
        var e = await Assert.ThrowsAsync<TruckGateException>(() =>
            fx.AdminSvc.EnrollDeviceAsync(fx.NextId(), "QUARRY_PHONE", "bad", rsa.ExportSubjectPublicKeyInfo(), Gatekeeper));
        Assert.Equal("PUBLIC_KEY_INVALID", e.Code);
    }
}
