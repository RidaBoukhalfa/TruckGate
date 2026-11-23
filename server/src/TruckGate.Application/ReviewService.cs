using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using TruckGate.Data;

namespace TruckGate.Application;

public enum ReviewDecision { Accept, AcceptPartial, Reject }

/// <summary>Mutable for Dapper (PostgreSQL arrays are not matched to constructor parameters).</summary>
public sealed class ReviewQueueItem
{
    public long TripId { get; set; }
    public string TripNumber { get; set; } = "";
    public string State { get; set; } = "";
    public string? FleetNumber { get; set; }
    public string? Plate { get; set; }
    public string? DriverName { get; set; }
    public DateTime EnteredAt { get; set; }
    public bool Exited { get; set; }
    public string Priority { get; set; } = "";
    public bool IsSample { get; set; }
    public string[] Reasons { get; set; } = [];
    public DateTime OpenedAt { get; set; }
    public bool Investigating { get; set; }
    public string? LastNote { get; set; }
    public string? TruckStatus { get; set; }
}

/// <summary>Mutable for Dapper (PostgreSQL arrays are not matched to constructor parameters).</summary>
public sealed class ScanDetail
{
    public long ScanId { get; set; }
    public string Direction { get; set; } = "";
    public string ScanType { get; set; } = "";
    public DateTime ScannedAt { get; set; }
    public string Outcome { get; set; } = "";
    public string[] Reasons { get; set; } = [];
    public string[] Notes { get; set; } = [];
    public string? GatekeeperName { get; set; }
    public string? Decision { get; set; }
    public string? DecisionNote { get; set; }
    public string? GateName { get; set; }
}

public sealed record SnapshotDetail(long Id, long ScanId, int CameraId, string CameraLabel, string Role, int FrameIndex,
    DateTime CapturedAt, string? LoadLabel, string? PlateLabel, string? QualityLabel);

