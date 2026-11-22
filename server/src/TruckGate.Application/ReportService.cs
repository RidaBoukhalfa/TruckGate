using System.Globalization;
using System.Text;
using Dapper;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record CountItem(string Key, string? Label, int Count);

public sealed record RecentScan(long ScanId, DateTime ScannedAt, string Direction, string Outcome, string[] Reasons,
    string? TicketId, string? FleetNumber, string? GateName);

public sealed record Dashboard(DateOnly Day, int Entries, int Exits, int Accepted, int Pending, int Rejected, int Partial,
    int Inside, int OpenReviews, int OpenAlertsCritical, int OpenAlertsOther, int PhonesOverdue,
    IReadOnlyList<CountItem> ByTruck, IReadOnlyList<CountItem> ByDriver, IReadOnlyList<CountItem> ByHour,
    IReadOnlyList<RecentScan> Recent);

public sealed record StatementLine(int DriverId, string DriverName, string EmploymentType, int Trips, int Full, int Partial,
    decimal Amount);

public sealed record StatementTrip(long TripId, string TripNumber, DateTime EnteredAt, int DriverId, string DriverName,
    int TruckId, string? FleetNumber, string? LoadStatus, string State, decimal Amount);

public sealed record Statement(DateOnly From, DateOnly To, decimal PricePerTrip, decimal PartialPercent, string Currency,
    IReadOnlyList<StatementLine> Lines, IReadOnlyList<StatementTrip> Trips, decimal Total, int NotYetPayable);

