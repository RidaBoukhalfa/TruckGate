using System.Globalization;
using System.Text;
using Dapper;
using Npgsql;
using TruckGate.Data;

namespace TruckGate.Application;

/// <summary>Truck ids 40000..65535 are reserved for trucks registered from a quarry phone.</summary>
public static class ProvisionalTruckIds
{
    public const int First = 40000;
    public const int Last = 65535;
    public const int BlockSize = 10;
    public const int MinFree = 5;
    public static bool Contains(int id) => id is >= First and <= Last;
}

/// <summary>Registration numbers ("matricule"): digits only, written NNNNN NNN NN when the length allows it.</summary>
public static class PlateRules
{
    /// <summary>Arabic-Indic and Persian digits become ASCII; anything that is not a digit is dropped.</summary>
    public static string Key(string? plate)
    {
        var sb = new StringBuilder();
        foreach (var c in plate ?? "")
        {
            if (c is >= '0' and <= '9') sb.Append(c);
            else if (c is >= '٠' and <= '٩') sb.Append((char)('0' + (c - '٠')));
            else if (c is >= '۰' and <= '۹') sb.Append((char)('0' + (c - '۰')));
        }
        return sb.ToString();
    }

    private static bool IsPlateDigit(char c) =>
        c is (>= '0' and <= '9') or (>= '\u0660' and <= '\u0669') or (>= '\u06F0' and <= '\u06F9');

    /// <summary>
    /// Null when the text is not a plausible Algerian registration number (6 to 12 digits; only digits,
    /// spaces, dashes, dots and slashes). 10 or 11 digits are written "serial category+year wilaya".
    /// </summary>
    public static string? Normalize(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return null;
        foreach (var c in plate)
            if (!(IsPlateDigit(c) || c is ' ' or '-' or '.' or '/' or '_')) return null;
        var digits = Key(plate);
        if (digits.Length is < 6 or > 12) return null;
        if (digits.Length is 10 or 11)
            return $"{digits[..^5]} {digits[^5..^2]} {digits[^2..]}";
        return AdminService.NormalizePlate(new string(plate.Select(c => IsPlateDigit(c) ? Key(c.ToString())[0] : c).ToArray()));
    }
}

public sealed record SyncTruck(int Id, string Plate, long CreatedAt, int? OperatorId, string? Note);

public sealed record SyncTruckResult(int Id, string Status, string? Error);

public sealed record TruckApproval(string FleetNumber, string OwnerLastName, string OwnerFirstName, string Plate, decimal Price,
    string? Model, int? DefaultDriverId);

public sealed record TruckCreate(int? Id, string FleetNumber, string OwnerLastName, string OwnerFirstName, string Plate, decimal Price,
    string? Model, int? DefaultDriverId);

public sealed record TruckEdit(string FleetNumber, string? OwnerLastName, string? OwnerFirstName, string? Model, int? DefaultDriverId);

public sealed record TruckDecisionResult(int TruckId, string Status, int ReviewsResolved);

