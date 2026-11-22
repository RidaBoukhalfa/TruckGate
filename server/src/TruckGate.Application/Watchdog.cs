using Dapper;
using Npgsql;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record WatchdogReport(int UndecidedEntries, int NoExit, int SyncOverdue, int SampleTrips);

/// <summary>
/// Periodic checks (every minute): entries left undecided, trucks that never came out, phones that
/// stopped syncing, the daily random sample. Each alert is raised once per subject and period.
/// </summary>
public sealed class Watchdog(Db db)
{
    private static readonly Actor WatchdogActor = Actor.System("watchdog");

    public Task<WatchdogReport> RunOnceAsync(CancellationToken ct = default) =>
        db.InTransactionAsync(WatchdogActor, async (conn, tx) =>
        {
            // one watchdog at a time, even with two server processes
            if (!await conn.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_xact_lock(hashtextextended('watchdog', 0))",
                    transaction: tx))
                return new WatchdogReport(0, 0, 0, 0);
            var s = await SettingsBag.LoadAsync(conn, tx);

            // 1. entry scanned but no decision
            var undecided = (await conn.QueryAsync<(long Id, string Number)>("""
                SELECT tr.id, tr.trip_number FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                WHERE tr.current_state = 'AT_GATE' AND gs.scanned_at < now() - make_interval(secs => @limit)
                """, new { limit = s.Int("gate.entry_decision_alert_s", 600) }, tx)).ToList();
            int undecidedAlerts = 0;
            foreach (var (id, number) in undecided)
                if (await AlertOnceAsync(conn, tx, "ENTRY_NOT_DECIDED", "MEDIUM", "trips", id.ToString(), "once",
                        new { trip_number = number }))
                    undecidedAlerts++;

            // 2. truck entered but never scanned out
            var missing = (await conn.QueryAsync<(long Id, string Number, string State)>("""
                SELECT tr.id, tr.trip_number, tr.current_state FROM tg.trips tr JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                WHERE tr.exit_scan_id IS NULL AND tr.current_state IN ('ENTERED', 'PENDING_REVIEW')
                  AND gs.scanned_at < now() - make_interval(secs => @deadline)
                  AND NOT EXISTS (SELECT 1 FROM tg.reviews r WHERE r.trip_id = tr.id AND 'NO_EXIT_SCAN' = ANY (r.reason_codes))
                """, new { deadline = s.Int("trip.exit_deadline_s", 7200) }, tx)).ToList();
            foreach (var (id, number, state) in missing)
            {
                if (state == "ENTERED") await Trips.TransitionAsync(conn, tx, id, "ENTERED", "PENDING_REVIEW", "NO_EXIT_SCAN");
                await Trips.OpenReviewAsync(conn, tx, id, ["NO_EXIT_SCAN"]);
                await AlertOnceAsync(conn, tx, "NO_EXIT_SCAN", "MEDIUM", "trips", id.ToString(), "once", new { trip_number = number });
            }

            // 3. quarry phones that have not synced
            var tz = s.String("site.timezone", "Africa/Algiers");
            var today = await conn.ExecuteScalarAsync<DateTime>("SELECT (now() AT TIME ZONE @tz)::date::timestamp", new { tz }, tx);
            var overdue = (await conn.QueryAsync<(int Id, string Label)>("""
                SELECT id, label FROM tg.devices
                WHERE kind = 'QUARRY_PHONE' AND revoked_at IS NULL
                  AND coalesce(last_sync_at, enrolled_at) < now() - make_interval(secs => @maxAge)
                """, new { maxAge = s.Int("quarry.sync_max_age_s", 86400) }, tx)).ToList();
            int overdueAlerts = 0;
            foreach (var (id, label) in overdue)
                if (await AlertOnceAsync(conn, tx, "SYNC_OVERDUE", "MEDIUM", "devices", id.ToString(), today.ToString("yyyy-MM-dd"),
                        new { device_id = id, label }))
                    overdueAlerts++;

            // 4. yesterday's random sample, once the day is over
            var yesterday = DateOnly.FromDateTime(today).AddDays(-1);
            var sampled = await ReviewService.CreateDailySampleAsync(conn, tx, yesterday, tz, s.Decimal("review.sample_percent", 5));

            return new WatchdogReport(undecidedAlerts, missing.Count, overdueAlerts, sampled);
        }, ct);

    /// <summary>
    /// A driver (or a quarry operator) with too many partial loads in the window raises one alert per day.
    /// Called when the gatekeeper records a partial load.
    /// </summary>
    internal static async Task CheckRepeatedPartialsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long tripId)
    {
        var s = await SettingsBag.LoadAsync(conn, tx);
        int threshold = s.Int("trip.partial_alert_threshold", 3);
        int days = s.Int("trip.partial_alert_window_days", 7);
        if (threshold <= 0) return;
        var trip = await conn.QuerySingleAsync<(int DriverId, int OperatorId)>("""
            SELECT tr.driver_id, tk.operator_id FROM tg.trips tr
            JOIN tg.tickets tk ON tk.device_id = tr.ticket_device_id AND tk.seq = tr.ticket_seq WHERE tr.id = @tripId
            """, new { tripId }, tx);
        var today = (await conn.ExecuteScalarAsync<DateTime>("SELECT now()::date::timestamp", transaction: tx)).ToString("yyyy-MM-dd");

        var byDriver = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM tg.trips WHERE driver_id = @d AND load_status = 'PARTIAL'
              AND created_at > now() - make_interval(days => @days)
            """, new { d = trip.DriverId, days }, tx);
        if (byDriver >= threshold)
            await AlertOnceAsync(conn, tx, "PARTIAL_LOADS_DRIVER", "MEDIUM", "drivers", trip.DriverId.ToString(), today,
                new { driver_id = trip.DriverId, count = byDriver, days });

        var byOperator = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM tg.trips tr JOIN tg.tickets tk ON tk.device_id = tr.ticket_device_id AND tk.seq = tr.ticket_seq
            WHERE tk.operator_id = @o AND tr.load_status = 'PARTIAL' AND tr.created_at > now() - make_interval(days => @days)
            """, new { o = trip.OperatorId, days }, tx);
        if (byOperator >= threshold)
            await AlertOnceAsync(conn, tx, "PARTIAL_LOADS_OPERATOR", "MEDIUM", "users", trip.OperatorId.ToString(), today,
                new { operator_id = trip.OperatorId, count = byOperator, days });
    }

    /// <summary>Inserts the alert unless one of this type was already raised for the subject in this period.</summary>
    internal static async Task<bool> AlertOnceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string type, string severity,
        string entity, string entityId, string period, object parameters)
    {
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))",
            new { k = $"alert:{type}:{entityId}:{period}" }, tx);
        if (await conn.ExecuteScalarAsync<bool>("""
                SELECT EXISTS (SELECT 1 FROM tg.alert_once WHERE type = @type AND entity_id = @entityId AND period = @period)
                """, new { type, entityId, period }, tx))
            return false;
        var alertId = await GateService.InsertAlertAsync(conn, tx, type, severity, entity, entityId, parameters);
        await conn.ExecuteAsync("INSERT INTO tg.alert_once (type, entity_id, period, alert_id) VALUES (@type, @entityId, @period, @alertId)",
            new { type, entityId, period, alertId }, tx);
        return true;
    }
}
