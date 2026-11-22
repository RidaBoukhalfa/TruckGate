using Dapper;
using Npgsql;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Application;

public enum ExitDecision { Empty, Loaded }

public sealed record TripCard(long TripId, string TripNumber, string State, int TruckId, string? FleetNumber, string? Plate,
    string? DriverName, string TicketId, DateTime EnteredAt, string? LoadStatus, string? EntryDecision);

public sealed record ScanExitResult(long ScanId, string Outcome, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes,
    TripCard? Trip);

public sealed record ExitDecisionResult(long TripId, string TripNumber, string State, long? ReviewId, IReadOnlyList<string> Reasons);

public sealed record ReceiptData(long TripId, string TripNumber, string QrText, string? FleetNumber, string? Plate,
    string? DriverName, string GateName, DateTime EnteredAt, string? LoadStatus, int PrintCount);

/// <summary>
/// Exit gate: the driver shows the receipt printed at entry (TR1, signed by the server key); the
/// cameras take the exit pictures; the gatekeeper confirms the truck leaves empty. A loaded exit, an
/// exit too soon after entry, or a truck that never comes out sends the trip to the supervisor.
/// </summary>
public sealed class ExitService(Db db, ServerSigningKey serverKey)
{
    public const string ReceiptMissing = "RECEIPT_MISSING";

