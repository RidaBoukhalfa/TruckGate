using System.Text.Json;
using Dapper;
using Npgsql;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Application;

public enum EntryDecision { Full, Partial, Empty, DifferentTruck, PlateUnreadable }

public sealed record ScanEntryRequest(short GateId, string QrText, int? GatekeeperId);

public sealed record TicketSummary(
    string TicketId, int DeviceId, long Seq, int TruckId, int DriverId, int OperatorId,
    long IssuedAt, long AgeSeconds, int Flags, string ShortCode);

public sealed record ScanEntryResult(
    long ScanId, long? TripId, string? TripNumber, string Outcome,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes, TicketSummary? Ticket);

/// <summary>What the gatekeeper reads on screen for a scanned ticket.</summary>
public sealed record TicketNames(string? FleetNumber, string? Plate, string? DriverName, bool DriverIsDefault,
    string? OperatorName);

public sealed record EntryDecisionResult(
    long TripId, string TripNumber, string State, string? LoadStatus, long? ReviewId, IReadOnlyList<string> ReviewReasons);

/// <summary>
/// Entry gate: verifies a scanned quarry ticket, records the scan (always, even when rejected),
/// opens the trip, then records the gatekeeper's decision.
/// </summary>
public sealed class GateService(Db db)
{
    private static readonly HashSet<string> NotAuthentic = new(StringComparer.Ordinal)
    {
        GateReasons.Malformed, GateReasons.UnknownDevice, GateReasons.WrongDeviceKind, GateReasons.BadSignature,
    };

    private static readonly Dictionary<string, string> AlertSeverity = new(StringComparer.Ordinal)
    {
        [GateReasons.BadSignature] = "CRITICAL",
        [GateReasons.UnknownDevice] = "CRITICAL",
        [GateReasons.WrongDeviceKind] = "CRITICAL",
        [GateReasons.AlreadyUsed] = "CRITICAL",
        [GateReasons.Voided] = "CRITICAL",
        [GateReasons.DeviceRevoked] = "CRITICAL",
        [GateReasons.TruckGap] = "MEDIUM",
        [GateReasons.IssuedInFuture] = "MEDIUM",
        [GateReasons.TooEarly] = "MEDIUM",
        [GateReasons.ClockMismatch] = "MEDIUM",
        [GateReasons.IssuedBeforeRevocation] = "MEDIUM",
        [GateReasons.OutsideGeofence] = "MEDIUM",
        [GateReasons.WrongKind] = "MEDIUM",
    };

    private static readonly HashSet<string> HighPriorityReasons = new(StringComparer.Ordinal)
    {
        GateReasons.IssuedInFuture, GateReasons.TooEarly, GateReasons.ClockMismatch,
        GateReasons.IssuedBeforeRevocation, GateReasons.OutsideGeofence,
        "GATE_EMPTY", "GATE_DIFFERENT_TRUCK",
    };

