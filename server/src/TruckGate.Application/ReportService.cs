using System.Globalization;
using System.Text;
using Dapper;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record CountItem(string Key, string? Label, int Count);

/// <summary>Mutable for Dapper (PostgreSQL arrays are not matched to constructor parameters).</summary>
public sealed class RecentScan
{
    public long ScanId { get; set; }
    public DateTime ScannedAt { get; set; }
    public string Direction { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string[] Reasons { get; set; } = [];
    public string? TicketId { get; set; }
    public string? FleetNumber { get; set; }
    public string? GateName { get; set; }
}

public sealed record Dashboard(DateOnly Day, int Entries, int Exits, int Accepted, int Pending, int Rejected, int Partial,
    int Inside, int OpenReviews, int OpenAlertsCritical, int OpenAlertsOther, int PhonesOverdue,
    IReadOnlyList<CountItem> ByTruck, IReadOnlyList<CountItem> ByDriver, IReadOnlyList<CountItem> ByHour,
    IReadOnlyList<RecentScan> Recent, int TripsToday, int CompletedToday, int TrucksAwaitingApproval, int BurnedToday,
    int CameraFailures, int RecordingFailuresToday, int SyncFailures, int UnclosedTrips);

/// <summary>Per driver (kept for drivers paid as employees); amounts use each truck's price.</summary>
public sealed record StatementLine(int DriverId, string DriverName, string EmploymentType, int Trips, int Full, int Partial,
    decimal Amount);

/// <summary>Per truck: what the owner is paid. Total = eligible trips × approved price (+ pending adjustments).</summary>
public sealed record TruckStatementLine(int TruckId, string? FleetNumber, string? OwnerLastName, string? OwnerFirstName,
    string? Plate, int Trips, int Full, int Partial, decimal? PricePerTrip, decimal TripsAmount, decimal Adjustments,
    decimal Total, int PaidTrips, decimal PaidAmount, string PaymentStatus);

public sealed record StatementTrip(long TripId, string TripNumber, DateTime EnteredAt, int DriverId, string DriverName,
    int TruckId, string? FleetNumber, string? LoadStatus, string State, decimal Amount, decimal? PricePerTrip, int EffectiveTruckId);

/// <summary>A closed trip of the period that cannot be paid yet, and why.</summary>
public sealed record ExcludedTrip(long TripId, string TripNumber, DateTime EnteredAt, int TruckId, string? FleetNumber,
    string State, string Reason);

public sealed record AdjustmentItem(long Id, int TruckId, string? FleetNumber, long? TripId, string? TripNumber, decimal Amount,
    string Reason, string? CreatedByName, DateTime CreatedAt, long? BatchId, DateTime? AppliedAt);

public sealed record Statement(DateOnly From, DateOnly To, decimal? PricePerTrip, decimal PartialPercent, string Currency,
    IReadOnlyList<StatementLine> Lines, IReadOnlyList<StatementTrip> Trips, decimal Total, int NotYetPayable,
    IReadOnlyList<TruckStatementLine> Trucks, IReadOnlyList<ExcludedTrip> Excluded, IReadOnlyList<AdjustmentItem> Adjustments,
    decimal TripsTotal, decimal AdjustmentsTotal);

public sealed record PaymentBatchItem(long Id, DateOnly PeriodFrom, DateOnly PeriodTo, decimal? PricePerTrip, string Currency,
    int TripCount, decimal Total, string? Note, string? CreatedByName, DateTime CreatedAt, string Pricing, decimal AdjustmentsTotal);

/// <summary>Manager dashboard and the accountant's statements and payment batches.</summary>
public sealed class ReportService(Db db)
{
    public Task<Dashboard> DashboardAsync(CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            var tz = s.String("site.timezone", "Africa/Algiers");
            var day = await conn.ExecuteScalarAsync<DateTime>("SELECT (now() AT TIME ZONE @tz)::date::timestamp", new { tz });
            var p = new { tz, d = day };
            const string today = "(gs.scanned_at AT TIME ZONE @tz)::date = @d";
            var c = await conn.QuerySingleAsync<(int Entries, int Exits, int Accepted, int Pending, int Rejected)>($"""
                SELECT count(*) FILTER (WHERE direction = 'ENTRY')::int, count(*) FILTER (WHERE direction = 'EXIT')::int,
                       count(*) FILTER (WHERE direction = 'ENTRY' AND outcome = 'ACCEPT')::int,
                       count(*) FILTER (WHERE direction = 'ENTRY' AND outcome = 'PENDING')::int,
                       count(*) FILTER (WHERE direction = 'ENTRY' AND outcome = 'REJECT')::int
                FROM tg.gate_scans gs WHERE {today}
                """, p);
            var partial = await conn.ExecuteScalarAsync<int>($"""
                SELECT count(*)::int FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                WHERE {today} AND tr.load_status = 'PARTIAL'
                """, p);
            var inside = await conn.ExecuteScalarAsync<int>("""
                SELECT count(*)::int FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                WHERE tr.exit_scan_id IS NULL AND tr.current_state NOT IN ('AT_GATE') AND gs.scanned_at > now() - interval '3 days'
                """);
            var reviews = await conn.ExecuteScalarAsync<int>(
                "SELECT count(DISTINCT trip_id)::int FROM tg.reviews WHERE decision IS NULL AND NOT is_random_sample");
            var alerts = await conn.QuerySingleAsync<(int Critical, int Other)>("""
                SELECT count(*) FILTER (WHERE severity = 'CRITICAL')::int, count(*) FILTER (WHERE severity <> 'CRITICAL')::int
                FROM tg.alerts WHERE ack_at IS NULL
                """);
            var overdue = await conn.ExecuteScalarAsync<int>("""
                SELECT count(*)::int FROM tg.devices WHERE kind = 'QUARRY_PHONE' AND revoked_at IS NULL
                  AND coalesce(last_sync_at, enrolled_at) < now() - make_interval(secs => @maxAge)
                """, new { maxAge = s.Int("quarry.sync_max_age_s", 86400) });
            var byTruck = (await conn.QueryAsync<CountItem>($"""
                SELECT tr.truck_id::text AS key, t.fleet_number AS label, count(*)::int AS count
                FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id LEFT JOIN tg.trucks t ON t.id = tr.truck_id
                WHERE {today} GROUP BY tr.truck_id, t.fleet_number ORDER BY count DESC, label
                """, p)).ToList();
            var byDriver = (await conn.QueryAsync<CountItem>($"""
                SELECT tr.driver_id::text AS key, d.full_name AS label, count(*)::int AS count
                FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id LEFT JOIN tg.drivers d ON d.id = tr.driver_id
                WHERE {today} GROUP BY tr.driver_id, d.full_name ORDER BY count DESC, label
                """, p)).ToList();
            var byHour = (await conn.QueryAsync<CountItem>($"""
                SELECT lpad(extract(hour FROM gs.scanned_at AT TIME ZONE @tz)::text, 2, '0') AS key, NULL::text AS label,
                       count(*)::int AS count
                FROM tg.gate_scans gs WHERE {today} AND gs.direction = 'ENTRY' GROUP BY 1 ORDER BY 1
                """, p)).ToList();
            var recent = (await conn.QueryAsync<RecentScan>("""
                SELECT gs.id AS scanid, gs.scanned_at AS scannedat, gs.direction, gs.outcome, gs.reasons,
                       CASE WHEN gs.ticket_device_id IS NULL THEN NULL
                            ELSE 'Q' || gs.ticket_device_id || '-' || tg.fmt_seq(gs.ticket_seq) END AS ticketid,
                       (SELECT t.fleet_number FROM tg.tickets tk JOIN tg.trucks t ON t.id = tk.truck_id
                        WHERE tk.device_id = gs.ticket_device_id AND tk.seq = gs.ticket_seq) AS fleetnumber,
                       g.name AS gatename
                FROM tg.gate_scans gs LEFT JOIN tg.gates g ON g.id = gs.gate_id
                ORDER BY gs.id DESC LIMIT 25
                """)).ToList();
            var more = await conn.QuerySingleAsync<(int Trips, int Completed, int Pending, int Cameras, int Recordings, int SyncRejected, int Unclosed)>($"""
                SELECT (SELECT count(*)::int FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id WHERE {today}),
                       (SELECT count(*)::int FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                        WHERE {today} AND tr.current_state IN ('CLOSED', 'PAID')),
                       (SELECT count(*)::int FROM tg.trucks WHERE approval_status = 'PENDING_APPROVAL'),
                       (SELECT count(*)::int FROM tg.cameras c LEFT JOIN tg.camera_health h ON h.camera_id = c.id
                        WHERE c.active AND (h.recorder_status = 'ERROR'
                              OR (h.last_error_at IS NOT NULL AND h.last_error_at > coalesce(h.last_ok_at, '-infinity')
                                  AND h.last_error_at > now() - interval '1 day'))),
                       (SELECT count(*)::int FROM tg.video_recordings v WHERE v.status IN ('FAILED', 'PARTIAL')
                        AND (v.scan_at AT TIME ZONE @tz)::date = @d),
                       (SELECT count(DISTINCT device_id)::int FROM tg.sync_runs WHERE rejected > 0 AND at > now() - interval '1 day'),
                       (SELECT count(*)::int FROM tg.trips WHERE current_state IN ('AT_GATE', 'ENTERED', 'PENDING_REVIEW', 'EXITED_EMPTY'))
                """, p);
            return new Dashboard(DateOnly.FromDateTime(day), c.Entries, c.Exits, c.Accepted, c.Pending, c.Rejected, partial,
                inside, reviews, alerts.Critical, alerts.Other, overdue, byTruck, byDriver, byHour, recent,
                more.Trips, more.Completed, more.Pending, more.Trips, more.Cameras, more.Recordings, overdue + more.SyncRejected,
                more.Unclosed);
        }, ct);