/// <summary>Mutable for Dapper (PostgreSQL arrays are not matched to constructor parameters).</summary>
public sealed class ReviewRecord
{
    public long Id { get; set; }
    public string[] Reasons { get; set; } = [];
    public string Priority { get; set; } = "";
    public bool IsSample { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Decision { get; set; }
    public string? DecisionReason { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
}

public sealed record StateEventRecord(string? FromState, string ToState, string ReasonCode, string Actor, DateTime At, string? Note);

public sealed record TicketDetail(string TicketId, DateTime IssuedAt, int ClockDelta, int Flags, double? Lat, double? Lon,
    int ConfigVersion, string? OperatorName, string FirstSeenVia, bool HasPhoto, string? DriverChangeReason, bool Voided);

public sealed record ReviewNoteItem(long Id, string Kind, string Note, string? CreatedByName, DateTime CreatedAt);

/// <summary>Mutable for Dapper (PostgreSQL arrays are not matched to constructor parameters).</summary>
public sealed class TripAnnotationItem
{
    public long Id { get; set; }
    public string[] Labels { get; set; } = [];
    public string? Note { get; set; }
    public string? AnnotatedByName { get; set; }
    public DateTime AnnotatedAt { get; set; }
}

public sealed record VideoItem(long Id, long ScanId, int CameraId, string CameraLabel, string Direction, string Status,
    DateTime ScanAt, DateTime WindowStart, DateTime WindowEnd, int? DurationMs, long? SizeBytes, string? Error);

public sealed record TruckSummary(int Id, string FleetNumber, string ApprovalStatus, string? OwnerLastName, string? OwnerFirstName,
    string? ReportedPlate, int? MergedInto);

public sealed record TripDetail(TripCard Trip, TicketDetail? Ticket, IReadOnlyList<ScanDetail> Scans,
    IReadOnlyList<SnapshotDetail> Snapshots, IReadOnlyList<ReviewRecord> Reviews, IReadOnlyList<StateEventRecord> Events,
    int ReceiptPrints, IReadOnlyList<ReviewNoteItem> Notes, IReadOnlyList<TripAnnotationItem> Annotations,
    IReadOnlyList<VideoItem> Videos, TruckSummary? Truck);

public sealed record ReviewDecisionResult(long TripId, string TripNumber, string State, int ReviewsClosed);

/// <summary>
/// Supervisor review: one decision per trip closes all its open reviews and moves the trip; picture
/// labels are kept (latest wins, history stays) to train the cameras' AI later.
/// </summary>
public sealed class ReviewService(Db db, StorageOptions storage)
{
    public static readonly string[] LoadLabels = ["FULL", "PARTIAL", "EMPTY", "NOT_VISIBLE"];
    public static readonly string[] QualityLabels = ["GOOD", "BLURRED", "DARK", "GLARE", "OBSTRUCTED"];
    public static readonly string[] TripLabels = ["NORMAL_TRIP", "EMPTY_TRUCK", "LOADED_TRUCK", "INCOMPLETE_CARGO", "WRONG_TRUCK",
        "PLATE_UNREADABLE", "POSSIBLE_MISMATCH", "CAMERA_FAILURE", "NEEDS_REVIEW"];

    public Task<IReadOnlyList<ReviewQueueItem>> QueueAsync(bool includeSamples, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<ReviewQueueItem>>(async conn => (await conn.QueryAsync<ReviewQueueItem>("""
            SELECT tr.id AS tripid, tr.trip_number AS tripnumber, tr.current_state AS state, t.fleet_number AS fleetnumber,
                   (SELECT p.plate_text FROM tg.truck_plates p WHERE p.truck_id = tr.truck_id AND p.valid_to IS NULL
                    ORDER BY p.valid_from DESC LIMIT 1) AS plate,
                   d.full_name AS drivername, gs.scanned_at AS enteredat, tr.exit_scan_id IS NOT NULL AS exited,
                   CASE min(CASE r.priority WHEN 'HIGH' THEN 1 WHEN 'NORMAL' THEN 2 ELSE 3 END)
                        WHEN 1 THEN 'HIGH' WHEN 2 THEN 'NORMAL' ELSE 'LOW' END AS priority,
                   bool_and(r.is_random_sample) AS issample,
                   array_agg(DISTINCT rc) AS reasons,
                   min(r.created_at) AS openedat,
                   EXISTS (SELECT 1 FROM tg.review_notes n WHERE n.trip_id = tr.id AND n.kind = 'INVESTIGATE'
                           AND n.created_at >= (SELECT min(r2.created_at) FROM tg.reviews r2
                                                WHERE r2.trip_id = tr.id AND r2.decision IS NULL)) AS investigating,
                   (SELECT n.note FROM tg.review_notes n WHERE n.trip_id = tr.id ORDER BY n.id DESC LIMIT 1) AS lastnote,
                   t.approval_status AS truckstatus
            FROM tg.reviews r
            CROSS JOIN LATERAL unnest(r.reason_codes) AS rc
            JOIN tg.trips tr ON tr.id = r.trip_id
            JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
            LEFT JOIN tg.trucks t ON t.id = tr.truck_id
            LEFT JOIN tg.drivers d ON d.id = tr.driver_id
            WHERE r.decision IS NULL AND (@includeSamples OR NOT r.is_random_sample)
            GROUP BY tr.id, t.fleet_number, t.approval_status, d.full_name, gs.scanned_at
            ORDER BY bool_and(r.is_random_sample), min(CASE r.priority WHEN 'HIGH' THEN 1 WHEN 'NORMAL' THEN 2 ELSE 3 END),
                     min(r.created_at)
            LIMIT 500
            """, new { includeSamples })).ToList(), ct);

    public Task<TripDetail> TripAsync(long tripId, CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var card = await ExitService.CardAsync(conn, null, tripId)
                ?? throw TruckGateException.NotFound("TRIP_NOT_FOUND", $"Trip {tripId} does not exist.");
            var ticket = await conn.QuerySingleOrDefaultAsync<TicketRow>("""
                SELECT tk.ticket_id, tk.issued_at, tk.clock_delta, tk.flags, tk.lat_e6, tk.lon_e6, tk.config_version,
                       u.full_name AS operator_name, tk.first_seen_via,
                       EXISTS (SELECT 1 FROM tg.ticket_photos ph WHERE ph.device_id = tk.device_id AND ph.seq = tk.seq) AS has_photo,
                       (SELECT e.payload ->> 'driver_change_reason' FROM tg.ticket_events e
                        WHERE e.device_id = tk.device_id AND e.seq = tk.seq AND e.type = 'SYNCED' LIMIT 1) AS driver_change_reason,
                       EXISTS (SELECT 1 FROM tg.ticket_events e WHERE e.device_id = tk.device_id AND e.seq = tk.seq
                               AND e.type = 'VOIDED') AS voided
                FROM tg.trips tr JOIN tg.tickets tk ON tk.device_id = tr.ticket_device_id AND tk.seq = tr.ticket_seq
                LEFT JOIN tg.users u ON u.id = tk.operator_id
                WHERE tr.id = @tripId
                """, new { tripId });
            var scans = (await conn.QueryAsync<ScanDetail>("""
                SELECT gs.id AS scanid, gs.direction, gs.scan_type AS scantype, gs.scanned_at AS scannedat, gs.outcome,
                       gs.reasons, gs.notes, u.full_name AS gatekeepername, gd.decision, gd.note AS decisionnote,
                       g.name AS gatename
                FROM tg.trips tr
                JOIN tg.gate_scans gs ON gs.id IN (tr.entry_scan_id, tr.exit_scan_id)
                LEFT JOIN tg.users u ON u.id = gs.gatekeeper_id
                LEFT JOIN tg.gate_decisions gd ON gd.gate_scan_id = gs.id
                LEFT JOIN tg.gates g ON g.id = gs.gate_id
                WHERE tr.id = @tripId ORDER BY gs.scanned_at
                """, new { tripId })).ToList();
            var snapshots = (await conn.QueryAsync<SnapshotDetail>(SnapshotSql + """
                WHERE s.gate_scan_id IN (SELECT unnest(array[tr.entry_scan_id, tr.exit_scan_id]) FROM tg.trips tr WHERE tr.id = @tripId)
                ORDER BY s.gate_scan_id, s.camera_id, s.frame_index
                """, new { tripId })).ToList();
            var reviews = (await conn.QueryAsync<ReviewRecord>("""
                SELECT r.id, r.reason_codes AS reasons, r.priority, r.is_random_sample AS issample, r.created_at AS createdat,
                       r.decision, r.decision_reason AS decisionreason, u.full_name AS decidedbyname, r.decided_at AS decidedat
                FROM tg.reviews r LEFT JOIN tg.users u ON u.id = r.decided_by
                WHERE r.trip_id = @tripId ORDER BY r.id
                """, new { tripId })).ToList();
            var events = (await conn.QueryAsync<StateEventRecord>("""
                SELECT from_state AS fromstate, to_state AS tostate, reason_code AS reasoncode, actor, at, note
                FROM tg.trip_state_events WHERE trip_id = @tripId ORDER BY id
                """, new { tripId })).ToList();
            var prints = await conn.ExecuteScalarAsync<int>(
                "SELECT count(*)::int FROM tg.receipt_prints WHERE trip_id = @tripId", new { tripId });
            var notes = (await conn.QueryAsync<ReviewNoteItem>("""
                SELECT n.id, n.kind, n.note, u.full_name AS createdbyname, n.created_at AS createdat
                FROM tg.review_notes n LEFT JOIN tg.users u ON u.id = n.created_by
                WHERE n.trip_id = @tripId ORDER BY n.id
                """, new { tripId })).ToList();
            var annotations = (await conn.QueryAsync<TripAnnotationItem>("""
                SELECT a.id, a.labels, a.note, u.full_name AS annotatedbyname, a.annotated_at AS annotatedat
                FROM tg.trip_annotations a LEFT JOIN tg.users u ON u.id = a.annotated_by
                WHERE a.trip_id = @tripId ORDER BY a.id DESC
                """, new { tripId })).ToList();
            var videos = (await conn.QueryAsync<VideoItem>(VideoSql + """
                WHERE v.gate_scan_id IN (SELECT unnest(array[tr.entry_scan_id, tr.exit_scan_id]) FROM tg.trips tr WHERE tr.id = @tripId)
                ORDER BY v.gate_scan_id, v.camera_id
                """, new { tripId })).ToList();
            var truck = await conn.QuerySingleOrDefaultAsync<TruckSummary>("""
                SELECT t.id, t.fleet_number AS fleetnumber, t.approval_status AS approvalstatus,
                       t.owner_last_name AS ownerlastname, t.owner_first_name AS ownerfirstname,
                       t.reported_plate AS reportedplate, t.merged_into AS mergedinto
                FROM tg.trips tr JOIN tg.trucks t ON t.id = tr.truck_id WHERE tr.id = @tripId
                """, new { tripId });

            TicketDetail? t = ticket is null ? null : new TicketDetail(ticket.TicketId, ticket.IssuedAt, ticket.ClockDelta,
                ticket.Flags, (ticket.Flags & 1) != 0 ? null : ticket.LatE6 / 1e6, (ticket.Flags & 1) != 0 ? null : ticket.LonE6 / 1e6,
                ticket.ConfigVersion, ticket.OperatorName, ticket.FirstSeenVia, ticket.HasPhoto, ticket.DriverChangeReason, ticket.Voided);
            return new TripDetail(card, t, scans, snapshots, reviews, events, prints, notes, annotations, videos, truck);
        }, ct);

    public Task<ReviewDecisionResult> DecideAsync(long tripId, ReviewDecision decision, string? reason, int? decidedBy,
        Actor actor, CancellationToken ct = default)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (decision == ReviewDecision.Reject && why is null)
            throw TruckGateException.Validation("REASON_REQUIRED", "A rejection needs a reason.");
        return db.InTransactionAsync(actor, (conn, tx) => DecideCoreAsync(conn, tx, tripId, decision, why, decidedBy), ct);
    }

    /// <summary>One decision closes every open review of the trip and moves the trip (also used by truck approval).</summary>
    internal static async Task<ReviewDecisionResult> DecideCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long tripId,
        ReviewDecision decision, string? why, int? decidedBy)
    {
        var trip = await Trips.LockAsync(conn, tx, tripId);
        var open = (await conn.QueryAsync<(long Id, string[] Reasons)>(
            "SELECT id, reason_codes FROM tg.reviews WHERE trip_id = @tripId AND decision IS NULL ORDER BY id",
            new { tripId }, tx)).ToList();
        if (open.Count == 0) throw TruckGateException.Conflict("NO_OPEN_REVIEW", $"{trip.TripNumber} has no open review.");
        if (decision == ReviewDecision.Reject && trip.CurrentState == "PAID")
            throw TruckGateException.Conflict("TRIP_PAID", $"{trip.TripNumber} is already paid; correct it in the next payment.");

        var dbDecision = decision switch
        {
            ReviewDecision.Accept => "ACCEPT",
            ReviewDecision.AcceptPartial => "ACCEPT_PARTIAL",
            _ => "REJECT",
        };
        await conn.ExecuteAsync("""
            UPDATE tg.reviews SET decision = @dbDecision, decision_reason = @why, decided_by = @decidedBy, decided_at = now()
            WHERE trip_id = @tripId AND decision IS NULL
            """, new { dbDecision, why, decidedBy, tripId }, tx);

        var state = trip.CurrentState;
        if (decision == ReviewDecision.Reject)
        {
            if (state != "REJECTED")
            {
                if (state != "PENDING_REVIEW")
                {
                    await Trips.TransitionAsync(conn, tx, tripId, state, "PENDING_REVIEW", "SAMPLE_REJECT", why);
                }
                await Trips.TransitionAsync(conn, tx, tripId, "PENDING_REVIEW", "REJECTED", "REVIEW_REJECT", why);
                state = "REJECTED";
            }
        }
        else
        {
            if (decision == ReviewDecision.AcceptPartial)
                await conn.ExecuteAsync("UPDATE tg.trips SET load_status = 'PARTIAL' WHERE id = @tripId", new { tripId }, tx);
            if (state == "PENDING_REVIEW")
            {
                bool noExit = open.Any(r => r.Reasons.Contains("NO_EXIT_SCAN"));
                var target = trip.ExitScanId is not null || noExit ? "CLOSED" : "ENTERED";
                await Trips.TransitionAsync(conn, tx, tripId, "PENDING_REVIEW", target, "REVIEW_" + dbDecision, why);
                state = target;
            }
            else if (state == "EXITED_EMPTY" && !await Trips.HasBlockingReviewAsync(conn, tx, tripId))
            {
                await Trips.TransitionAsync(conn, tx, tripId, "EXITED_EMPTY", "CLOSED", "REVIEW_" + dbDecision, why);
                state = "CLOSED";
            }
        }
        return new ReviewDecisionResult(tripId, trip.TripNumber, state, open.Count);
    }

