using Dapper;
using TruckGate.Data;

namespace TruckGate.Application;

/// <summary>Filters of the burned-receipts list. Dates are entry dates in the site time zone.</summary>
public sealed record BurnedFilter(DateOnly? From, DateOnly? To, int? TruckId, int? DriverId, int? OperatorId, string? Lifecycle,
    string? Review, string? Search, int Limit = 200, int Offset = 0);

/// <summary>
/// A quarry receipt consumed at the gate (it has a trip), mutable for Dapper. Lifecycle is derived from
/// the trip: SCANNED_AT_ENTRY (gatekeeper deciding), ENTRY_REVIEW, INSIDE_FACTORY, SCANNED_AT_EXIT,
/// CLOSED (paid ones too), REJECTED.
/// </summary>
public sealed class BurnedReceipt
{
    public long TripId { get; set; }
    public string TripNumber { get; set; } = "";
    public string TicketId { get; set; } = "";
    public int DeviceId { get; set; }
    public long Seq { get; set; }
    public int TruckId { get; set; }
    public string? FleetNumber { get; set; }
    public string? Plate { get; set; }
    public string? TruckStatus { get; set; }
    public string? DriverName { get; set; }
    public string? OperatorName { get; set; }
    public DateTime? IssuedAt { get; set; }
    public DateTime EnteredAt { get; set; }
    public DateTime? ExitedAt { get; set; }
    public string EntryOutcome { get; set; } = "";
    public string? ExitOutcome { get; set; }
    public string State { get; set; } = "";
    public string Lifecycle { get; set; } = "";
    public bool Paid { get; set; }
    public bool VoidedAfterUse { get; set; }
    public string? LoadStatus { get; set; }
    public string? EntryVideo { get; set; }
    public string? ExitVideo { get; set; }
    public string ReviewStatus { get; set; } = "";
    public bool HasPhoto { get; set; }
    public bool PhotoExpected { get; set; }
}

public sealed record BurnedPage(int Total, IReadOnlyList<BurnedReceipt> Items);

/// <summary>Mutable for Dapper.</summary>
public sealed class ActivityItem
{
    public DateTime At { get; set; }
    public string Kind { get; set; } = "";
    public string Code { get; set; } = "";
    public long? TripId { get; set; }
    public string? TripNumber { get; set; }
    public string? FleetNumber { get; set; }
    public string? Detail { get; set; }
}

/// <summary>
/// "الوصلات المحروقة": every quarry receipt that was used at the gate, kept for ever with its whole
/// history; and the activity feed of the notification centre (entries, exits, trip outcomes).
/// </summary>
public sealed class ReceiptsService(Db db)
{
    public const string LifecycleSql = """
        CASE WHEN tr.current_state = 'REJECTED' THEN 'REJECTED'
             WHEN tr.current_state IN ('CLOSED', 'PAID') THEN 'CLOSED'
             WHEN tr.exit_scan_id IS NOT NULL THEN 'SCANNED_AT_EXIT'
             WHEN tr.current_state = 'AT_GATE' THEN 'SCANNED_AT_ENTRY'
             WHEN tr.current_state = 'PENDING_REVIEW' THEN 'ENTRY_REVIEW'
             WHEN tr.current_state = 'ENTERED' THEN 'INSIDE_FACTORY'
             ELSE tr.current_state END
        """;

    private static string VideoStatusSql(string scanColumn) => $"""
        (SELECT CASE WHEN count(*) = 0 THEN NULL
                     WHEN bool_and(v.status = 'OK') THEN 'OK'
                     WHEN bool_or(v.status = 'RECORDING') THEN 'RECORDING'
                     WHEN bool_or(v.status IN ('OK', 'PARTIAL')) THEN 'PARTIAL'
                     WHEN bool_and(v.status = 'PURGED') THEN 'PURGED'
                     ELSE 'FAILED' END
         FROM tg.video_recordings v WHERE v.gate_scan_id = {scanColumn})
        """;