    // ------------------------------------------------------------------ accountant

    /// <summary>
    /// Payable trips of the period (entry date), per truck and per driver. A trip is payable when it is
    /// CLOSED, its truck (or the truck it was merged into) is approved and has a price, its exit was
    /// recorded (setting pay.require_exit_scan) and no review is open. Its amount is the truck's price in
    /// force when it entered, a partial load at trip.partial_pay_percent. Pending adjustments are added.
    /// </summary>
    public Task<Statement> StatementAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            return await BuildStatementAsync(conn, null, s, from, to);
        }, ct);

    private static async Task<Statement> BuildStatementAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction? tx,
        SettingsBag s, DateOnly from, DateOnly to, bool forUpdate = false, IReadOnlyCollection<int>? onlyTrucks = null)
    {
        if (to < from || to.DayNumber - from.DayNumber > 366)
            throw TruckGateException.Validation("PERIOD_INVALID", "Choose a period of at most one year.");
        var tz = s.String("site.timezone", "Africa/Algiers");
        var partialPercent = s.Decimal("trip.partial_pay_percent", 100);
        var currency = s.String("trip.currency", "DZD");
        var requireExit = s.Bool("pay.require_exit_scan", true);
        var p = new { tz, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) };
        var rows = (await conn.QueryAsync<TripRow>($"""
            SELECT tr.id AS trip_id, tr.trip_number, gs.scanned_at AS entered_at, tr.driver_id,
                   coalesce(d.full_name, '#' || tr.driver_id) AS driver_name, coalesce(d.employment_type, '') AS employment_type,
                   tr.truck_id, tg.effective_truck_id(tr.truck_id) AS effective_truck_id, eff.fleet_number,
                   eff.owner_last_name, eff.owner_first_name,
                   (SELECT pl.plate_text FROM tg.truck_plates pl WHERE pl.truck_id = eff.id AND pl.valid_to IS NULL
                    ORDER BY pl.valid_from DESC LIMIT 1) AS plate,
                   tr.load_status, tr.current_state AS state, eff.approval_status, tr.exit_scan_id IS NOT NULL AS has_exit,
                   tg.truck_price_at(eff.id, gs.scanned_at) AS price,
                   EXISTS (SELECT 1 FROM tg.reviews r WHERE r.trip_id = tr.id AND r.decision IS NULL
                           AND NOT r.is_random_sample) AS open_review
            FROM tg.trips tr
            JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
            LEFT JOIN tg.drivers d ON d.id = tr.driver_id
            LEFT JOIN tg.trucks eff ON eff.id = tg.effective_truck_id(tr.truck_id)
            WHERE tr.current_state = 'CLOSED' AND (gs.scanned_at AT TIME ZONE @tz)::date BETWEEN @from AND @to
            ORDER BY gs.scanned_at
            {(forUpdate ? "FOR UPDATE OF tr" : "")}
            """, p, tx)).ToList();
        var notYet = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
            WHERE tr.current_state IN ('AT_GATE', 'ENTERED', 'PENDING_REVIEW', 'EXITED_EMPTY')
              AND (gs.scanned_at AT TIME ZONE @tz)::date BETWEEN @from AND @to
            """, p, tx);

        string? Why(TripRow r) =>
            r.ApprovalStatus is null ? "TRUCK_UNKNOWN"
            : r.ApprovalStatus != "APPROVED" ? "TRUCK_NOT_APPROVED"
            : r.Price is null ? "NO_PRICE"
            : requireExit && !r.HasExit ? "NO_EXIT"
            : r.OpenReview ? "OPEN_REVIEW"
            : null;
        decimal Amount(TripRow r) => r.LoadStatus == "PARTIAL" ? Math.Round(r.Price!.Value * partialPercent / 100m, 2) : r.Price!.Value;

        var eligible = rows.Where(r => Why(r) is null && (onlyTrucks is null || onlyTrucks.Contains(r.EffectiveTruckId))).ToList();
        var excluded = rows.Where(r => Why(r) is not null)
            .Select(r => new ExcludedTrip(r.TripId, r.TripNumber, r.EnteredAt, r.TruckId, r.FleetNumber, r.State, Why(r)!)).ToList();
        var detail = eligible.Select(r => new StatementTrip(r.TripId, r.TripNumber, r.EnteredAt, r.DriverId, r.DriverName,
            r.TruckId, r.FleetNumber, r.LoadStatus, r.State, Amount(r), r.Price, r.EffectiveTruckId)).ToList();

        var adjustments = (await conn.QueryAsync<AdjustmentItem>(AdjustmentSql + """
            WHERE a.batch_id IS NULL ORDER BY a.id
            """ + (forUpdate ? " FOR UPDATE OF a" : ""), transaction: tx)).ToList();
        if (onlyTrucks is not null) adjustments = adjustments.Where(a => onlyTrucks.Contains(a.TruckId)).ToList();

        var paid = (await conn.QueryAsync<(int TruckId, int Trips, decimal Amount)>("""
            SELECT coalesce(i.effective_truck_id, tg.effective_truck_id(i.truck_id)), count(*)::int, sum(i.amount)
            FROM tg.payment_items i JOIN tg.trips tr ON tr.id = i.trip_id JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
            WHERE (gs.scanned_at AT TIME ZONE @tz)::date BETWEEN @from AND @to
            GROUP BY 1
            """, p, tx)).ToDictionary(x => x.TruckId);
        var truckInfo = (await conn.QueryAsync<TruckInfoRow>("""
            SELECT t.id, t.fleet_number, t.owner_last_name, t.owner_first_name,
                   (SELECT pl.plate_text FROM tg.truck_plates pl WHERE pl.truck_id = t.id AND pl.valid_to IS NULL
                    ORDER BY pl.valid_from DESC LIMIT 1) AS plate,
                   (SELECT pr.price FROM tg.truck_prices pr WHERE pr.truck_id = t.id ORDER BY pr.valid_from DESC, pr.id DESC LIMIT 1) AS price
            FROM tg.trucks t WHERE t.id = ANY(@ids)
            """, new { ids = eligible.Select(r => r.EffectiveTruckId).Concat(adjustments.Select(a => a.TruckId))
                .Concat(onlyTrucks is null ? paid.Keys : paid.Keys.Where(onlyTrucks.Contains)).Distinct().ToArray() }, tx))
            .ToDictionary(t => t.Id);

        var truckLines = truckInfo.Values.Select(t =>
        {
            var trips = eligible.Where(r => r.EffectiveTruckId == t.Id).ToList();
            var adj = adjustments.Where(a => a.TruckId == t.Id).Sum(a => a.Amount);
            var tripsAmount = trips.Sum(Amount);
            paid.TryGetValue(t.Id, out var pd);
            var status = trips.Count > 0 || adj != 0 ? "UNPAID" : pd.Trips > 0 ? "PAID" : "NONE";
            return new TruckStatementLine(t.Id, t.FleetNumber, t.OwnerLastName, t.OwnerFirstName, t.Plate, trips.Count,
                trips.Count(x => x.LoadStatus != "PARTIAL"), trips.Count(x => x.LoadStatus == "PARTIAL"), t.Price, tripsAmount, adj,
                tripsAmount + adj, pd.Trips, pd.Amount, status);
        }).OrderBy(l => l.FleetNumber, StringComparer.CurrentCulture).ToList();

        var lines = eligible.GroupBy(r => (r.DriverId, r.DriverName, r.EmploymentType))
            .Select(g => new StatementLine(g.Key.DriverId, g.Key.DriverName, g.Key.EmploymentType, g.Count(),
                g.Count(x => x.LoadStatus != "PARTIAL"), g.Count(x => x.LoadStatus == "PARTIAL"), g.Sum(Amount)))
            .OrderBy(l => l.DriverName, StringComparer.CurrentCulture).ToList();
        var tripsTotal = detail.Sum(t => t.Amount);
        var adjTotal = adjustments.Sum(a => a.Amount);
        return new Statement(from, to, null, partialPercent, currency, lines, detail, tripsTotal + adjTotal, notYet, truckLines,
            excluded, adjustments, tripsTotal, adjTotal);
    }

    /// <summary>
    /// Marks the payable trips of the period as paid (CLOSED → PAID) with the price of each, applies the
    /// pending adjustments of the same trucks and records the batch. Only the given trucks when listed.
    /// </summary>
    public Task<long> CreatePaymentBatchAsync(DateOnly from, DateOnly to, string? note, int? createdBy, Actor actor,
        CancellationToken ct = default, IReadOnlyCollection<int>? truckIds = null) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('payment', 0))", transaction: tx);
            var s = await SettingsBag.LoadAsync(conn, tx);
            var st = await BuildStatementAsync(conn, tx, s, from, to, forUpdate: true,
                onlyTrucks: truckIds is { Count: > 0 } ? truckIds : null);
            if (st.Trips.Count == 0 && st.Adjustments.Count == 0)
            {
                if (st.Excluded.Any(e => e.Reason == "NO_PRICE"))
                    throw TruckGateException.Validation("PRICE_NOT_SET", "Set the price of the trucks first.");
                throw TruckGateException.Conflict("NOTHING_TO_PAY", "No payable trip in this period.");
            }
            var batchId = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO tg.payment_batches (period_from, period_to, price_per_trip, partial_percent, currency, trip_count, total,
                                                note, created_by, pricing, adjustments_total)
                VALUES (@from, @to, NULL, @partial, @currency, @count, @total, @note, @createdBy, 'PER_TRUCK', @adj)
                RETURNING id
                """, new
            {
                from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue), partial = st.PartialPercent,
                currency = st.Currency, count = st.Trips.Count, total = st.Total, adj = st.AdjustmentsTotal,
                note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), createdBy,
            }, tx);
            foreach (var t in st.Trips)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO tg.payment_items (batch_id, trip_id, driver_id, truck_id, load_status, amount, price_per_trip,
                                                  effective_truck_id)
                    VALUES (@batchId, @tripId, @driverId, @truckId, @load, @amount, @price, @eff)
                    """, new
                {
                    batchId, tripId = t.TripId, driverId = t.DriverId, truckId = t.TruckId, load = t.LoadStatus, amount = t.Amount,
                    price = t.PricePerTrip, eff = t.EffectiveTruckId,
                }, tx);
                await Trips.TransitionAsync(conn, tx, t.TripId, "CLOSED", "PAID", "PAYMENT_BATCH", $"batch {batchId}");
            }
            if (st.Adjustments.Count > 0)
                await conn.ExecuteAsync("""
                    UPDATE tg.payment_adjustments SET batch_id = @batchId, applied_at = now() WHERE id = ANY(@ids) AND batch_id IS NULL
                    """, new { batchId, ids = st.Adjustments.Select(a => a.Id).ToArray() }, tx);
            return batchId;
        }, ct);

    /// <summary>A correction (positive or negative) with its reason, paid in the next payment of the truck.</summary>
    public Task<long> AddAdjustmentAsync(int truckId, long? tripId, decimal amount, string reason, int? createdBy, Actor actor,
        CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length is 0 or > 1000) throw TruckGateException.Validation("REASON_REQUIRED", "A correction needs its reason.");
        if (amount == 0 || Math.Abs(amount) > 100_000_000 || decimal.Round(amount, 2) != amount)
            throw TruckGateException.Validation("AMOUNT_INVALID", "Type a non-zero amount (2 decimals at most).");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var status = await conn.ExecuteScalarAsync<string?>("SELECT approval_status FROM tg.trucks WHERE id = @truckId",
                new { truckId }, tx) ?? throw TruckGateException.NotFound("TRUCK_NOT_FOUND", $"Truck {truckId} does not exist.");
            if (status != "APPROVED") throw TruckGateException.Validation("TRUCK_NOT_APPROVED", "Only an approved truck is paid.");
            if (tripId is { } tid)
            {
                var eff = await conn.ExecuteScalarAsync<int?>("SELECT tg.effective_truck_id(truck_id) FROM tg.trips WHERE id = @tid",
                    new { tid }, tx) ?? throw TruckGateException.NotFound("TRIP_NOT_FOUND", $"Trip {tid} does not exist.");
                if (eff != truckId) throw TruckGateException.Validation("TRIP_OTHER_TRUCK", "This trip belongs to another truck.");
            }
            return await conn.ExecuteScalarAsync<long>("""
                INSERT INTO tg.payment_adjustments (truck_id, trip_id, amount, reason, created_by)
                VALUES (@truckId, @tripId, @amount, @why, @createdBy) RETURNING id
                """, new { truckId, tripId, amount, why, createdBy }, tx);
        }, ct);
    }

    public Task<IReadOnlyList<AdjustmentItem>> AdjustmentsAsync(bool pendingOnly, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<AdjustmentItem>>(async conn => (await conn.QueryAsync<AdjustmentItem>(AdjustmentSql +
            (pendingOnly ? "WHERE a.batch_id IS NULL " : "") + "ORDER BY a.id DESC LIMIT 500")).ToList(), ct);

    private const string AdjustmentSql = """
        SELECT a.id, a.truck_id AS truckid, t.fleet_number AS fleetnumber, a.trip_id AS tripid, tr.trip_number AS tripnumber,
               a.amount, a.reason, u.full_name AS createdbyname, a.created_at AS createdat, a.batch_id AS batchid,
               a.applied_at AS appliedat
        FROM tg.payment_adjustments a
        JOIN tg.trucks t ON t.id = a.truck_id
        LEFT JOIN tg.trips tr ON tr.id = a.trip_id
        LEFT JOIN tg.users u ON u.id = a.created_by

        """;

    public Task<IReadOnlyList<PaymentBatchItem>> BatchesAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<PaymentBatchItem>>(async conn => (await conn.QueryAsync<BatchRow>("""
            SELECT b.id, b.period_from, b.period_to, b.price_per_trip, b.partial_percent, b.currency, b.trip_count, b.total, b.note,
                   u.full_name AS created_by_name, b.created_at, b.pricing, b.adjustments_total
            FROM tg.payment_batches b LEFT JOIN tg.users u ON u.id = b.created_by ORDER BY b.id DESC LIMIT 200
            """)).Select(b => new PaymentBatchItem(b.Id, DateOnly.FromDateTime(b.PeriodFrom), DateOnly.FromDateTime(b.PeriodTo),
                b.PricePerTrip, b.Currency, b.TripCount, b.Total, b.Note, b.CreatedByName, b.CreatedAt, b.Pricing,
                b.AdjustmentsTotal)).ToList(), ct);

    /// <summary>CSV for Excel (UTF-8 with BOM, semicolons): per truck, trip detail, adjustments, per driver.</summary>
    public static byte[] StatementCsv(Statement st, string lang)
    {
        bool fr = lang == "fr";
        string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', fr
            ? ["Camion", "Nom", "Prénom", "Matricule", "Voyages", "Prix", "Montant voyages", "Corrections", "Total", "Statut"]
            : new[] { "الشاحنة", "اللقب", "الاسم", "الماطريكول", "الرحلات", "السعر", "مبلغ الرحلات", "تسويات", "المجموع", "الحالة" }));
        foreach (var l in st.Trucks)
            sb.AppendLine(string.Join(';', Csv(l.FleetNumber ?? l.TruckId.ToString(CultureInfo.InvariantCulture)),
                Csv(l.OwnerLastName ?? ""), Csv(l.OwnerFirstName ?? ""), Csv(l.Plate ?? ""), l.Trips,
                l.PricePerTrip is { } pr ? M(pr) : "", M(l.TripsAmount), M(l.Adjustments), M(l.Total), l.PaymentStatus));
        sb.AppendLine(string.Join(';', fr ? "Total" : "المجموع", "", "", "", st.Trips.Count, "", M(st.TripsTotal), M(st.AdjustmentsTotal),
            M(st.Total), ""));
        sb.AppendLine();
        sb.AppendLine(string.Join(';', fr
            ? ["Voyage", "Date", "Chauffeur", "Camion", "Chargement", "État", "Prix", "Montant"]
            : new[] { "الرحلة", "التاريخ", "السائق", "الشاحنة", "الحمولة", "الحالة", "السعر", "المبلغ" }));
        foreach (var t in st.Trips)
            sb.AppendLine(string.Join(';', Csv(t.TripNumber), t.EnteredAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Csv(t.DriverName), Csv(t.FleetNumber ?? t.TruckId.ToString(CultureInfo.InvariantCulture)), Csv(t.LoadStatus ?? ""),
                t.State, t.PricePerTrip is { } pp ? M(pp) : "", M(t.Amount)));
        if (st.Adjustments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(string.Join(';', fr ? ["Correction", "Camion", "Voyage", "Montant", "Motif"]
                : new[] { "التسوية", "الشاحنة", "الرحلة", "المبلغ", "السبب" }));
            foreach (var a in st.Adjustments)
                sb.AppendLine(string.Join(';', a.Id, Csv(a.FleetNumber ?? ""), Csv(a.TripNumber ?? ""), M(a.Amount), Csv(a.Reason)));
        }
        sb.AppendLine();
        sb.AppendLine(string.Join(';', fr ? ["Chauffeur", "Voyages", "Complets", "Partiels", "Montant"]
            : new[] { "السائق", "الرحلات", "كاملة", "ناقصة", "المبلغ" }));
        foreach (var l in st.Lines)
            sb.AppendLine(string.Join(';', Csv(l.DriverName), l.Trips, l.Full, l.Partial, M(l.Amount)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public Task<Statement> BatchStatementAsync(long batchId, CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var b = await conn.QuerySingleOrDefaultAsync<BatchRow>("""
                SELECT id, period_from, period_to, price_per_trip, partial_percent, currency, trip_count, total, note, created_at,
                       pricing, adjustments_total
                FROM tg.payment_batches WHERE id = @batchId
                """, new { batchId }) ?? throw TruckGateException.NotFound("BATCH_NOT_FOUND", $"Payment {batchId} does not exist.");
            var trips = (await conn.QueryAsync<TripRow>("""
                SELECT tr.id AS trip_id, tr.trip_number, gs.scanned_at AS entered_at, i.driver_id,
                       coalesce(d.full_name, '#' || i.driver_id) AS driver_name, coalesce(d.employment_type, '') AS employment_type,
                       i.truck_id, coalesce(i.effective_truck_id, tg.effective_truck_id(i.truck_id)) AS effective_truck_id,
                       eff.fleet_number, eff.owner_last_name, eff.owner_first_name,
                       (SELECT pl.plate_text FROM tg.truck_plates pl WHERE pl.truck_id = eff.id AND pl.valid_to IS NULL
                        ORDER BY pl.valid_from DESC LIMIT 1) AS plate,
                       i.load_status, tr.current_state AS state, eff.approval_status, tr.exit_scan_id IS NOT NULL AS has_exit,
                       coalesce(i.price_per_trip, b.price_per_trip) AS price, false AS open_review, i.amount
                FROM tg.payment_items i JOIN tg.payment_batches b ON b.id = i.batch_id
                JOIN tg.trips tr ON tr.id = i.trip_id JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                LEFT JOIN tg.drivers d ON d.id = i.driver_id
                LEFT JOIN tg.trucks eff ON eff.id = coalesce(i.effective_truck_id, tg.effective_truck_id(i.truck_id))
                WHERE i.batch_id = @batchId ORDER BY gs.scanned_at
                """, new { batchId })).ToList();
            var adjustments = (await conn.QueryAsync<AdjustmentItem>(AdjustmentSql + "WHERE a.batch_id = @batchId ORDER BY a.id",
                new { batchId })).ToList();
            var detail = trips.Select(r => new StatementTrip(r.TripId, r.TripNumber, r.EnteredAt, r.DriverId, r.DriverName,
                r.TruckId, r.FleetNumber, r.LoadStatus, r.State, r.Amount, r.Price, r.EffectiveTruckId)).ToList();
            var lines = trips.GroupBy(r => (r.DriverId, r.DriverName, r.EmploymentType))
                .Select(g => new StatementLine(g.Key.DriverId, g.Key.DriverName, g.Key.EmploymentType, g.Count(),
                    g.Count(x => x.LoadStatus != "PARTIAL"), g.Count(x => x.LoadStatus == "PARTIAL"), g.Sum(x => x.Amount)))
                .OrderBy(l => l.DriverName, StringComparer.CurrentCulture).ToList();
            var truckIds = trips.Select(t => t.EffectiveTruckId).Concat(adjustments.Select(a => a.TruckId)).Distinct();
            var truckLines = truckIds.Select(id =>
            {
                var ts = trips.Where(t => t.EffectiveTruckId == id).ToList();
                var any = ts.FirstOrDefault();
                var adj = adjustments.Where(a => a.TruckId == id).Sum(a => a.Amount);
                var amount = ts.Sum(t => t.Amount);
                return new TruckStatementLine(id, any?.FleetNumber ?? adjustments.FirstOrDefault(a => a.TruckId == id)?.FleetNumber,
                    any?.OwnerLastName, any?.OwnerFirstName, any?.Plate, ts.Count, ts.Count(x => x.LoadStatus != "PARTIAL"),
                    ts.Count(x => x.LoadStatus == "PARTIAL"), any?.Price, amount, adj, amount + adj, ts.Count, amount + adj, "PAID");
            }).OrderBy(l => l.FleetNumber, StringComparer.CurrentCulture).ToList();
            var tripsTotal = detail.Sum(t => t.Amount);
            return new Statement(DateOnly.FromDateTime(b.PeriodFrom), DateOnly.FromDateTime(b.PeriodTo), b.PricePerTrip,
                b.PartialPercent, b.Currency, lines, detail, b.Total, 0, truckLines, [], adjustments, tripsTotal, b.AdjustmentsTotal);
        }, ct);

    private static string Csv(string v) =>
        v.IndexOfAny([';', '"', '\n', '\r']) >= 0 || v.StartsWith('=') || v.StartsWith('+') || v.StartsWith('-') || v.StartsWith('@')
            ? "\"" + (v.StartsWith('=') || v.StartsWith('+') || v.StartsWith('-') || v.StartsWith('@') ? "'" : "") + v.Replace("\"", "\"\"") + "\""
            : v;

    private sealed class TripRow
    {
        public long TripId { get; set; }
        public string TripNumber { get; set; } = "";
        public DateTime EnteredAt { get; set; }
        public int DriverId { get; set; }
        public string DriverName { get; set; } = "";
        public string EmploymentType { get; set; } = "";
        public int TruckId { get; set; }
        public int EffectiveTruckId { get; set; }
        public string? FleetNumber { get; set; }
        public string? OwnerLastName { get; set; }
        public string? OwnerFirstName { get; set; }
        public string? Plate { get; set; }
        public string? LoadStatus { get; set; }
        public string State { get; set; } = "";
        public string? ApprovalStatus { get; set; }
        public bool HasExit { get; set; }
        public decimal? Price { get; set; }
        public bool OpenReview { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class TruckInfoRow
    {
        public int Id { get; set; }
        public string? FleetNumber { get; set; }
        public string? OwnerLastName { get; set; }
        public string? OwnerFirstName { get; set; }
        public string? Plate { get; set; }
        public decimal? Price { get; set; }
    }

    private sealed class BatchRow
    {
        public long Id { get; set; }
        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }
        public decimal? PricePerTrip { get; set; }
        public decimal PartialPercent { get; set; }
        public string Currency { get; set; } = "";
        public int TripCount { get; set; }
        public decimal Total { get; set; }
        public string? Note { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Pricing { get; set; } = "";
        public decimal AdjustmentsTotal { get; set; }
    }
}