    public Task<ScanExitResult> ScanReceiptAsync(short gateId, string qrText, int? gatekeeperId, Actor actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(qrText) || qrText.Length > 512)
            throw TruckGateException.Validation("QR_INVALID", "Scanned text is empty or too long.");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await GateService.EnsureGateAsync(conn, tx, gateId);
            var decoded = GateReceipt.TryVerify(qrText, serverKey.PublicKeySpki, out var failure);
            if (decoded is null)
            {
                long rejected = await GateService.InsertScanAsync(conn, tx, gateId, "EXIT", "GATE_RECEIPT", qrText,
                    null, null, "REJECT", [failure!], [], gatekeeperId);
                if (failure == GateReasons.BadSignature)
                    await GateService.InsertAlertAsync(conn, tx, "GATE_FORGED_RECEIPT", "CRITICAL", "gate_scans",
                        rejected.ToString(), new { gate_id = gateId });
                return new ScanExitResult(rejected, "REJECT", [failure!], [], null);
            }
            return await ExitCoreAsync(conn, tx, gateId, (long)decoded.Fields.TripId, decoded.Fields, "GATE_RECEIPT",
                qrText, [], gatekeeperId);
        }, ct);
    }

    /// <summary>Receipt lost: the gatekeeper picks the truck among the trucks inside.</summary>
    public Task<ScanExitResult> ScanManualAsync(short gateId, long tripId, int? gatekeeperId, Actor actor,
        CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await GateService.EnsureGateAsync(conn, tx, gateId);
            return await ExitCoreAsync(conn, tx, gateId, tripId, null, "MANUAL_SELECT", $"TRIP:{tripId}",
                [ReceiptMissing], gatekeeperId);
        }, ct);

    private static async Task<ScanExitResult> ExitCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, short gateId,
        long tripId, ReceiptFields? receipt, string scanType, string rawText, IReadOnlyList<string> notes, int? gatekeeperId)
    {
        var trip = await conn.QuerySingleOrDefaultAsync<Trips.TripLock>("""
            SELECT id, trip_number, current_state, truck_id, driver_id, ticket_device_id, ticket_seq,
                   entry_scan_id, exit_scan_id, load_status
            FROM tg.trips WHERE id = @tripId FOR UPDATE
            """, new { tripId }, tx);

        var reject = new List<string>();
        var pending = new List<string>();
        if (trip is null || (receipt is not null && (receipt.TruckId != trip.TruckId
                || receipt.TicketDeviceId != trip.TicketDeviceId || receipt.TicketSeq != trip.TicketSeq)))
            reject.Add("RECEIPT_MISMATCH");
        else if (trip.ExitScanId is not null)
            reject.Add("ALREADY_EXITED");
        else if (trip.CurrentState == "AT_GATE")
            reject.Add("ENTRY_NOT_DECIDED");
        else
        {
            var settings = await SettingsBag.LoadAsync(conn, tx);
            var insideSeconds = await conn.ExecuteScalarAsync<long>("""
                SELECT extract(epoch FROM now() - gs.scanned_at)::bigint FROM tg.gate_scans gs WHERE gs.id = @id
                """, new { id = trip.EntryScanId }, tx);
            if (insideSeconds < settings.Int("gate.min_unload_s", 180)) pending.Add("EXIT_TOO_SOON");
        }

        var outcome = reject.Count > 0 ? "REJECT" : pending.Count > 0 ? "PENDING" : "ACCEPT";
        var reasons = reject.Count > 0 ? reject : pending;
        long scanId = await GateService.InsertScanAsync(conn, tx, gateId, "EXIT", scanType, rawText,
            trip?.TicketDeviceId, trip?.TicketSeq, outcome, reasons, notes, gatekeeperId);

        if (reject.Count == 0)
            await conn.ExecuteAsync("UPDATE tg.trips SET exit_scan_id = @scanId WHERE id = @id", new { scanId, id = trip!.Id }, tx);
        else if (reject[0] is "RECEIPT_MISMATCH" or "ALREADY_EXITED" && receipt is not null)
            await GateService.InsertAlertAsync(conn, tx, "GATE_" + reject[0], "CRITICAL", "gate_scans", scanId.ToString(),
                new { trip_id = tripId, gate_id = gateId });

        var card = trip is null ? null : await CardAsync(conn, tx, trip.Id);
        return new ScanExitResult(scanId, outcome, reasons, notes, card);
    }

    public Task<ExitDecisionResult> RecordExitDecisionAsync(long scanId, ExitDecision decision, string? note, int? decidedBy,
        Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var scan = await conn.QuerySingleOrDefaultAsync<(string Direction, string Outcome, string[] Reasons)>(
                "SELECT direction, outcome, reasons FROM tg.gate_scans WHERE id = @scanId", new { scanId }, tx);
            if (scan.Direction is null) throw TruckGateException.NotFound("SCAN_NOT_FOUND", $"Scan {scanId} does not exist.");
            if (scan.Direction != "EXIT") throw TruckGateException.Validation("NOT_AN_EXIT_SCAN", "This scan is not an exit scan.");
            var tripId = await conn.ExecuteScalarAsync<long?>("SELECT id FROM tg.trips WHERE exit_scan_id = @scanId", new { scanId }, tx)
                ?? throw TruckGateException.Conflict("SCAN_REJECTED", "This exit scan was rejected; there is no trip to decide.");
            var trip = await Trips.LockAsync(conn, tx, tripId);
            if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.gate_decisions WHERE gate_scan_id = @scanId)",
                    new { scanId }, tx))
                throw TruckGateException.Conflict("ALREADY_DECIDED", $"Exit of {trip.TripNumber} is already recorded.");

            var dbDecision = decision == ExitDecision.Empty ? "EXIT_EMPTY" : "EXIT_LOADED";
            await conn.ExecuteAsync("""
                INSERT INTO tg.gate_decisions (gate_scan_id, decision, decided_by, note) VALUES (@scanId, @dbDecision, @decidedBy, @note)
                """, new { scanId, dbDecision, decidedBy, note }, tx);

            var reasons = new List<string>();
            if (scan.Outcome == "PENDING") reasons.AddRange(scan.Reasons);
            if (decision == ExitDecision.Loaded) reasons.Add("EXIT_LOADED");

            long? reviewId = null;
            var state = trip.CurrentState;
            if (state == "ENTERED")
            {
                if (reasons.Count == 0)
                {
                    await Trips.TransitionAsync(conn, tx, trip.Id, "ENTERED", "EXITED_EMPTY", "GATE_EXIT_EMPTY", note);
                    state = "EXITED_EMPTY";
                    if (!await Trips.HasBlockingReviewAsync(conn, tx, trip.Id))
                    {
                        await Trips.TransitionAsync(conn, tx, trip.Id, "EXITED_EMPTY", "CLOSED", "AUTO_CLOSE");
                        state = "CLOSED";
                    }
                }
                else
                {
                    await Trips.TransitionAsync(conn, tx, trip.Id, "ENTERED", "PENDING_REVIEW", reasons[0], note);
                    state = "PENDING_REVIEW";
                    reviewId = await Trips.OpenReviewAsync(conn, tx, trip.Id, reasons);
                }
            }
            else if (reasons.Count > 0 && state == "PENDING_REVIEW")
            {
                reviewId = await Trips.OpenReviewAsync(conn, tx, trip.Id, reasons);
            }

            if (decision == ExitDecision.Loaded)
                await GateService.InsertAlertAsync(conn, tx, "GATE_EXIT_LOADED", "CRITICAL", "trips", trip.Id.ToString(),
                    new { trip_number = trip.TripNumber, scan_id = scanId });

            return new ExitDecisionResult(trip.Id, trip.TripNumber, state, reviewId, reasons);
        }, ct);

    /// <summary>Trucks whose entry was decided and that have not come out yet.</summary>
    public Task<IReadOnlyList<TripCard>> InsideAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<TripCard>>(async conn =>
            (await conn.QueryAsync<TripCard>(CardSql + """
                WHERE tr.exit_scan_id IS NULL AND tr.current_state NOT IN ('AT_GATE', 'NEW')
                  AND gs.scanned_at > now() - interval '3 days'
                ORDER BY gs.scanned_at
                """)).ToList(), ct);

    /// <summary>Signed receipt for a trip whose entry was decided; every print is recorded.</summary>
    public Task<ReceiptData> PrintReceiptAsync(long tripId, int? printedBy, Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<ReceiptRow>("""
                SELECT tr.id, tr.trip_number, tr.current_state, tr.truck_id, tr.ticket_device_id, tr.ticket_seq,
                       tr.load_status, gs.gate_id, g.name AS gate_name, gs.scanned_at AS entered_at,
                       floor(extract(epoch FROM gs.scanned_at))::bigint AS entered_unix
                FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id JOIN tg.gates g ON g.id = gs.gate_id
                WHERE tr.id = @tripId
                """, new { tripId }, tx)
                ?? throw TruckGateException.NotFound("TRIP_NOT_FOUND", $"Trip {tripId} does not exist.");
            if (row.CurrentState == "AT_GATE")
                throw TruckGateException.Conflict("ENTRY_NOT_DECIDED", "Record the entry decision before printing the receipt.");

            byte load = row.LoadStatus switch
            {
                "FULL" => ReceiptFields.LoadFull,
                "PARTIAL" => ReceiptFields.LoadPartial,
                "EMPTY" => ReceiptFields.LoadEmpty,
                _ => ReceiptFields.LoadUnknown,
            };
            var fields = new ReceiptFields((uint)row.Id, (ushort)row.GateId, (uint)row.EnteredUnix, (ushort)row.TruckId,
                (ushort)row.TicketDeviceId, (uint)row.TicketSeq, load);
            var qr = GateReceipt.ToQrText(GateReceipt.Build(fields, serverKey.Sign));

            await conn.ExecuteAsync("INSERT INTO tg.receipt_prints (trip_id, printed_by) VALUES (@tripId, @printedBy)",
                new { tripId, printedBy }, tx);
            var count = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM tg.receipt_prints WHERE trip_id = @tripId",
                new { tripId }, tx);
            var card = await CardAsync(conn, tx, tripId);
            return new ReceiptData(row.Id, row.TripNumber, qr, card?.FleetNumber, card?.Plate, card?.DriverName,
                row.GateName, row.EnteredAt, row.LoadStatus, count);
        }, ct);

    internal const string CardSql = """
        SELECT tr.id AS tripid, tr.trip_number AS tripnumber, tr.current_state AS state, tr.truck_id AS truckid,
               t.fleet_number AS fleetnumber,
               (SELECT p.plate_text FROM tg.truck_plates p WHERE p.truck_id = tr.truck_id AND p.valid_to IS NULL
                ORDER BY p.valid_from DESC LIMIT 1) AS plate,
               d.full_name AS drivername,
               'Q' || tr.ticket_device_id || '-' || tg.fmt_seq(tr.ticket_seq) AS ticketid,
               gs.scanned_at AS enteredat, tr.load_status AS loadstatus,
               (SELECT gd.decision FROM tg.gate_decisions gd WHERE gd.gate_scan_id = tr.entry_scan_id) AS entrydecision
        FROM tg.trips tr
        JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
        LEFT JOIN tg.trucks t ON t.id = tr.truck_id
        LEFT JOIN tg.drivers d ON d.id = tr.driver_id

        """;

    internal static Task<TripCard?> CardAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long tripId) =>
        conn.QuerySingleOrDefaultAsync<TripCard>(CardSql + "WHERE tr.id = @tripId", new { tripId }, tx);

    private sealed class ReceiptRow
    {
        public long Id { get; set; }
        public string TripNumber { get; set; } = "";
        public string CurrentState { get; set; } = "";
        public int TruckId { get; set; }
        public int TicketDeviceId { get; set; }
        public long TicketSeq { get; set; }
        public string? LoadStatus { get; set; }
        public short GateId { get; set; }
        public string GateName { get; set; } = "";
        public DateTime EnteredAt { get; set; }
        public long EnteredUnix { get; set; }
    }
}