    public Task<ScanEntryResult> ScanEntryAsync(ScanEntryRequest req, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.QrText) || req.QrText.Length > 512)
            throw TruckGateException.Validation("QR_INVALID", "Scanned text is empty or too long.");

        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var gateExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM tg.gates WHERE id = @g)", new { g = (int)req.GateId }, tx);
            if (!gateExists) throw TruckGateException.NotFound("GATE_NOT_FOUND", $"Gate {req.GateId} does not exist.");

            // Server time is the only clock used for decisions.
            long now = await conn.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint", transaction: tx);

            var decoded = TryDecode(req.QrText);
            var lookup = PreloadedLookup.Empty;
            if (decoded is not null)
            {
                // Serialize concurrent scans of the same ticket and of the same truck (fixed order: no deadlock).
                await LockAsync(conn, tx, "ticket:" + decoded.Fields.TicketId);
                await LockAsync(conn, tx, "truck:" + decoded.Fields.TruckId);
                lookup = await PreloadedLookup.LoadAsync(conn, tx, decoded.Fields);
            }

            var parameters = await Settings.LoadGatePolicyAsync(conn, tx);
            var result = GatePolicy.Evaluate(req.QrText, now, lookup, parameters);
            var outcome = result.Outcome.ToString().ToUpperInvariant();
            bool authentic = decoded is not null && !result.Reasons.Any(NotAuthentic.Contains);

            if (authentic) await InsertTicketIfNewAsync(conn, tx, decoded!, "GATE");

            long scanId = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO tg.gate_scans (gate_id, direction, scan_type, raw_text, ticket_device_id, ticket_seq,
                                           outcome, reasons, notes, gatekeeper_id)
                VALUES (@gate, 'ENTRY', 'QUARRY_TICKET', @raw, @dev, @seq, @outcome, @reasons, @notes, @gk)
                RETURNING id
                """,
                new
                {
                    gate = (int)req.GateId,
                    raw = req.QrText,
                    dev = authentic ? (int?)decoded!.Fields.DeviceId : null,
                    seq = authentic ? (long?)decoded!.Fields.Seq : null,
                    outcome,
                    reasons = result.Reasons.ToArray(),
                    notes = result.Notes.ToArray(),
                    gk = req.GatekeeperId,
                }, tx);

            long? tripId = null;
            string? tripNumber = null;
            if (result.Outcome != GateOutcome.Reject && authentic)
            {
                var f = decoded!.Fields;
                var trip = await conn.QuerySingleAsync<(long Id, string Number)>("""
                    INSERT INTO tg.trips (ticket_device_id, ticket_seq, truck_id, driver_id, entry_scan_id)
                    VALUES (@dev, @seq, @truck, @driver, @scan)
                    RETURNING id, trip_number
                    """,
                    new { dev = (int)f.DeviceId, seq = (long)f.Seq, truck = (int)f.TruckId, driver = (int)f.DriverId, scan = scanId },
                    tx);
                await conn.ExecuteAsync("""
                    INSERT INTO tg.trip_state_events (trip_id, from_state, to_state, reason_code)
                    VALUES (@t, NULL, 'AT_GATE', @r)
                    """, new { t = trip.Id, r = "SCAN_" + outcome }, tx);
                tripId = trip.Id;
                tripNumber = trip.Number;
            }

            foreach (var reason in result.Reasons)
            {
                if (!AlertSeverity.TryGetValue(reason, out var severity)) continue;
                await InsertAlertAsync(conn, tx, "GATE_" + reason, severity, "gate_scans", scanId.ToString(),
                    new { ticket_id = result.TicketId, gate_id = req.GateId, reasons = result.Reasons });
            }

            TicketSummary? summary = null;
            if (decoded is not null)
            {
                var f = decoded.Fields;
                var bytes = decoded.SignedPart.Concat(decoded.Signature).ToArray();
                summary = new TicketSummary(f.TicketId, f.DeviceId, f.Seq, f.TruckId, f.DriverId, f.OperatorId,
                    f.IssuedAt, now - f.IssuedAt, f.Flags, TicketCodec.ShortCode(bytes));
            }

            return new ScanEntryResult(scanId, tripId, tripNumber, outcome, result.Reasons, result.Notes, summary);
        }, ct);
    }

    public Task<EntryDecisionResult> RecordEntryDecisionAsync(long scanId, EntryDecision decision, string? note,
        int? decidedBy, Actor actor, CancellationToken ct = default)
    {
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var scan = await conn.QuerySingleOrDefaultAsync<ScanRow>(
                "SELECT id, direction, outcome, reasons FROM tg.gate_scans WHERE id = @id", new { id = scanId }, tx)
                ?? throw TruckGateException.NotFound("SCAN_NOT_FOUND", $"Scan {scanId} does not exist.");
            if (scan.Direction != "ENTRY")
                throw TruckGateException.Validation("NOT_AN_ENTRY_SCAN", "This scan is not an entry scan.");

            var trip = await conn.QuerySingleOrDefaultAsync<TripRow>(
                "SELECT id, trip_number, current_state FROM tg.trips WHERE entry_scan_id = @id FOR UPDATE",
                new { id = scanId }, tx)
                ?? throw TruckGateException.Conflict("SCAN_REJECTED", "This scan was rejected; there is no trip to decide.");
            if (trip.CurrentState != "AT_GATE")
                throw TruckGateException.Conflict("ALREADY_DECIDED", $"Trip {trip.TripNumber} is already {trip.CurrentState}.");

            var (dbDecision, load, gateReason) = decision switch
            {
                EntryDecision.Full => ("FULL", (string?)"FULL", (string?)null),
                EntryDecision.Partial => ("PARTIAL", "PARTIAL", null),
                EntryDecision.Empty => ("EMPTY", "EMPTY", "GATE_EMPTY"),
                EntryDecision.DifferentTruck => ("DIFFERENT_TRUCK", null, "GATE_DIFFERENT_TRUCK"),
                EntryDecision.PlateUnreadable => ("PLATE_UNREADABLE", null, "GATE_PLATE_UNREADABLE"),
                _ => throw TruckGateException.Validation("DECISION_INVALID", "Unknown decision."),
            };

            await conn.ExecuteAsync("""
                INSERT INTO tg.gate_decisions (gate_scan_id, decision, decided_by, note)
                VALUES (@scan, @decision, @by, @note)
                """, new { scan = scanId, decision = dbDecision, by = decidedBy, note }, tx);

            if (load is not null)
                await conn.ExecuteAsync("UPDATE tg.trips SET load_status = @load WHERE id = @id",
                    new { load, id = trip.Id }, tx);

            var reasons = new List<string>();
            if (scan.Outcome == "PENDING") reasons.AddRange(scan.Reasons);
            if (gateReason is not null) reasons.Add(gateReason);

            long? reviewId = null;
            string state;
            if (reasons.Count == 0)
            {
                state = "ENTERED";
                await InsertStateEventAsync(conn, tx, trip.Id, "AT_GATE", state, "GATE_" + dbDecision, note);
            }
            else
            {
                state = "PENDING_REVIEW";
                await InsertStateEventAsync(conn, tx, trip.Id, "AT_GATE", state, reasons[0], note);
                var priority = reasons.Any(HighPriorityReasons.Contains) ? "HIGH" : "NORMAL";
                reviewId = await conn.ExecuteScalarAsync<long>("""
                    INSERT INTO tg.reviews (trip_id, reason_codes, priority) VALUES (@trip, @reasons, @priority)
                    RETURNING id
                    """, new { trip = trip.Id, reasons = reasons.ToArray(), priority }, tx);
            }

            if (decision == EntryDecision.DifferentTruck)
                await InsertAlertAsync(conn, tx, "GATE_DIFFERENT_TRUCK", "CRITICAL", "trips", trip.Id.ToString(),
                    new { trip_number = trip.TripNumber, scan_id = scanId });
            else if (decision == EntryDecision.Empty)
                await InsertAlertAsync(conn, tx, "GATE_EMPTY_ON_ENTRY", "MEDIUM", "trips", trip.Id.ToString(),
                    new { trip_number = trip.TripNumber, scan_id = scanId });

            return new EntryDecisionResult(trip.Id, trip.TripNumber, state, load, reviewId, reasons);
        }, ct);
    }

    /// <summary>Fleet number, current plate, driver and quarry operator names for a ticket.</summary>
    public Task<TicketNames> NamesAsync(TicketSummary t, CancellationToken ct = default) =>
        db.ReadAsync(conn => conn.QuerySingleAsync<TicketNames>("""
            SELECT (SELECT fleet_number FROM tg.trucks WHERE id = @truck) AS fleetnumber,
                   (SELECT plate_text FROM tg.truck_plates WHERE truck_id = @truck AND valid_to IS NULL
                    ORDER BY valid_from DESC LIMIT 1) AS plate,
                   (SELECT full_name FROM tg.drivers WHERE id = @driver) AS drivername,
                   coalesce((SELECT default_driver_id = @driver FROM tg.trucks WHERE id = @truck), false) AS driverisdefault,
                   (SELECT full_name FROM tg.users WHERE id = @op) AS operatorname
            """, new { truck = t.TruckId, driver = t.DriverId, op = t.OperatorId }), ct);

    // ------------------------------------------------------------------ helpers

    private static DecodedTicket? TryDecode(string qrText)
    {
        try
        {
            return TicketCodec.Decode(TicketCodec.FromQrText(qrText));
        }
        catch (TicketFormatException)
        {
            return null;
        }
    }

    private static Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key) =>
        conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = key }, tx);

    internal static Task InsertTicketIfNewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DecodedTicket t, string via)
    {
        var f = t.Fields;
        return conn.ExecuteAsync("""
            INSERT INTO tg.tickets (device_id, seq, kind, issued_at, clock_delta, truck_id, driver_id, operator_id,
                                    lat_e6, lon_e6, flags, config_version, photo_sha256, raw, first_seen_via)
            VALUES (@dev, @seq, @kind, to_timestamp(@issued), @delta, @truck, @driver, @op,
                    @lat, @lon, @flags, @cfg, @photo, @raw, @via)
            ON CONFLICT (device_id, seq) DO NOTHING
            """,
            new
            {
                dev = (int)f.DeviceId, seq = (long)f.Seq, kind = (short)f.Kind, issued = (long)f.IssuedAt,
                delta = f.ClockDelta, truck = (int)f.TruckId, driver = (int)f.DriverId, op = (int)f.OperatorId,
                lat = f.LatE6, lon = f.LonE6, flags = (short)f.Flags, cfg = (int)f.ConfigVersion,
                photo = f.PhotoSha256, raw = t.SignedPart.Concat(t.Signature).ToArray(), via,
            }, tx);
    }

    private static Task InsertStateEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long tripId,
        string from, string to, string reason, string? note) =>
        conn.ExecuteAsync("""
            INSERT INTO tg.trip_state_events (trip_id, from_state, to_state, reason_code, note)
            VALUES (@trip, @from, @to, @reason, @note)
            """, new { trip = tripId, from, to, reason, note }, tx);

    internal static Task InsertAlertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, string severity,
        string entity, string entityId, object parameters) =>
        conn.ExecuteAsync("""
            INSERT INTO tg.alerts (type, severity, entity, entity_id, params)
            VALUES (@type, @severity, @entity, @entityId, CAST(@params AS jsonb))
            """,
            new { type, severity, entity, entityId, @params = JsonSerializer.Serialize(parameters) }, tx);

    private sealed class ScanRow
    {
        public long Id { get; set; }
        public string Direction { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string[] Reasons { get; set; } = [];
    }

    private sealed class TripRow
    {
        public long Id { get; set; }
        public string TripNumber { get; set; } = "";
        public string CurrentState { get; set; } = "";
    }

    /// <summary>Answers the policy's questions for one ticket, preloaded inside the locked transaction.</summary>
    private sealed class PreloadedLookup : IGateLookup
    {
        public static readonly PreloadedLookup Empty = new();

        private RegisteredDevice? _device;
        private TruckState? _truck;
        private string? _ticketId;
        private bool _used;
        private bool _voided;

        public static async Task<PreloadedLookup> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction tx, TicketFields f)
        {
            var l = new PreloadedLookup { _ticketId = f.TicketId };

            var dev = await conn.QuerySingleOrDefaultAsync<DeviceRow>("""
                SELECT id, kind, public_key_spki, floor(extract(epoch FROM revoked_at))::bigint AS revoked_at
                FROM tg.devices WHERE id = @id
                """, new { id = (int)f.DeviceId }, tx);
            if (dev is not null)
                l._device = new RegisteredDevice((ushort)dev.Id,
                    dev.Kind == "QUARRY_PHONE" ? DeviceKind.QuarryPhone : DeviceKind.GateStation,
                    dev.PublicKeySpki, dev.RevokedAt);

            var truck = await conn.QuerySingleOrDefaultAsync<TruckRow>("""
                SELECT t.id, t.active,
                       (SELECT extract(epoch FROM max(gs.scanned_at))::bigint
                        FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                        WHERE tr.truck_id = t.id AND tr.current_state <> 'REJECTED'
                          -- only entries made with other tickets: a replay is ALREADY_USED, not a truck gap
                          AND NOT (tr.ticket_device_id = @d AND tr.ticket_seq = @s)) AS last_entry_at
                FROM tg.trucks t WHERE t.id = @id
                """, new { id = (int)f.TruckId, d = (int)f.DeviceId, s = (long)f.Seq }, tx);
            if (truck is not null)
                l._truck = new TruckState((ushort)truck.Id, truck.Active, truck.LastEntryAt);

            var args = new { d = (int)f.DeviceId, s = (long)f.Seq };
            l._used = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM tg.trips WHERE ticket_device_id = @d AND ticket_seq = @s)", args, tx);
            l._voided = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM tg.ticket_events WHERE device_id = @d AND seq = @s AND type = 'VOIDED')",
                args, tx);
            return l;
        }

        public RegisteredDevice? FindDevice(ushort deviceId) => _device?.DeviceId == deviceId ? _device : null;
        public TruckState? FindTruck(ushort truckId) => _truck?.TruckId == truckId ? _truck : null;
        public bool IsUsed(string ticketId) => _used && ticketId == _ticketId;
        public bool IsVoided(string ticketId) => _voided && ticketId == _ticketId;

        private sealed class DeviceRow
        {
            public int Id { get; set; }
            public string Kind { get; set; } = "";
            public byte[] PublicKeySpki { get; set; } = [];
            public long? RevokedAt { get; set; }
        }

        private sealed class TruckRow
        {
            public int Id { get; set; }
            public bool Active { get; set; }
            public long? LastEntryAt { get; set; }
        }
    }
}