/// <summary>A truck as the supervisor sees it (mutable for Dapper).</summary>
public sealed class FleetTruck
{
    public int Id { get; set; }
    public string FleetNumber { get; set; } = "";
    public string? OwnerLastName { get; set; }
    public string? OwnerFirstName { get; set; }
    public string? Plate { get; set; }
    public string? Model { get; set; }
    public int? DefaultDriverId { get; set; }
    public string? DefaultDriverName { get; set; }
    public bool Active { get; set; }
    public string ApprovalStatus { get; set; } = "";
    public string CreatedVia { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public decimal? Price { get; set; }
    public string? ReportedPlate { get; set; }
    public int? CreatedByDevice { get; set; }
    public string? CreatedByDeviceLabel { get; set; }
    public string? CreatedByName { get; set; }
    public int? PossibleDuplicateOf { get; set; }
    public string? DuplicateFleetNumber { get; set; }
    public string? DuplicatePlate { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
    public int? MergedInto { get; set; }
    public int TripCount { get; set; }
    public DateTime? LastTripAt { get; set; }
    public string[] ObservedPlates { get; set; } = [];
    public bool Complete { get; set; }
}

public sealed record PriceItem(long Id, decimal Price, DateTime ValidFrom, string? SetByName, string? Reason);

public sealed record PlateItem(string PlateText, DateTime ValidFrom, DateTime? ValidTo);

public sealed record TruckTripItem(long TripId, string TripNumber, string TicketId, DateTime EnteredAt, DateTime? ExitedAt,
    string State, string? LoadStatus, string? DriverName, int TruckId);

public sealed record TruckDriverItem(int DriverId, string DriverName, int Trips, bool IsDefault);

public sealed record AuditItem(long Id, DateTime At, string Actor, string Action, string Entity, string? EntityId, string Payload);

public sealed record TruckHistory(FleetTruck Truck, IReadOnlyList<PriceItem> Prices, IReadOnlyList<PlateItem> Plates,
    IReadOnlyList<TruckTripItem> Trips, IReadOnlyList<TruckDriverItem> Drivers, IReadOnlyList<AuditItem> Audit);

/// <summary>
/// Trucks over their whole life: registration from a quarry phone (provisional, PENDING_APPROVAL),
/// supervisor approval with the five business fields, rejection, merge into an existing truck,
/// later edits, price per truck with history. Trip records are never touched: a merged truck's trips
/// count for the truck it was merged into.
/// </summary>
public sealed class FleetService(Db db)
{
    /// <summary>Reasons resolved automatically when the supervisor approves (or merges) the truck.</summary>
    internal static readonly string[] TruckReasons = ["UNKNOWN_TRUCK", "TRUCK_PENDING_APPROVAL"];

    internal const string TruckSql = """
        SELECT t.id, t.fleet_number, t.owner_last_name, t.owner_first_name,
               (SELECT p.plate_text FROM tg.truck_plates p WHERE p.truck_id = t.id AND p.valid_to IS NULL
                ORDER BY p.valid_from DESC LIMIT 1) AS plate,
               t.model, t.default_driver_id, dd.full_name AS default_driver_name, t.active, t.approval_status,
               t.created_via, t.created_at, t.updated_at,
               (SELECT pr.price FROM tg.truck_prices pr WHERE pr.truck_id = t.id ORDER BY pr.valid_from DESC, pr.id DESC LIMIT 1) AS price,
               t.reported_plate, t.created_by_device, dv.label AS created_by_device_label, cu.full_name AS created_by_name,
               t.possible_duplicate_of, dup.fleet_number AS duplicate_fleet_number,
               (SELECT p.plate_text FROM tg.truck_plates p WHERE p.truck_id = t.possible_duplicate_of AND p.valid_to IS NULL
                ORDER BY p.valid_from DESC LIMIT 1) AS duplicate_plate,
               du.full_name AS decided_by_name, t.decided_at, t.decision_reason, t.merged_into,
               (SELECT count(*)::int FROM tg.trips tr WHERE tr.truck_id = t.id) AS trip_count,
               (SELECT max(tr.created_at) FROM tg.trips tr WHERE tr.truck_id = t.id) AS last_trip_at,
               coalesce((SELECT array_agg(DISTINCT gd.observed_plate) FROM tg.trips tr
                         JOIN tg.gate_decisions gd ON gd.gate_scan_id = tr.entry_scan_id
                         WHERE tr.truck_id = t.id AND gd.observed_plate IS NOT NULL), '{}') AS observed_plates,
               (t.owner_last_name IS NOT NULL AND t.owner_first_name IS NOT NULL
                AND EXISTS (SELECT 1 FROM tg.truck_plates p WHERE p.truck_id = t.id AND p.valid_to IS NULL)
                AND EXISTS (SELECT 1 FROM tg.truck_prices pr WHERE pr.truck_id = t.id)) AS complete
        FROM tg.trucks t
        LEFT JOIN tg.drivers dd ON dd.id = t.default_driver_id
        LEFT JOIN tg.devices dv ON dv.id = t.created_by_device
        LEFT JOIN tg.users cu ON cu.id = t.created_by_user
        LEFT JOIN tg.users du ON du.id = t.decided_by
        LEFT JOIN tg.trucks dup ON dup.id = t.possible_duplicate_of

        """;

    // ------------------------------------------------------------------ lists

    /// <summary>Every truck; <paramref name="status"/> filters on the approval status, <paramref name="search"/> on number or plate.</summary>
    public Task<IReadOnlyList<FleetTruck>> ListAsync(string? status, string? search, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<FleetTruck>>(async conn =>
        {
            var key = PlateRules.Key(search);
            var text = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";
            return (await conn.QueryAsync<FleetTruck>(TruckSql + """
                WHERE (@status::text IS NULL OR t.approval_status = @status)
                  AND (@text::text IS NULL OR t.fleet_number ILIKE @text OR t.owner_last_name ILIKE @text
                       OR t.owner_first_name ILIKE @text
                       OR (length(@key) >= 3 AND EXISTS (SELECT 1 FROM tg.truck_plates p WHERE p.truck_id = t.id
                                                         AND tg.plate_key(p.plate_text) LIKE '%' || @key || '%')))
                ORDER BY (t.approval_status = 'PENDING_APPROVAL') DESC, t.id
                """, new { status, text, key })).ToList();
        }, ct);

    public Task<FleetTruck> GetAsync(int id, CancellationToken ct = default) =>
        db.ReadAsync(async conn => await conn.QuerySingleOrDefaultAsync<FleetTruck>(TruckSql + "WHERE t.id = @id", new { id })
            ?? throw TruckGateException.NotFound("TRUCK_NOT_FOUND", $"Truck {id} does not exist."), ct);

    public Task<TruckHistory> HistoryAsync(int id, CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var truck = await conn.QuerySingleOrDefaultAsync<FleetTruck>(TruckSql + "WHERE t.id = @id", new { id })
                ?? throw TruckGateException.NotFound("TRUCK_NOT_FOUND", $"Truck {id} does not exist.");
            var prices = (await conn.QueryAsync<PriceItem>("""
                SELECT pr.id, pr.price, pr.valid_from AS validfrom, u.full_name AS setbyname, pr.reason
                FROM tg.truck_prices pr LEFT JOIN tg.users u ON u.id = pr.set_by
                WHERE pr.truck_id = @id ORDER BY pr.valid_from DESC, pr.id DESC
                """, new { id })).ToList();
            var plates = (await conn.QueryAsync<PlateItem>(
                "SELECT plate_text AS platetext, valid_from AS validfrom, valid_to AS validto FROM tg.truck_plates WHERE truck_id = @id ORDER BY valid_from DESC",
                new { id })).ToList();
            // its own trips and those of the provisional trucks merged into it
            var trips = (await conn.QueryAsync<TruckTripItem>("""
                SELECT tr.id AS tripid, tr.trip_number AS tripnumber, 'Q' || tr.ticket_device_id || '-' || tg.fmt_seq(tr.ticket_seq) AS ticketid,
                       gs.scanned_at AS enteredat, xs.scanned_at AS exitedat, tr.current_state AS state, tr.load_status AS loadstatus,
                       d.full_name AS drivername, tr.truck_id AS truckid
                FROM tg.trips tr
                JOIN tg.gate_scans gs ON gs.id = tr.entry_scan_id
                LEFT JOIN tg.gate_scans xs ON xs.id = tr.exit_scan_id
                LEFT JOIN tg.drivers d ON d.id = tr.driver_id
                WHERE tr.truck_id = @id OR tr.truck_id IN (SELECT m.id FROM tg.trucks m WHERE m.merged_into = @id)
                ORDER BY gs.scanned_at DESC LIMIT 300
                """, new { id })).ToList();
            var drivers = (await conn.QueryAsync<TruckDriverItem>("""
                SELECT d.id AS driverid, d.full_name AS drivername, count(tr.id)::int AS trips,
                       coalesce(d.id = (SELECT t.default_driver_id FROM tg.trucks t WHERE t.id = @id), false) AS isdefault
                FROM tg.drivers d
                LEFT JOIN tg.trips tr ON tr.driver_id = d.id
                     AND (tr.truck_id = @id OR tr.truck_id IN (SELECT m.id FROM tg.trucks m WHERE m.merged_into = @id))
                WHERE d.id IN (SELECT tr2.driver_id FROM tg.trips tr2 WHERE tr2.truck_id = @id
                               OR tr2.truck_id IN (SELECT m.id FROM tg.trucks m WHERE m.merged_into = @id))
                   OR d.id = (SELECT t.default_driver_id FROM tg.trucks t WHERE t.id = @id)
                GROUP BY d.id, d.full_name ORDER BY trips DESC, d.full_name
                """, new { id })).ToList();
            var idText = id.ToString(CultureInfo.InvariantCulture);
            var audit = (await conn.QueryAsync<AuditItem>("""
                SELECT a.id, a.at, a.actor, a.action, a.entity, a.entity_id AS entityid, a.payload::text AS payload
                FROM tg.audit_log a
                WHERE (a.entity = 'trucks' AND a.entity_id = @idText)
                   OR (a.entity IN ('truck_plates', 'truck_prices', 'payment_adjustments')
                       AND a.payload -> 'new' ->> 'truck_id' = @idText)
                ORDER BY a.id DESC LIMIT 200
                """, new { idText })).ToList();
            return new TruckHistory(truck, prices, plates, trips, drivers, audit);
        }, ct);

    // ------------------------------------------------------------------ supervisor decisions

    public Task<TruckDecisionResult> ApproveAsync(int id, TruckApproval a, int? userId, Actor actor, CancellationToken ct = default)
    {
        var fleet = Required(a.FleetNumber, "FLEET_NUMBER_REQUIRED", 32);
        var last = Required(a.OwnerLastName, "OWNER_LAST_NAME_REQUIRED", 80);
        var first = Required(a.OwnerFirstName, "OWNER_FIRST_NAME_REQUIRED", 80);
        var plate = PlateRules.Normalize(a.Plate) ?? throw TruckGateException.Validation("PLATE_INVALID",
            "Type the registration number (6 to 12 digits).");
        ValidatePrice(a.Price);
        return Guard(db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var t = await LockAsync(conn, tx, id);
            if (t.ApprovalStatus != "PENDING_APPROVAL")
                throw TruckGateException.Conflict("TRUCK_ALREADY_DECIDED", $"Truck {id} is already {t.ApprovalStatus}.");
            await EnsureFleetNumberFreeAsync(conn, tx, fleet, id);
            if (a.DefaultDriverId is { } dd) await EnsureDriverAsync(conn, tx, dd);
            await conn.ExecuteAsync("""
                UPDATE tg.trucks SET fleet_number = @fleet, owner_last_name = @last, owner_first_name = @first,
                       model = coalesce(@model, model), default_driver_id = coalesce(@driver, default_driver_id),
                       approval_status = 'APPROVED', decided_by = @userId, decided_at = now()
                WHERE id = @id
                """, new { id, fleet, last, first, model = Trim(a.Model), driver = a.DefaultDriverId, userId }, tx);
            await SetPlateCoreAsync(conn, tx, id, plate);
            await conn.ExecuteAsync("""
                INSERT INTO tg.truck_prices (truck_id, price, set_by, reason) VALUES (@id, @price, @userId, 'APPROVAL')
                """, new { id, price = a.Price, userId }, tx);
            await GateService.InsertAlertAsync(conn, tx, "TRUCK_APPROVED", "INFO", "trucks", id.ToString(CultureInfo.InvariantCulture),
                new { truck_id = id, label = fleet, plate });
            var resolved = await ResolveTruckReviewsAsync(conn, tx, [id], userId, $"TRUCK_APPROVED {fleet}");
            return new TruckDecisionResult(id, "APPROVED", resolved);
        }, ct));
    }

