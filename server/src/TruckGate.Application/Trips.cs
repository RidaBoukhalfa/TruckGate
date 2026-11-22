using Dapper;
using Npgsql;

namespace TruckGate.Application;

/// <summary>Trip state changes and reviews, shared by the gate, the review screen and the watchdog.</summary>
internal static class Trips
{
    /// <summary>Reasons that put a review at the top of the supervisor's queue.</summary>
    public static readonly HashSet<string> HighPriorityReasons = new(StringComparer.Ordinal)
    {
        "ISSUED_IN_FUTURE", "TOO_EARLY", "CLOCK_MISMATCH", "ISSUED_BEFORE_REVOCATION", "OUTSIDE_GEOFENCE",
        "GATE_EMPTY", "GATE_DIFFERENT_TRUCK", "EXIT_LOADED", "EXIT_TOO_SOON",
    };

    /// <summary>The database checks the transition against tg.trip_transitions and the current state.</summary>
    public static Task TransitionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long tripId,
        string from, string to, string reason, string? note = null) =>
        conn.ExecuteAsync("""
            INSERT INTO tg.trip_state_events (trip_id, from_state, to_state, reason_code, note)
            VALUES (@trip, @from, @to, @reason, @note)
            """, new { trip = tripId, from, to, reason, note }, tx);

    public static Task<long> OpenReviewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long tripId,
        IReadOnlyCollection<string> reasons, bool sample = false)
    {
        var priority = sample ? "LOW" : reasons.Any(HighPriorityReasons.Contains) ? "HIGH" : "NORMAL";
        return conn.ExecuteScalarAsync<long>("""
            INSERT INTO tg.reviews (trip_id, reason_codes, priority, is_random_sample)
            VALUES (@trip, @reasons, @priority, @sample)
            RETURNING id
            """, new { trip = tripId, reasons = reasons.ToArray(), priority, sample }, tx);
    }

    /// <summary>Open reviews that block payment (random samples do not).</summary>
    public static Task<bool> HasBlockingReviewAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long tripId) =>
        conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM tg.reviews WHERE trip_id = @tripId AND decision IS NULL AND NOT is_random_sample)
            """, new { tripId }, tx);

    public static async Task<TripLock> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long tripId) =>
        await conn.QuerySingleOrDefaultAsync<TripLock>("""
            SELECT id, trip_number, current_state, truck_id, driver_id, ticket_device_id, ticket_seq,
                   entry_scan_id, exit_scan_id, load_status
            FROM tg.trips WHERE id = @tripId FOR UPDATE
            """, new { tripId }, tx)
        ?? throw TruckGateException.NotFound("TRIP_NOT_FOUND", $"Trip {tripId} does not exist.");

    internal sealed class TripLock
    {
        public long Id { get; set; }
        public string TripNumber { get; set; } = "";
        public string CurrentState { get; set; } = "";
        public int TruckId { get; set; }
        public int DriverId { get; set; }
        public int TicketDeviceId { get; set; }
        public long TicketSeq { get; set; }
        public long EntryScanId { get; set; }
        public long? ExitScanId { get; set; }
        public string? LoadStatus { get; set; }
    }
}