public sealed record PaymentBatchItem(long Id, DateOnly PeriodFrom, DateOnly PeriodTo, decimal PricePerTrip, string Currency,
    int TripCount, decimal Total, string? Note, string? CreatedByName, DateTime CreatedAt);

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
            return new Dashboard(DateOnly.FromDateTime(day), c.Entries, c.Exits, c.Accepted, c.Pending, c.Rejected, partial,
                inside, reviews, alerts.Critical, alerts.Other, overdue, byTruck, byDriver, byHour, recent);
        }, ct);

    // ------------------------------------------------------------------ accountant

    /// <summary>
    /// Payable trips (CLOSED, not yet paid) whose entry falls in the period, per driver. A partial load is
    /// paid at trip.partial_pay_percent (100 % by the agreed rule) and stays marked in the detail.
    /// </summary>
    public Task<Statement> StatementAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            return await BuildStatementAsync(conn, null, s, from, to);
        }, ct);

    private static async Task<Statement> BuildStatementAsync(Npgsql.NpgsqlConnection conn, Npgsql.NpgsqlTransaction? tx,
        SettingsBag s, DateOnly from, DateOnly to, bool forUpdate = false)
    {
        if (to < from || to.DayNumber - from.DayNumber > 366)
            throw TruckGateException.Validation("PERIOD_INVALID", "Choose a period of at most one year.");
        var tz = s.String("site.timezone", "Africa/Algiers");
        var price = s.Decimal("trip.price_per_trip", 0);
        var partialPercent = s.Decimal("trip.partial_pay_percent", 100);
        var currency = s.String("trip.currency", "DZD");
        var p = new { tz, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) };
        var trips = (await conn.QueryAsync<TripRow>($"""
            SELECT tr.id AS trip_id, tr.trip_number, gs.scanned_at AS entered_at, tr.driver_id,
                   coalesce(d.full_name, '#' || tr.driver_id) AS driver_name, coalesce(d.employment_type, '') AS employment_type,
                   tr.truck_id, t.fleet_number, tr.load_status, tr.current_state AS state
            FROM tg.trips tr
            JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
            LEFT JOIN tg.drivers d ON d.id = tr.driver_id
            LEFT JOIN tg.trucks t ON t.id = tr.truck_id
            WHERE tr.current_state = 'CLOSED' AND (gs.scanned_at AT TIME ZONE @tz)::date BETWEEN @from AND @to
            ORDER BY gs.scanned_at
            {(forUpdate ? "FOR UPDATE OF tr" : "")}
            """, p, tx)).ToList();
        var notYet = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
            WHERE tr.current_state IN ('AT_GATE', 'ENTERED', 'PENDING_REVIEW', 'EXITED_EMPTY')
              AND (gs.scanned_at AT TIME ZONE @tz)::date BETWEEN @from AND @to
            """, p, tx);

        decimal Amount(string? load) => load == "PARTIAL" ? Math.Round(price * partialPercent / 100m, 2) : price;
        var detail = trips.Select(r => new StatementTrip(r.TripId, r.TripNumber, r.EnteredAt, r.DriverId, r.DriverName,
            r.TruckId, r.FleetNumber, r.LoadStatus, r.State, Amount(r.LoadStatus))).ToList();
        var lines = trips.GroupBy(r => (r.DriverId, r.DriverName, r.EmploymentType))
            .Select(g => new StatementLine(g.Key.DriverId, g.Key.DriverName, g.Key.EmploymentType, g.Count(),
                g.Count(x => x.LoadStatus != "PARTIAL"), g.Count(x => x.LoadStatus == "PARTIAL"), g.Sum(x => Amount(x.LoadStatus))))
            .OrderBy(l => l.DriverName, StringComparer.CurrentCulture).ToList();
        return new Statement(from, to, price, partialPercent, currency, lines, detail, lines.Sum(l => l.Amount), notYet);
    }

    /// <summary>Marks every payable trip of the period as paid (CLOSED → PAID) and records the batch.</summary>
    public Task<long> CreatePaymentBatchAsync(DateOnly from, DateOnly to, string? note, int? createdBy, Actor actor,
        CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('payment', 0))", transaction: tx);
            var s = await SettingsBag.LoadAsync(conn, tx);
            var st = await BuildStatementAsync(conn, tx, s, from, to, forUpdate: true);
            if (st.PricePerTrip <= 0) throw TruckGateException.Validation("PRICE_NOT_SET", "Set the price per trip first.");
            if (st.Trips.Count == 0) throw TruckGateException.Conflict("NOTHING_TO_PAY", "No payable trip in this period.");
            var batchId = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO tg.payment_batches (period_from, period_to, price_per_trip, partial_percent, currency, trip_count, total,
                                                note, created_by)
                VALUES (@from, @to, @price, @partial, @currency, @count, @total, @note, @createdBy)
                RETURNING id
                """, new
            {
                from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue), price = st.PricePerTrip,
                partial = st.PartialPercent, currency = st.Currency, count = st.Trips.Count, total = st.Total,
                note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), createdBy,
            }, tx);
            foreach (var t in st.Trips)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO tg.payment_items (batch_id, trip_id, driver_id, truck_id, load_status, amount)
                    VALUES (@batchId, @tripId, @driverId, @truckId, @load, @amount)
                    """, new { batchId, tripId = t.TripId, driverId = t.DriverId, truckId = t.TruckId, load = t.LoadStatus, amount = t.Amount }, tx);
                await Trips.TransitionAsync(conn, tx, t.TripId, "CLOSED", "PAID", "PAYMENT_BATCH", $"batch {batchId}");
            }
            return batchId;
        }, ct);

    public Task<IReadOnlyList<PaymentBatchItem>> BatchesAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<PaymentBatchItem>>(async conn => (await conn.QueryAsync<BatchRow>("""
            SELECT b.id, b.period_from, b.period_to, b.price_per_trip, b.currency, b.trip_count, b.total, b.note,
                   u.full_name AS created_by_name, b.created_at
            FROM tg.payment_batches b LEFT JOIN tg.users u ON u.id = b.created_by ORDER BY b.id DESC LIMIT 200
            """)).Select(b => new PaymentBatchItem(b.Id, DateOnly.FromDateTime(b.PeriodFrom), DateOnly.FromDateTime(b.PeriodTo),
                b.PricePerTrip, b.Currency, b.TripCount, b.Total, b.Note, b.CreatedByName, b.CreatedAt)).ToList(), ct);

    /// <summary>CSV for Excel (UTF-8 with BOM, semicolons) of a statement or of a recorded batch.</summary>
    public static byte[] StatementCsv(Statement st, string lang)
    {
        bool fr = lang == "fr";
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', fr
            ? ["Voyage", "Date", "Chauffeur", "Camion", "Chargement", "État", "Montant"]
            : new[] { "الرحلة", "التاريخ", "السائق", "الشاحنة", "الحمولة", "الحالة", "المبلغ" }));
        foreach (var t in st.Trips)
            sb.AppendLine(string.Join(';', Csv(t.TripNumber), t.EnteredAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Csv(t.DriverName), Csv(t.FleetNumber ?? t.TruckId.ToString(CultureInfo.InvariantCulture)), Csv(t.LoadStatus ?? ""),
                t.State, t.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        sb.AppendLine();
        sb.AppendLine(string.Join(';', fr ? ["Chauffeur", "Voyages", "Complets", "Partiels", "Montant"]
            : new[] { "السائق", "الرحلات", "كاملة", "ناقصة", "المبلغ" }));
        foreach (var l in st.Lines)
            sb.AppendLine(string.Join(';', Csv(l.DriverName), l.Trips, l.Full, l.Partial, l.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        sb.AppendLine(string.Join(';', fr ? "Total" : "المجموع", st.Trips.Count, "", "", st.Total.ToString("0.00", CultureInfo.InvariantCulture)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public Task<Statement> BatchStatementAsync(long batchId, CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var b = await conn.QuerySingleOrDefaultAsync<BatchRow>("""
                SELECT id, period_from, period_to, price_per_trip, partial_percent, currency, trip_count, total, note, created_at
                FROM tg.payment_batches WHERE id = @batchId
                """, new { batchId }) ?? throw TruckGateException.NotFound("BATCH_NOT_FOUND", $"Payment {batchId} does not exist.");
            var trips = (await conn.QueryAsync<TripRow>("""
                SELECT tr.id AS trip_id, tr.trip_number, gs.scanned_at AS entered_at, i.driver_id,
                       coalesce(d.full_name, '#' || i.driver_id) AS driver_name, coalesce(d.employment_type, '') AS employment_type,
                       i.truck_id, t.fleet_number, i.load_status, tr.current_state AS state, i.amount
                FROM tg.payment_items i JOIN tg.trips tr ON tr.id = i.trip_id JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                LEFT JOIN tg.drivers d ON d.id = i.driver_id LEFT JOIN tg.trucks t ON t.id = i.truck_id
                WHERE i.batch_id = @batchId ORDER BY gs.scanned_at
                """, new { batchId })).ToList();
            var detail = trips.Select(r => new StatementTrip(r.TripId, r.TripNumber, r.EnteredAt, r.DriverId, r.DriverName,
                r.TruckId, r.FleetNumber, r.LoadStatus, r.State, r.Amount)).ToList();
            var lines = trips.GroupBy(r => (r.DriverId, r.DriverName, r.EmploymentType))
                .Select(g => new StatementLine(g.Key.DriverId, g.Key.DriverName, g.Key.EmploymentType, g.Count(),
                    g.Count(x => x.LoadStatus != "PARTIAL"), g.Count(x => x.LoadStatus == "PARTIAL"), g.Sum(x => x.Amount)))
                .OrderBy(l => l.DriverName, StringComparer.CurrentCulture).ToList();
            return new Statement(DateOnly.FromDateTime(b.PeriodFrom), DateOnly.FromDateTime(b.PeriodTo), b.PricePerTrip,
                b.PartialPercent, b.Currency, lines, detail, b.Total, 0);
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
        public string? FleetNumber { get; set; }
        public string? LoadStatus { get; set; }
        public string State { get; set; } = "";
        public decimal Amount { get; set; }
    }

    private sealed class BatchRow
    {
        public long Id { get; set; }
        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }
        public decimal PricePerTrip { get; set; }
        public decimal PartialPercent { get; set; }
        public string Currency { get; set; } = "";
        public int TripCount { get; set; }
        public decimal Total { get; set; }
        public string? Note { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