    /// <summary>"Request further investigation": the review stays open, the note says who asks what and when.</summary>
    public Task InvestigateAsync(long tripId, string note, int? userId, Actor actor, CancellationToken ct = default) =>
        AddNoteAsync(tripId, "INVESTIGATE", note, userId, actor, requireOpenReview: true, ct);

    public Task NoteAsync(long tripId, string note, int? userId, Actor actor, CancellationToken ct = default) =>
        AddNoteAsync(tripId, "NOTE", note, userId, actor, requireOpenReview: false, ct);

    private Task AddNoteAsync(long tripId, string kind, string note, int? userId, Actor actor, bool requireOpenReview,
        CancellationToken ct)
    {
        var text = (note ?? "").Trim();
        if (text.Length is 0 or > 2000) throw TruckGateException.Validation("NOTE_REQUIRED", "Write what must be checked (2000 characters at most).");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var trip = await Trips.LockAsync(conn, tx, tripId);
            if (requireOpenReview && !await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM tg.reviews WHERE trip_id = @tripId AND decision IS NULL)", new { tripId }, tx))
                throw TruckGateException.Conflict("NO_OPEN_REVIEW", $"{trip.TripNumber} has no open review.");
            await conn.ExecuteAsync("""
                INSERT INTO tg.review_notes (trip_id, kind, note, created_by) VALUES (@tripId, @kind, @text, @userId)
                """, new { tripId, kind, text, userId }, tx);
            return 0;
        }, ct);
    }

    /// <summary>Human labels of the whole trip for the future dataset. The new set replaces the previous one (history kept).</summary>
    public Task AnnotateAsync(long tripId, IReadOnlyList<string> labels, string? note, int? userId, Actor actor,
        CancellationToken ct = default)
    {
        var set = (labels ?? []).Select(l => (l ?? "").Trim().ToUpperInvariant()).Where(l => l.Length > 0).Distinct().ToArray();
        if (set.Any(l => !TripLabels.Contains(l))) throw TruckGateException.Validation("LABEL_INVALID", "Unknown trip label.");
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (text is { Length: > 2000 }) throw TruckGateException.Validation("NOTE_TOO_LONG", "2000 characters at most.");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await Trips.LockAsync(conn, tx, tripId);
            await conn.ExecuteAsync("""
                INSERT INTO tg.trip_annotations (trip_id, labels, note, annotated_by) VALUES (@tripId, @set, @text, @userId)
                """, new { tripId, set, text, userId }, tx);
            return 0;
        }, ct);
    }

    public Task LabelAsync(long snapshotId, string type, string value, int? labeledBy, Actor actor, CancellationToken ct = default)
    {
        var t = (type ?? "").Trim().ToUpperInvariant();
        var v = (value ?? "").Trim().ToUpperInvariant();
        var ok = t switch
        {
            "LOAD" => LoadLabels.Contains(v),
            "QUALITY" => QualityLabels.Contains(v),
            "PLATE" => v.Length is >= 1 and <= 32,
            _ => false,
        };
        if (!ok) throw TruckGateException.Validation("LABEL_INVALID", "Unknown label type or value.");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.snapshots WHERE id = @snapshotId)",
                    new { snapshotId }, tx))
                throw TruckGateException.NotFound("SNAPSHOT_NOT_FOUND", $"Picture {snapshotId} does not exist.");
            await conn.ExecuteAsync("""
                INSERT INTO tg.snapshot_labels (snapshot_id, label_type, value, labeled_by) VALUES (@snapshotId, @t, @v, @labeledBy)
                """, new { snapshotId, t, v, labeledBy }, tx);
            return 0;
        }, ct);
    }

    /// <summary>
    /// Random check of accepted trips from one day (default 5 %), at least one when there were trips.
    /// Runs once per day; the result is also a source of training labels.
    /// </summary>
    public static async Task<int> CreateDailySampleAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DateOnly day,
        string timeZone, decimal percent)
    {
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.review_samples WHERE day = @d)",
                new { d = day.ToDateTime(TimeOnly.MinValue) }, tx))
            return 0;
        var candidates = (await conn.QueryAsync<long>("""
            SELECT tr.id FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
            WHERE (gs.scanned_at AT TIME ZONE @timeZone)::date = @d
              AND tr.current_state IN ('ENTERED', 'EXITED_EMPTY', 'CLOSED')
              AND NOT EXISTS (SELECT 1 FROM tg.reviews r WHERE r.trip_id = tr.id)
            """, new { timeZone, d = day.ToDateTime(TimeOnly.MinValue) }, tx)).ToList();
        int n = candidates.Count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(candidates.Count * percent / 100m));
        var chosen = candidates.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(n).ToList();
        foreach (var id in chosen)
            await Trips.OpenReviewAsync(conn, tx, id, ["RANDOM_SAMPLE"], sample: true);
        await conn.ExecuteAsync("INSERT INTO tg.review_samples (day, trip_count, percent) VALUES (@d, @n, @percent)",
            new { d = day.ToDateTime(TimeOnly.MinValue), n = chosen.Count, percent }, tx);
        return chosen.Count;
    }

    /// <summary>
    /// Training data export: every picture in the period with its labels, the gatekeeper's decision and
    /// the review decision, as a zip (images/ + labels.jsonl). Pictures failing their fingerprint are left
    /// out and listed in problems.txt. Returns the path of a temporary file deleted when closed.
    /// </summary>
    public async Task<string> ExportDatasetAsync(DateOnly from, DateOnly to, string timeZone, CancellationToken ct = default) =>
        await ExportDatasetAsync(from, to, timeZone, includeVideos: false, ct);

    /// <summary>
    /// Training data for a period: pictures with their labels (labels.jsonl), one line per trip with the
    /// supervisor's trip labels (trips.jsonl) and, when asked, the passage videos. Every file is checked
    /// against its recorded fingerprint first; a file that fails is listed in problems.txt, never exported.
    /// </summary>
    public async Task<string> ExportDatasetAsync(DateOnly from, DateOnly to, string timeZone, bool includeVideos,
        CancellationToken ct = default)
    {
        if (to < from || to.DayNumber - from.DayNumber > 92)
            throw TruckGateException.Validation("PERIOD_INVALID", "Choose a period of at most 3 months.");
        var rows = await db.ReadAsync(async conn => (await conn.QueryAsync<ExportRow>("""
            SELECT s.id, s.gate_scan_id, s.camera_id, s.camera_role, s.camera_config_version, s.frame_index, s.captured_at,
                   s.path, s.sha256, gs.direction, gs.outcome, gs.reasons, gd.decision AS gate_decision,
                   tr.id AS trip_id, tr.truck_id, tr.load_status,
                   (SELECT p.plate_text FROM tg.truck_plates p WHERE p.truck_id = tr.truck_id AND p.valid_from <= gs.scanned_at
                    AND (p.valid_to IS NULL OR p.valid_to > gs.scanned_at) ORDER BY p.valid_from DESC LIMIT 1) AS plate_on_record,
                   (SELECT r.decision FROM tg.reviews r WHERE r.trip_id = tr.id AND r.decision IS NOT NULL
                    ORDER BY r.decided_at DESC LIMIT 1) AS review_decision,
                   (SELECT l.value FROM tg.snapshot_labels l WHERE l.snapshot_id = s.id AND l.label_type = 'LOAD' ORDER BY l.id DESC LIMIT 1) AS label_load,
                   (SELECT l.value FROM tg.snapshot_labels l WHERE l.snapshot_id = s.id AND l.label_type = 'PLATE' ORDER BY l.id DESC LIMIT 1) AS label_plate,
                   (SELECT l.value FROM tg.snapshot_labels l WHERE l.snapshot_id = s.id AND l.label_type = 'QUALITY' ORDER BY l.id DESC LIMIT 1) AS label_quality
            FROM tg.snapshots s
            JOIN tg.gate_scans gs ON gs.id = s.gate_scan_id
            LEFT JOIN tg.gate_decisions gd ON gd.gate_scan_id = gs.id
            LEFT JOIN tg.trips tr ON tr.entry_scan_id = gs.id OR tr.exit_scan_id = gs.id
            WHERE (gs.scanned_at AT TIME ZONE @timeZone)::date BETWEEN @from AND @to
            ORDER BY s.id
            """, new { timeZone, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) },
            commandTimeout: 300)).ToList(), ct);

        var path = Path.Combine(Path.GetTempPath(), $"truckgate-dataset-{Guid.NewGuid():N}.zip");
        var root = Path.GetFullPath(storage.Root);
        var problems = new List<string>();
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var jsonl = new StringBuilder();
            foreach (var r in rows)
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(Path.Combine(root, r.Path));
                byte[]? bytes = full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full)
                    ? await File.ReadAllBytesAsync(full, ct) : null;
                if (bytes is null || !SHA256.HashData(bytes).AsSpan().SequenceEqual(r.Sha256))
                {
                    problems.Add($"{r.Id}\t{(bytes is null ? "missing" : "fingerprint mismatch")}\t{r.Path}");
                    continue;
                }
                var entry = zip.CreateEntry($"images/{r.Id}.jpg", CompressionLevel.NoCompression);
                await using (var es = entry.Open()) await es.WriteAsync(bytes, ct);
                jsonl.AppendLine(JsonSerializer.Serialize(new
                {
                    snapshot_id = r.Id, image = $"images/{r.Id}.jpg", sha256 = Convert.ToHexStringLower(r.Sha256),
                    scan_id = r.GateScanId, trip_id = r.TripId, direction = r.Direction, camera_id = r.CameraId,
                    camera_role = r.CameraRole, camera_config_version = r.CameraConfigVersion, frame_index = r.FrameIndex,
                    captured_at = r.CapturedAt, scan_outcome = r.Outcome, scan_reasons = r.Reasons,
                    gate_decision = r.GateDecision, review_decision = r.ReviewDecision, trip_load = r.LoadStatus,
                    truck_id = r.TruckId, plate_on_record = r.PlateOnRecord,
                    labels = new { load = r.LabelLoad, plate = r.LabelPlate, quality = r.LabelQuality },
                }));
            }
            await WriteTextAsync(zip, "labels.jsonl", jsonl.ToString(), ct);
            int pictureProblems = problems.Count;

            // One line per trip: the human trip labels, the gate and review decisions, and its videos.
            var trips = await db.ReadAsync(async conn => (await conn.QueryAsync<TripExportRow>("""
                SELECT tr.id, tr.trip_number, tr.truck_id, tg.effective_truck_id(tr.truck_id) AS effective_truck_id,
                       t.approval_status AS truck_status, tr.driver_id, tr.load_status, tr.current_state AS state,
                       tk.ticket_id, gs.scanned_at AS entered_at, gx.scanned_at AS exited_at,
                       coalesce(tk.photo_sha256 <> decode(repeat('00', 32), 'hex'), true) AS photo_expected,
                       EXISTS (SELECT 1 FROM tg.ticket_photos ph WHERE ph.device_id = tr.ticket_device_id
                               AND ph.seq = tr.ticket_seq) AS photo_received,
                       ge.decision AS entry_decision, gdx.decision AS exit_decision,
                       (SELECT r.decision FROM tg.reviews r WHERE r.trip_id = tr.id AND r.decision IS NOT NULL
                        ORDER BY r.decided_at DESC LIMIT 1) AS review_decision,
                       a.labels AS trip_labels, a.note AS label_note, u.full_name AS labelled_by, a.annotated_at AS labelled_at
                FROM tg.trips tr
                JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                JOIN tg.tickets tk ON tk.device_id = tr.ticket_device_id AND tk.seq = tr.ticket_seq
                LEFT JOIN tg.gate_scans gx ON gx.id = tr.exit_scan_id
                LEFT JOIN tg.trucks t ON t.id = tr.truck_id
                LEFT JOIN tg.gate_decisions ge ON ge.gate_scan_id = tr.entry_scan_id
                LEFT JOIN tg.gate_decisions gdx ON gdx.gate_scan_id = tr.exit_scan_id
                LEFT JOIN LATERAL (SELECT x.labels, x.note, x.annotated_by, x.annotated_at FROM tg.trip_annotations x
                                   WHERE x.trip_id = tr.id ORDER BY x.id DESC LIMIT 1) a ON true
                LEFT JOIN tg.users u ON u.id = a.annotated_by
                WHERE (gs.scanned_at AT TIME ZONE @timeZone)::date BETWEEN @from AND @to
                ORDER BY tr.id
                """, new { timeZone, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) },
                commandTimeout: 300)).ToList(), ct);
            var videos = await db.ReadAsync(async conn => (await conn.QueryAsync<VideoExportRow>("""
                SELECT v.id, tr.id AS trip_id, v.gate_scan_id, v.camera_id, v.camera_config_version, v.direction, v.status,
                       v.window_start, v.window_end, v.media_start, v.media_end, v.duration_ms, v.path, v.sha256, v.error
                FROM tg.video_recordings v
                JOIN tg.trips tr ON tr.entry_scan_id = v.gate_scan_id OR tr.exit_scan_id = v.gate_scan_id
                JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                WHERE (gs.scanned_at AT TIME ZONE @timeZone)::date BETWEEN @from AND @to
                ORDER BY v.id
                """, new { timeZone, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) },
                commandTimeout: 300)).ToList(), ct);

            var videoFiles = new Dictionary<long, string>();
            if (includeVideos)
            {
                foreach (var v in videos.Where(v => v.Status is "OK" or "PARTIAL" && v.Path is not null && v.Sha256 is not null))
                {
                    ct.ThrowIfCancellationRequested();
                    var full = Path.GetFullPath(Path.Combine(root, v.Path!));
                    bool ok = false;
                    if (full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full))
                    {
                        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                        ok = (await SHA256.HashDataAsync(fs, ct)).AsSpan().SequenceEqual(v.Sha256);
                    }
                    if (!ok)
                    {
                        problems.Add($"video {v.Id}\t{(File.Exists(full) ? "fingerprint mismatch" : "missing")}\t{v.Path}");
                        continue;
                    }
                    var name = $"videos/{v.Id}.mp4";
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    await using (var es = entry.Open())
                    await using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                        await fs.CopyToAsync(es, ct);
                    videoFiles[v.Id] = name;
                }
            }

            var byTrip = videos.ToLookup(v => v.TripId);
            var tripsJsonl = new StringBuilder();
            foreach (var tr in trips)
            {
                tripsJsonl.AppendLine(JsonSerializer.Serialize(new
                {
                    trip_id = tr.Id, trip_number = tr.TripNumber, ticket_id = tr.TicketId, truck_id = tr.TruckId,
                    effective_truck_id = tr.EffectiveTruckId, truck_status = tr.TruckStatus, driver_id = tr.DriverId,
                    entered_at = tr.EnteredAt, exited_at = tr.ExitedAt, state = tr.State, trip_load = tr.LoadStatus,
                    quarry_photo = tr.PhotoExpected ? (tr.PhotoReceived ? "RECEIVED" : "NOT_RECEIVED") : "NONE",
                    entry_decision = tr.EntryDecision, exit_decision = tr.ExitDecision, review_decision = tr.ReviewDecision,
                    // Human labels only: null when nobody labelled the trip (never filled in automatically).
                    labels = tr.TripLabels, label_note = tr.LabelNote, labelled_by = tr.LabelledBy, labelled_at = tr.LabelledAt,
                    human_verified = tr.LabelledAt is not null,
                    videos = byTrip[tr.Id].Select(v => new
                    {
                        video_id = v.Id, scan_id = v.GateScanId, direction = v.Direction, camera_id = v.CameraId,
                        camera_config_version = v.CameraConfigVersion, status = v.Status, window_start = v.WindowStart,
                        window_end = v.WindowEnd, media_start = v.MediaStart, media_end = v.MediaEnd, duration_ms = v.DurationMs,
                        sha256 = v.Sha256 is null ? null : Convert.ToHexStringLower(v.Sha256), error = v.Error,
                        file = videoFiles.GetValueOrDefault(v.Id),
                    }),
                }));
            }
            await WriteTextAsync(zip, "trips.jsonl", tripsJsonl.ToString(), ct);
            int labelled = trips.Count(x => x.LabelledAt is not null);
            await WriteTextAsync(zip, "README.txt", $"""
                TruckGate training data {from:yyyy-MM-dd} .. {to:yyyy-MM-dd}
                labels.jsonl: one line per picture. "labels" are set by supervisors (latest label wins).
                gate_decision / review_decision / trip_load are weaker labels from the gate and the review.
                trips.jsonl: one line per trip. "labels" are the supervisor's trip labels (NORMAL_TRIP, EMPTY_TRUCK,
                LOADED_TRUCK, INCOMPLETE_CARGO, WRONG_TRUCK, PLATE_UNREADABLE, POSSIBLE_MISMATCH, CAMERA_FAILURE,
                NEEDS_REVIEW); human_verified is true only when a person set them. Nothing here is produced by a model.
                Pictures: {rows.Count - pictureProblems} exported, {pictureProblems} refused (see problems.txt).
                Trips: {trips.Count} ({labelled} labelled by a supervisor). Videos listed: {videos.Count};
                video files {(includeVideos ? $"exported: {videoFiles.Count}" : "not included (ask with videos=true)")}.

                """, ct);
            if (problems.Count > 0) await WriteTextAsync(zip, "problems.txt", string.Join('\n', problems) + "\n", ct);
        }
        return path;
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name);
        await using var s = entry.Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    internal const string VideoSql = """
        SELECT v.id, v.gate_scan_id AS scanid, v.camera_id AS cameraid, c.label AS cameralabel, v.direction, v.status,
               v.scan_at AS scanat, v.window_start AS windowstart, v.window_end AS windowend, v.duration_ms AS durationms,
               v.size_bytes AS sizebytes, v.error
        FROM tg.video_recordings v JOIN tg.cameras c ON c.id = v.camera_id

        """;

    internal const string SnapshotSql = """
        SELECT s.id, s.gate_scan_id AS scanid, s.camera_id AS cameraid, c.label AS cameralabel, s.camera_role AS role,
               s.frame_index::int AS frameindex, s.captured_at AS capturedat,
               (SELECT l.value FROM tg.snapshot_labels l WHERE l.snapshot_id = s.id AND l.label_type = 'LOAD' ORDER BY l.id DESC LIMIT 1) AS loadlabel,
               (SELECT l.value FROM tg.snapshot_labels l WHERE l.snapshot_id = s.id AND l.label_type = 'PLATE' ORDER BY l.id DESC LIMIT 1) AS platelabel,
               (SELECT l.value FROM tg.snapshot_labels l WHERE l.snapshot_id = s.id AND l.label_type = 'QUALITY' ORDER BY l.id DESC LIMIT 1) AS qualitylabel
        FROM tg.snapshots s JOIN tg.cameras c ON c.id = s.camera_id

        """;

    private sealed class TicketRow
    {
        public string TicketId { get; set; } = "";
        public DateTime IssuedAt { get; set; }
        public int ClockDelta { get; set; }
        public int Flags { get; set; }
        public int LatE6 { get; set; }
        public int LonE6 { get; set; }
        public int ConfigVersion { get; set; }
        public string? OperatorName { get; set; }
        public string FirstSeenVia { get; set; } = "";
        public bool HasPhoto { get; set; }
        public string? DriverChangeReason { get; set; }
        public bool Voided { get; set; }
    }

    private sealed class TripExportRow
    {
        public long Id { get; set; }
        public string TripNumber { get; set; } = "";
        public int TruckId { get; set; }
        public int EffectiveTruckId { get; set; }
        public string? TruckStatus { get; set; }
        public int DriverId { get; set; }
        public string? LoadStatus { get; set; }
        public string State { get; set; } = "";
        public string TicketId { get; set; } = "";
        public DateTime EnteredAt { get; set; }
        public DateTime? ExitedAt { get; set; }
        public bool PhotoExpected { get; set; }
        public bool PhotoReceived { get; set; }
        public string? EntryDecision { get; set; }
        public string? ExitDecision { get; set; }
        public string? ReviewDecision { get; set; }
        public string[]? TripLabels { get; set; }
        public string? LabelNote { get; set; }
        public string? LabelledBy { get; set; }
        public DateTime? LabelledAt { get; set; }
    }

    private sealed class VideoExportRow
    {
        public long Id { get; set; }
        public long TripId { get; set; }
        public long GateScanId { get; set; }
        public int CameraId { get; set; }
        public int CameraConfigVersion { get; set; }
        public string Direction { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime WindowStart { get; set; }
        public DateTime WindowEnd { get; set; }
        public DateTime? MediaStart { get; set; }
        public DateTime? MediaEnd { get; set; }
        public int? DurationMs { get; set; }
        public string? Path { get; set; }
        public byte[]? Sha256 { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ExportRow
    {
        public long Id { get; set; }
        public long GateScanId { get; set; }
        public int CameraId { get; set; }
        public string CameraRole { get; set; } = "";
        public int CameraConfigVersion { get; set; }
        public int FrameIndex { get; set; }
        public DateTime CapturedAt { get; set; }
        public string Path { get; set; } = "";
        public byte[] Sha256 { get; set; } = [];
        public string Direction { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string[] Reasons { get; set; } = [];
        public string? GateDecision { get; set; }
        public long? TripId { get; set; }
        public int? TruckId { get; set; }
        public string? LoadStatus { get; set; }
        public string? PlateOnRecord { get; set; }
        public string? ReviewDecision { get; set; }
        public string? LabelLoad { get; set; }
        public string? LabelPlate { get; set; }
        public string? LabelQuality { get; set; }
    }
}