    public Task<BurnedPage> ListAsync(BurnedFilter f, CancellationToken ct = default)
    {
        var limit = Math.Clamp(f.Limit, 1, 500);
        var offset = Math.Max(0, f.Offset);
        return db.ReadAsync(async conn =>
        {
            var tz = (await SettingsBag.LoadAsync(conn)).String("site.timezone", "Africa/Algiers");
            var search = string.IsNullOrWhiteSpace(f.Search) ? null : f.Search.Trim();
            var p = new
            {
                tz, from = f.From?.ToDateTime(TimeOnly.MinValue), to = f.To?.ToDateTime(TimeOnly.MinValue),
                truck = f.TruckId, driver = f.DriverId, op = f.OperatorId, lifecycle = f.Lifecycle, review = f.Review,
                text = search is null ? null : "%" + search + "%", key = PlateRules.Key(search), limit, offset,
            };
            var sql = $"""
                WITH r AS (
                    SELECT tr.id AS trip_id, tr.trip_number, 'Q' || tr.ticket_device_id || '-' || tg.fmt_seq(tr.ticket_seq) AS ticket_id,
                           tr.ticket_device_id AS device_id, tr.ticket_seq AS seq, tr.truck_id, t.fleet_number,
                           (SELECT pl.plate_text FROM tg.truck_plates pl WHERE pl.truck_id = tr.truck_id AND pl.valid_to IS NULL
                            ORDER BY pl.valid_from DESC LIMIT 1) AS plate,
                           t.approval_status AS truck_status, d.full_name AS driver_name, u.full_name AS operator_name,
                           tk.issued_at, gs.scanned_at AS entered_at, xs.scanned_at AS exited_at,
                           gs.outcome AS entry_outcome, xs.outcome AS exit_outcome, tr.current_state AS state,
                           {LifecycleSql} AS lifecycle,
                           tr.current_state = 'PAID' AS paid,
                           EXISTS (SELECT 1 FROM tg.ticket_events e WHERE e.device_id = tr.ticket_device_id
                                   AND e.seq = tr.ticket_seq AND e.type = 'VOIDED') AS voided_after_use,
                           tr.load_status,
                           {VideoStatusSql("tr.entry_scan_id")} AS entry_video,
                           {VideoStatusSql("tr.exit_scan_id")} AS exit_video,
                           CASE WHEN EXISTS (SELECT 1 FROM tg.reviews rv WHERE rv.trip_id = tr.id AND rv.decision IS NULL)
                                     THEN CASE WHEN EXISTS (SELECT 1 FROM tg.review_notes n WHERE n.trip_id = tr.id AND n.kind = 'INVESTIGATE'
                                                            AND n.created_at >= (SELECT min(rv2.created_at) FROM tg.reviews rv2
                                                                                 WHERE rv2.trip_id = tr.id AND rv2.decision IS NULL))
                                               THEN 'INVESTIGATING' ELSE 'OPEN' END
                                WHEN EXISTS (SELECT 1 FROM tg.reviews rv WHERE rv.trip_id = tr.id) THEN 'DECIDED'
                                ELSE 'NONE' END AS review_status,
                           EXISTS (SELECT 1 FROM tg.ticket_photos ph WHERE ph.device_id = tr.ticket_device_id
                                   AND ph.seq = tr.ticket_seq) AS has_photo,
                           coalesce(tk.photo_sha256 <> decode(repeat('00', 32), 'hex'), true) AS photo_expected,
                           tk.operator_id, tr.driver_id
                    FROM tg.trips tr
                    JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                    LEFT JOIN tg.gate_scans xs ON xs.id = tr.exit_scan_id
                    LEFT JOIN tg.tickets tk ON tk.device_id = tr.ticket_device_id AND tk.seq = tr.ticket_seq
                    LEFT JOIN tg.trucks t ON t.id = tr.truck_id
                    LEFT JOIN tg.drivers d ON d.id = tr.driver_id
                    LEFT JOIN tg.users u ON u.id = tk.operator_id
                    WHERE (@from::timestamp IS NULL OR (gs.scanned_at AT TIME ZONE @tz)::date >= @from::date)
                      AND (@to::timestamp IS NULL OR (gs.scanned_at AT TIME ZONE @tz)::date <= @to::date)
                      AND (@truck::int IS NULL OR tr.truck_id = @truck OR tg.effective_truck_id(tr.truck_id) = @truck)
                      AND (@driver::int IS NULL OR tr.driver_id = @driver)
                      AND (@op::int IS NULL OR tk.operator_id = @op)
                      AND (@text::text IS NULL OR tr.trip_number ILIKE @text
                           OR ('Q' || tr.ticket_device_id || '-' || tg.fmt_seq(tr.ticket_seq)) ILIKE @text
                           OR t.fleet_number ILIKE @text
                           OR (length(@key) >= 3 AND EXISTS (SELECT 1 FROM tg.truck_plates pl WHERE pl.truck_id = tr.truck_id
                                                             AND tg.plate_key(pl.plate_text) LIKE '%' || @key || '%')))
                )
                SELECT * FROM r
                WHERE (@lifecycle::text IS NULL OR lifecycle = @lifecycle)
                  AND (@review::text IS NULL OR review_status = @review)
                ORDER BY entered_at DESC
                """;
            var total = await conn.ExecuteScalarAsync<int>($"SELECT count(*)::int FROM ({sql}) x", p);
            var items = (await conn.QueryAsync<BurnedReceipt>(sql + " LIMIT @limit OFFSET @offset", p)).ToList();
            return new BurnedPage(total, items);
        }, ct);
    }