    public Task<TruckDecisionResult> RejectAsync(int id, string reason, int? userId, Actor actor, CancellationToken ct = default)
    {
        var why = Required(reason, "REASON_REQUIRED", 500);
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var t = await LockAsync(conn, tx, id);
            if (t.ApprovalStatus != "PENDING_APPROVAL")
                throw TruckGateException.Conflict("TRUCK_ALREADY_DECIDED", $"Truck {id} is already {t.ApprovalStatus}.");
            await conn.ExecuteAsync("""
                UPDATE tg.trucks SET approval_status = 'REJECTED', decided_by = @userId, decided_at = now(),
                       decision_reason = @why, active = false
                WHERE id = @id
                """, new { id, userId, why }, tx);
            await GateService.InsertAlertAsync(conn, tx, "TRUCK_REJECTED", "INFO", "trucks", id.ToString(CultureInfo.InvariantCulture),
                new { truck_id = id, label = t.FleetNumber, reason = why });
            return new TruckDecisionResult(id, "REJECTED", 0);
        }, ct);
    }

    /// <summary>The provisional truck is the same as an existing approved one: its trips count for that one.</summary>
    public Task<TruckDecisionResult> MergeAsync(int id, int targetId, string? reason, int? userId, Actor actor,
        CancellationToken ct = default)
    {
        if (id == targetId) throw TruckGateException.Validation("MERGE_SAME", "Choose another truck.");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            // fixed lock order: no deadlock with another merge
            var (a, b) = id < targetId ? (id, targetId) : (targetId, id);
            var first = await LockAsync(conn, tx, a);
            var second = await LockAsync(conn, tx, b);
            var t = first.Id == id ? first : second;
            var target = first.Id == targetId ? first : second;
            if (t.ApprovalStatus != "PENDING_APPROVAL")
                throw TruckGateException.Conflict("TRUCK_ALREADY_DECIDED", $"Truck {id} is already {t.ApprovalStatus}.");
            if (target.ApprovalStatus != "APPROVED")
                throw TruckGateException.Validation("MERGE_TARGET", "Merge only into an approved truck.");
            await conn.ExecuteAsync("""
                UPDATE tg.trucks SET approval_status = 'MERGED', merged_into = @targetId, decided_by = @userId,
                       decided_at = now(), decision_reason = @why, active = false
                WHERE id = @id
                """, new { id, targetId, userId, why = Trim(reason) }, tx);
            await GateService.InsertAlertAsync(conn, tx, "TRUCK_MERGED", "INFO", "trucks", id.ToString(CultureInfo.InvariantCulture),
                new { truck_id = id, target_id = targetId, label = target.FleetNumber });
            var resolved = await ResolveTruckReviewsAsync(conn, tx, [id], userId, $"TRUCK_MERGED {target.FleetNumber}");
            return new TruckDecisionResult(id, "MERGED", resolved);
        }, ct);
    }

    /// <summary>A truck added by the supervisor is approved at once (all five fields are required).</summary>
    public Task<int> CreateAsync(TruckCreate c, int? userId, Actor actor, CancellationToken ct = default)
    {
        var fleet = Required(c.FleetNumber, "FLEET_NUMBER_REQUIRED", 32);
        var last = Required(c.OwnerLastName, "OWNER_LAST_NAME_REQUIRED", 80);
        var first = Required(c.OwnerFirstName, "OWNER_FIRST_NAME_REQUIRED", 80);
        var plate = PlateRules.Normalize(c.Plate) ?? throw TruckGateException.Validation("PLATE_INVALID",
            "Type the registration number (6 to 12 digits).");
        ValidatePrice(c.Price);
        return Guard(db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('truck-id', 0))", transaction: tx);
            int id;
            if (c.Id is { } wanted)
            {
                if (wanted is < 1 or > 65535) throw TruckGateException.Validation("TRUCK_ID_RANGE", "Truck id must be 1..65535.");
                // a provisional number is only accepted when a trip already carries it (phone lost before its sync)
                if (ProvisionalTruckIds.Contains(wanted) && !await conn.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM tg.trips WHERE truck_id = @wanted)", new { wanted }, tx))
                    throw TruckGateException.Validation("TRUCK_ID_RESERVED", "Numbers from 40000 are reserved for the quarry phones.");
                id = wanted;
            }
            else
            {
                id = await conn.ExecuteScalarAsync<int>(
                    "SELECT coalesce(max(id), 0) + 1 FROM tg.trucks WHERE id < @first", new { first = ProvisionalTruckIds.First }, tx);
                if (id >= ProvisionalTruckIds.First) throw TruckGateException.Conflict("TRUCK_IDS_EXHAUSTED", "No truck number left.");
            }
            await EnsureFleetNumberFreeAsync(conn, tx, fleet, null);
            if (c.DefaultDriverId is { } dd) await EnsureDriverAsync(conn, tx, dd);
            await conn.ExecuteAsync("""
                INSERT INTO tg.trucks (id, fleet_number, model, default_driver_id, owner_last_name, owner_first_name,
                                       approval_status, decided_by, decided_at)
                VALUES (@id, @fleet, @model, @driver, @last, @first, 'APPROVED', @userId, now())
                """, new { id, fleet, model = Trim(c.Model), driver = c.DefaultDriverId, last, first, userId }, tx);
            await conn.ExecuteAsync("INSERT INTO tg.truck_plates (truck_id, plate_text) VALUES (@id, @plate)", new { id, plate }, tx);
            await conn.ExecuteAsync("""
                INSERT INTO tg.truck_prices (truck_id, price, set_by, reason) VALUES (@id, @price, @userId, 'CREATED')
                """, new { id, price = c.Price, userId }, tx);
            return id;
        }, ct));
    }

    public Task EditAsync(int id, TruckEdit e, Actor actor, CancellationToken ct = default)
    {
        var fleet = Required(e.FleetNumber, "FLEET_NUMBER_REQUIRED", 32);
        return Guard(db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var t = await LockAsync(conn, tx, id);
            if (t.ApprovalStatus is "REJECTED" or "MERGED")
                throw TruckGateException.Conflict("TRUCK_CLOSED", $"Truck {id} is {t.ApprovalStatus}; it cannot be edited.");
            await EnsureFleetNumberFreeAsync(conn, tx, fleet, id);
            if (e.DefaultDriverId is { } dd) await EnsureDriverAsync(conn, tx, dd);
            await conn.ExecuteAsync("""
                UPDATE tg.trucks SET fleet_number = @fleet, owner_last_name = @last, owner_first_name = @first,
                       model = @model, default_driver_id = @driver
                WHERE id = @id
                """, new { id, fleet, last = Trim(e.OwnerLastName), first = Trim(e.OwnerFirstName), model = Trim(e.Model),
                driver = e.DefaultDriverId }, tx);
            return 0;
        }, ct));
    }

    /// <summary>A new price applies to trips entering from now on; paid trips never change (corrections are adjustments).</summary>
    public Task SetPriceAsync(int id, decimal price, string reason, int? userId, Actor actor, CancellationToken ct = default)
    {
        ValidatePrice(price);
        var why = Required(reason, "REASON_REQUIRED", 500);
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var t = await LockAsync(conn, tx, id);
            if (t.ApprovalStatus != "APPROVED")
                throw TruckGateException.Conflict("TRUCK_NOT_APPROVED", "Approve the truck first; its price is set there.");
            await conn.ExecuteAsync("""
                INSERT INTO tg.truck_prices (truck_id, price, set_by, reason) VALUES (@id, @price, @userId, @why)
                """, new { id, price, userId, why }, tx);
            return 0;
        }, ct);
    }

    public Task SetPlateAsync(int id, string plate, Actor actor, CancellationToken ct = default)
    {
        var normalized = PlateRules.Normalize(plate) ?? throw TruckGateException.Validation("PLATE_INVALID",
            "Type the registration number (6 to 12 digits).");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await LockAsync(conn, tx, id);
            await SetPlateCoreAsync(conn, tx, id, normalized);
            return 0;
        }, ct);
    }

    // ------------------------------------------------------------------ quarry phones

    /// <summary>
    /// Makes sure the phone holds at least <see cref="ProvisionalTruckIds.MinFree"/> unused reserved ids
    /// (as far as the server knows) and returns its blocks that still have a free id.
    /// </summary>
    internal static async Task<IReadOnlyList<int[]>> EnsureIdBlocksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int deviceId)
    {
        const string freeSql = """
            SELECT count(*)::int FROM tg.truck_id_blocks b, generate_series(b.first_id, b.last_id) g(id)
            WHERE b.device_id = @deviceId AND NOT EXISTS (SELECT 1 FROM tg.trucks t WHERE t.id = g.id)
            """;
        var free = await conn.ExecuteScalarAsync<int>(freeSql, new { deviceId }, tx);
        if (free < ProvisionalTruckIds.MinFree)
        {
            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('truck-id-blocks', 0))", transaction: tx);
            free = await conn.ExecuteScalarAsync<int>(freeSql, new { deviceId }, tx);
            if (free < ProvisionalTruckIds.MinFree)
            {
                var next = await conn.ExecuteScalarAsync<int>("""
                    SELECT greatest(coalesce((SELECT max(last_id) FROM tg.truck_id_blocks), @first - 1),
                                    coalesce((SELECT max(id) FROM tg.trucks WHERE id >= @first), @first - 1)) + 1
                    """, new { first = ProvisionalTruckIds.First }, tx);
                var last = next + ProvisionalTruckIds.BlockSize - 1;
                if (last <= ProvisionalTruckIds.Last)
                    await conn.ExecuteAsync(
                        "INSERT INTO tg.truck_id_blocks (device_id, first_id, last_id) VALUES (@deviceId, @next, @last)",
                        new { deviceId, next, last }, tx);
            }
        }
        return (await conn.QueryAsync<(int First, int Last)>("""
            SELECT b.first_id, b.last_id FROM tg.truck_id_blocks b
            WHERE b.device_id = @deviceId
              AND EXISTS (SELECT 1 FROM generate_series(b.first_id, b.last_id) g(id)
                          WHERE NOT EXISTS (SELECT 1 FROM tg.trucks t WHERE t.id = g.id))
            ORDER BY b.first_id
            """, new { deviceId }, tx)).Select(b => new[] { b.First, b.Last }).ToList();
    }

    internal static Task<bool> IsDeviceBlockIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int deviceId, int truckId) =>
        conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM tg.truck_id_blocks WHERE device_id = @deviceId AND @truckId BETWEEN first_id AND last_id)
            """, new { deviceId, truckId }, tx);

    /// <summary>
    /// A truck typed by a quarry worker arrives with the phone's sync: stored as PENDING_APPROVAL with the
    /// plate exactly as reported, the supervisor is alerted, a possible duplicate is pointed out.
    /// Idempotent: the same truck sent twice is accepted once.
    /// </summary>
    internal static async Task<SyncTruckResult> RegisterFromPhoneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int deviceId,
        SyncTruck t)
    {
        var plate = PlateRules.Normalize(t.Plate);
        if (plate is null) return new SyncTruckResult(t.Id, "REJECTED", "PLATE_INVALID");
        if (!ProvisionalTruckIds.Contains(t.Id) || !await IsDeviceBlockIdAsync(conn, tx, deviceId, t.Id))
        {
            await Watchdog.AlertOnceAsync(conn, tx, "SYNC_TRUCK_ID_NOT_ALLOWED", "CRITICAL", "devices",
                deviceId.ToString(CultureInfo.InvariantCulture), t.Id.ToString(CultureInfo.InvariantCulture),
                new { device_id = deviceId, truck_id = t.Id });
            return new SyncTruckResult(t.Id, "REJECTED", "TRUCK_ID_NOT_ALLOWED");
        }

        // same lock key as the gate uses for this truck
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = "truck:" + t.Id }, tx);
        var existing = await conn.QuerySingleOrDefaultAsync<OriginRow>(
            "SELECT created_by_device, reported_plate FROM tg.trucks WHERE id = @id", new { id = t.Id }, tx);
        if (existing is not null)
        {
            if (existing.CreatedByDevice == deviceId && existing.ReportedPlate == plate) return new SyncTruckResult(t.Id, "OK", null);
            await Watchdog.AlertOnceAsync(conn, tx, "SYNC_TRUCK_CONFLICT", "CRITICAL", "trucks",
                t.Id.ToString(CultureInfo.InvariantCulture), "once", new { device_id = deviceId, truck_id = t.Id, plate });
            return new SyncTruckResult(t.Id, "REJECTED", "TRUCK_CONFLICT");
        }

        int? operatorId = t.OperatorId is { } op && await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM tg.users WHERE id = @op AND role = 'QUARRY_OPERATOR')", new { op }, tx) ? op : null;
        var duplicate = await conn.ExecuteScalarAsync<int?>("""
            SELECT t.id FROM tg.trucks t
            WHERE t.id <> @id AND t.approval_status IN ('APPROVED', 'PENDING_APPROVAL')
              AND EXISTS (SELECT 1 FROM tg.truck_plates p WHERE p.truck_id = t.id AND p.valid_to IS NULL
                          AND tg.plate_key(p.plate_text) = tg.plate_key(@plate))
            ORDER BY (t.approval_status = 'APPROVED') DESC, t.id LIMIT 1
            """, new { id = t.Id, plate }, tx);
        var fleet = "P" + t.Id.ToString(CultureInfo.InvariantCulture);
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.trucks WHERE fleet_number = @fleet)", new { fleet }, tx))
            fleet += "-" + deviceId.ToString(CultureInfo.InvariantCulture);
        await conn.ExecuteAsync("""
            INSERT INTO tg.trucks (id, fleet_number, approval_status, created_via, created_by_user, created_by_device,
                                   reported_plate, possible_duplicate_of)
            VALUES (@id, @fleet, 'PENDING_APPROVAL', 'PHONE', @operatorId, @deviceId, @plate, @duplicate)
            """, new { id = t.Id, fleet, operatorId, deviceId, plate, duplicate }, tx);
        await conn.ExecuteAsync("INSERT INTO tg.truck_plates (truck_id, plate_text) VALUES (@id, @plate)", new { id = t.Id, plate }, tx);
        var operatorName = operatorId is null ? null
            : await conn.ExecuteScalarAsync<string?>("SELECT full_name FROM tg.users WHERE id = @operatorId", new { operatorId }, tx);
        await GateService.InsertAlertAsync(conn, tx, "TRUCK_REGISTERED", "MEDIUM", "trucks", t.Id.ToString(CultureInfo.InvariantCulture),
            new { truck_id = t.Id, label = plate, device_id = deviceId, @operator = operatorName, duplicate_of = duplicate });
        return new SyncTruckResult(t.Id, "OK", null);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Accepts the open reviews of these trucks' trips whose only reasons were about the truck itself.</summary>
    private static async Task<int> ResolveTruckReviewsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int[] truckIds,
        int? userId, string why)
    {
        var trips = (await conn.QueryAsync<long>("""
            SELECT r.trip_id FROM tg.reviews r JOIN tg.trips tr ON tr.id = r.trip_id
            WHERE tr.truck_id = ANY(@truckIds) AND r.decision IS NULL
            GROUP BY r.trip_id
            HAVING bool_and(r.reason_codes <@ @reasons::text[])
            ORDER BY r.trip_id
            """, new { truckIds, reasons = TruckReasons }, tx)).ToList();
        foreach (var tripId in trips)
            await ReviewService.DecideCoreAsync(conn, tx, tripId, ReviewDecision.Accept, why, userId);
        return trips.Count;
    }

    private static async Task SetPlateCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int id, string plate)
    {
        var current = await conn.ExecuteScalarAsync<string?>("""
            SELECT plate_text FROM tg.truck_plates WHERE truck_id = @id AND valid_to IS NULL ORDER BY valid_from DESC LIMIT 1
            """, new { id }, tx);
        if (current == plate) return;
        await conn.ExecuteAsync("""
            UPDATE tg.truck_plates SET valid_to = greatest(clock_timestamp(), valid_from + interval '1 microsecond')
            WHERE truck_id = @id AND valid_to IS NULL
            """, new { id }, tx);
        await conn.ExecuteAsync("INSERT INTO tg.truck_plates (truck_id, plate_text) VALUES (@id, @plate)", new { id, plate }, tx);
    }

    private static async Task<TruckLock> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int id) =>
        await conn.QuerySingleOrDefaultAsync<TruckLock>(
            "SELECT id, fleet_number, approval_status FROM tg.trucks WHERE id = @id FOR UPDATE", new { id }, tx)
        ?? throw TruckGateException.NotFound("TRUCK_NOT_FOUND", $"Truck {id} does not exist.");

    private static async Task EnsureFleetNumberFreeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string fleet, int? exceptId)
    {
        if (await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM tg.trucks WHERE fleet_number = @fleet AND id IS DISTINCT FROM @exceptId)",
                new { fleet, exceptId }, tx))
            throw TruckGateException.Conflict("FLEET_NUMBER_EXISTS", $"Truck number {fleet} is already used.");
    }

    private static async Task EnsureDriverAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int driverId)
    {
        if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.drivers WHERE id = @driverId)", new { driverId }, tx))
            throw TruckGateException.NotFound("DRIVER_NOT_FOUND", $"Driver {driverId} does not exist.");
    }

    private static void ValidatePrice(decimal price)
    {
        if (price <= 0 || price > 100_000_000 || decimal.Round(price, 2) != price)
            throw TruckGateException.Validation("PRICE_INVALID", "The trip price must be a positive amount (2 decimals at most).");
    }

    private static string Required(string? value, string code, int maxLength)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) throw TruckGateException.Validation(code, "This field is required.");
        if (v.Length > maxLength) throw TruckGateException.Validation(code, $"At most {maxLength} characters.");
        return v;
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static async Task<T> Guard<T>(Task<T> task)
    {
        try
        {
            return await task;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw TruckGateException.Conflict("FLEET_NUMBER_EXISTS", e.ConstraintName ?? "duplicate");
        }
    }

    private sealed class OriginRow
    {
        public int? CreatedByDevice { get; set; }
        public string? ReportedPlate { get; set; }
    }

    private sealed class TruckLock
    {
        public int Id { get; set; }
        public string FleetNumber { get; set; } = "";
        public string ApprovalStatus { get; set; } = "";
    }
}