    /// <summary>Gate passages and trip outcomes of the last hours, newest first (real events only).</summary>
    public Task<IReadOnlyList<ActivityItem>> ActivityAsync(int hours, int limit, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<ActivityItem>>(async conn => (await conn.QueryAsync<ActivityItem>("""
            (SELECT gs.scanned_at AS at, 'SCAN_' || gs.direction AS kind, gs.outcome AS code, tr.id AS trip_id,
                    tr.trip_number, t.fleet_number, array_to_string(gs.reasons, ',') AS detail
             FROM tg.gate_scans gs
             LEFT JOIN tg.trips tr ON tr.entry_scan_id = gs.id OR tr.exit_scan_id = gs.id
             LEFT JOIN tg.trucks t ON t.id = tr.truck_id
             WHERE gs.scanned_at > now() - make_interval(hours => @hours))
            UNION ALL
            (SELECT e.at, 'TRIP' AS kind, e.to_state AS code, tr.id, tr.trip_number, t.fleet_number, e.reason_code
             FROM tg.trip_state_events e
             JOIN tg.trips tr ON tr.id = e.trip_id
             LEFT JOIN tg.trucks t ON t.id = tr.truck_id
             WHERE e.at > now() - make_interval(hours => @hours) AND e.to_state IN ('PENDING_REVIEW', 'CLOSED', 'REJECTED', 'PAID'))
            UNION ALL
            (SELECT t.created_at, 'TRUCK' AS kind, 'REGISTERED' AS code, NULL, NULL, t.fleet_number, t.reported_plate
             FROM tg.trucks t WHERE t.created_via = 'PHONE' AND t.created_at > now() - make_interval(hours => @hours))
            UNION ALL
            (SELECT t.decided_at, 'TRUCK' AS kind, t.approval_status AS code, NULL, NULL, t.fleet_number, t.decision_reason
             FROM tg.trucks t WHERE t.created_via = 'PHONE' AND t.decided_at > now() - make_interval(hours => @hours))
            ORDER BY at DESC LIMIT @limit
            """, new { hours = Math.Clamp(hours, 1, 24 * 14), limit = Math.Clamp(limit, 1, 500) })).ToList(), ct);
}
