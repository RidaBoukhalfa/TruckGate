using Dapper;
using Npgsql;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record DeviceInfo(int Id, string Kind, string Label, DateTime EnrolledAt, DateTime? RevokedAt,
    string? RevokeReason, DateTime? LastSyncAt);

/// <summary>Reference data: sites, gates, users, drivers, trucks, plates and devices. Every change is audited by the database.</summary>
public sealed class AdminService(Db db)
{
    public static readonly string[] DeviceKinds = ["QUARRY_PHONE", "GATE_STATION"];
    public static readonly string[] SiteKinds = ["QUARRY", "FACTORY"];
    public static readonly string[] UserRoles =
        ["QUARRY_OPERATOR", "GATEKEEPER", "SUPERVISOR", "ACCOUNTANT", "MANAGER", "ADMIN", "AUDITOR"];

    // ------------------------------------------------------------------ devices

    public Task EnrollDeviceAsync(int id, string kind, string label, byte[] publicKeySpki, Actor actor,
        CancellationToken ct = default)
    {
        if (id is < 1 or > 65535) throw TruckGateException.Validation("DEVICE_ID_RANGE", "Device id must be 1..65535.");
        if (!DeviceKinds.Contains(kind)) throw TruckGateException.Validation("DEVICE_KIND", "Unknown device kind.");
        if (string.IsNullOrWhiteSpace(label)) throw TruckGateException.Validation("LABEL_REQUIRED", "Label is required.");
        if (!EcdsaP256.IsValidP256PublicKey(publicKeySpki))
            throw TruckGateException.Validation("PUBLIC_KEY_INVALID", "Public key must be an ECDSA P-256 SubjectPublicKeyInfo.");

        return Guard(db.InTransactionAsync(actor, (conn, tx) =>
            conn.ExecuteAsync("""
                INSERT INTO tg.devices (id, kind, label, public_key_spki) VALUES (@id, @kind, @label, @key)
                """, new { id, kind, label = label.Trim(), key = publicKeySpki }, tx), ct), "DEVICE_EXISTS");
    }

    /// <summary>Revocation is permanent. Tickets issued from that moment on are rejected.</summary>
    public Task RevokeDeviceAsync(int id, string reason, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw TruckGateException.Validation("REASON_REQUIRED", "A reason is required.");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var state = await conn.QuerySingleOrDefaultAsync<bool?>(
                "SELECT revoked_at IS NOT NULL FROM tg.devices WHERE id = @id FOR UPDATE", new { id }, tx);
            if (state is null) throw TruckGateException.NotFound("DEVICE_NOT_FOUND", $"Device {id} does not exist.");
            if (state.Value) throw TruckGateException.Conflict("ALREADY_REVOKED", $"Device {id} is already revoked.");
            await conn.ExecuteAsync("UPDATE tg.devices SET revoked_at = now(), revoke_reason = @reason WHERE id = @id",
                new { id, reason = reason.Trim() }, tx);
            await GateService.InsertAlertAsync(conn, tx, "DEVICE_REVOKED", "MEDIUM", "devices", id.ToString(),
                new { reason });
            return 0;
        }, ct);
    }

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<DeviceInfo>>(async conn => (await conn.QueryAsync<DeviceInfo>("""
            SELECT id, kind, label, enrolled_at AS enrolledat, revoked_at AS revokedat,
                   revoke_reason AS revokereason, last_sync_at AS lastsyncat
            FROM tg.devices ORDER BY id
            """)).ToList(), ct);

    // ------------------------------------------------------------------ sites, gates, users

    public Task<int> CreateSiteAsync(string code, string name, string kind, Actor actor, CancellationToken ct = default)
    {
        if (!SiteKinds.Contains(kind)) throw TruckGateException.Validation("SITE_KIND", "Unknown site kind.");
        return Guard(db.InTransactionAsync(actor, (conn, tx) => conn.ExecuteScalarAsync<int>(
            "INSERT INTO tg.sites (code, name, kind) VALUES (@code, @name, @kind) RETURNING id",
            new { code, name, kind }, tx), ct), "SITE_EXISTS");
    }

    public Task<int> CreateGateAsync(string code, string name, int? siteId, Actor actor, CancellationToken ct = default) =>
        Guard(db.InTransactionAsync(actor, (conn, tx) => conn.ExecuteScalarAsync<int>(
            "INSERT INTO tg.gates (code, name, site_id) VALUES (@code, @name, @siteId) RETURNING id",
            new { code, name, siteId }, tx), ct), "GATE_EXISTS");

    public Task<int> CreateUserAsync(string username, string fullName, string role, string language, Actor actor,
        CancellationToken ct = default)
    {
        if (!UserRoles.Contains(role)) throw TruckGateException.Validation("ROLE_INVALID", "Unknown role.");
        if (language is not ("ar" or "fr")) throw TruckGateException.Validation("LANGUAGE_INVALID", "Language must be ar or fr.");
        return Guard(db.InTransactionAsync(actor, (conn, tx) => conn.ExecuteScalarAsync<int>("""
            INSERT INTO tg.users (username, full_name, role, language) VALUES (@username, @fullName, @role, @language)
            RETURNING id
            """, new { username, fullName, role, language }, tx), ct), "USER_EXISTS");
    }

    /// <summary>Quarry boundary, points as [lat_e6, lon_e6]. Phones refuse to issue tickets outside it.</summary>
    public Task SetSiteGeofenceAsync(int siteId, IReadOnlyList<int[]> points, Actor actor, CancellationToken ct = default)
    {
        if (points.Count is < 3 or > 200)
            throw TruckGateException.Validation("GEOFENCE_POINTS", "A geofence needs 3 to 200 points.");
        if (points.Any(p => p is not { Length: 2 } || p[0] is < -90_000_000 or > 90_000_000 || p[1] is < -180_000_000 or > 180_000_000))
            throw TruckGateException.Validation("GEOFENCE_RANGE", "Points must be [lat_e6, lon_e6] within valid ranges.");
        var json = System.Text.Json.JsonSerializer.Serialize(points);
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var n = await conn.ExecuteAsync("UPDATE tg.sites SET geofence = CAST(@json AS jsonb) WHERE id = @siteId",
                new { json, siteId }, tx);
            if (n == 0) throw TruckGateException.NotFound("SITE_NOT_FOUND", $"Site {siteId} does not exist.");
            return 0;
        }, ct);
    }

    /// <summary>
    /// Sets a user's PIN (gatekeepers, quarry operators) or password (other roles). Only the PBKDF2 hash
    /// is stored, never written to the audit log. Also unlocks the account.
    /// </summary>
    public Task SetUserPinAsync(int userId, string secret, Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var role = await conn.ExecuteScalarAsync<string?>(
                "SELECT role FROM tg.users WHERE id = @userId AND active FOR UPDATE", new { userId }, tx)
                ?? throw TruckGateException.NotFound("USER_NOT_FOUND", $"Active user {userId} does not exist.");
            SecretPolicy.Validate(role, secret);
            await conn.ExecuteAsync("""
                UPDATE tg.users SET pin_hash = @hash, pin_set_at = now(), failed_logins = 0, locked_until = NULL
                WHERE id = @userId
                """, new { hash = PinHasher.HashSecret(secret), userId }, tx);
            return 0;
        }, ct);

    public Task SetUserActiveAsync(int userId, bool active, Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var n = await conn.ExecuteAsync("UPDATE tg.users SET active = @active WHERE id = @userId", new { userId, active }, tx);
            if (n == 0) throw TruckGateException.NotFound("USER_NOT_FOUND", $"User {userId} does not exist.");
            return 0;
        }, ct);

    // ------------------------------------------------------------------ lists for the admin screens

    public sealed record TruckItem(int Id, string FleetNumber, string? Model, string? Plate, int? DefaultDriverId, bool Active);
    public sealed record DriverItem(int Id, string FullName, string? Phone, string EmploymentType, bool Active);
    public sealed record GateItem(int Id, string Code, string Name, int? SiteId);
    public sealed record SiteItem(int Id, string Code, string Name, string Kind, bool HasGeofence);

    public Task<IReadOnlyList<TruckItem>> ListTrucksAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<TruckItem>>(async conn => (await conn.QueryAsync<TruckItem>("""
            SELECT t.id, t.fleet_number AS fleetnumber, t.model,
                   (SELECT p.plate_text FROM tg.truck_plates p WHERE p.truck_id = t.id AND p.valid_to IS NULL
                    ORDER BY p.valid_from DESC LIMIT 1) AS plate,
                   t.default_driver_id AS defaultdriverid, t.active
            FROM tg.trucks t ORDER BY t.id
            """)).ToList(), ct);

    public Task<IReadOnlyList<DriverItem>> ListDriversAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<DriverItem>>(async conn => (await conn.QueryAsync<DriverItem>(
            "SELECT id, full_name AS fullname, phone, employment_type AS employmenttype, active FROM tg.drivers ORDER BY id"))
            .ToList(), ct);

    public Task<IReadOnlyList<GateItem>> ListGatesAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<GateItem>>(async conn => (await conn.QueryAsync<GateItem>(
            "SELECT id::int AS id, code, name, site_id::int AS siteid FROM tg.gates ORDER BY id")).ToList(), ct);

    public Task<IReadOnlyList<SiteItem>> ListSitesAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<SiteItem>>(async conn => (await conn.QueryAsync<SiteItem>(
            "SELECT id::int AS id, code, name, kind, geofence IS NOT NULL AS hasgeofence FROM tg.sites ORDER BY id"))
            .ToList(), ct);

    public Task<IReadOnlyList<UserListItem>> ListUsersAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<UserListItem>>(async conn => (await conn.QueryAsync<UserListItem>("""
            SELECT id, username, full_name AS fullname, role, language, active, pin_hash IS NOT NULL AS hassecret,
                   last_login_at AS lastloginat, CASE WHEN locked_until > now() THEN locked_until END AS lockeduntil
            FROM tg.users ORDER BY id
            """)).ToList(), ct);

    // ------------------------------------------------------------------ drivers and trucks

    public Task<int> CreateDriverAsync(string fullName, string? phone, string employmentType, Actor actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullName)) throw TruckGateException.Validation("NAME_REQUIRED", "Name is required.");
        if (employmentType is not ("EMPLOYEE" or "CONTRACTOR"))
            throw TruckGateException.Validation("EMPLOYMENT_TYPE", "Employment type must be EMPLOYEE or CONTRACTOR.");
        return db.InTransactionAsync(actor, (conn, tx) => conn.ExecuteScalarAsync<int>("""
            INSERT INTO tg.drivers (full_name, phone, employment_type) VALUES (@fullName, @phone, @employmentType)
            RETURNING id
            """, new { fullName = fullName.Trim(), phone, employmentType }, tx), ct);
    }

    public Task CreateTruckAsync(int id, string fleetNumber, string? model, int? defaultDriverId, string? plateText,
        Actor actor, CancellationToken ct = default)
    {
        if (id is < 1 or > 65535) throw TruckGateException.Validation("TRUCK_ID_RANGE", "Truck id must be 1..65535.");
        if (string.IsNullOrWhiteSpace(fleetNumber))
            throw TruckGateException.Validation("FLEET_NUMBER_REQUIRED", "Fleet number is required.");
        return Guard(db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO tg.trucks (id, fleet_number, model, default_driver_id)
                VALUES (@id, @fleetNumber, @model, @defaultDriverId)
                """, new { id, fleetNumber = fleetNumber.Trim(), model, defaultDriverId }, tx);
            if (!string.IsNullOrWhiteSpace(plateText))
                await conn.ExecuteAsync("INSERT INTO tg.truck_plates (truck_id, plate_text) VALUES (@id, @plate)",
                    new { id, plate = NormalizePlate(plateText) }, tx);
            return 0;
        }, ct), "TRUCK_EXISTS");
    }

    /// <summary>Closes the current plate and opens a new one. History is kept for old evidence.</summary>
    public Task SetTruckPlateAsync(int truckId, string plateText, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plateText)) throw TruckGateException.Validation("PLATE_REQUIRED", "Plate is required.");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await EnsureTruckAsync(conn, tx, truckId);
            await conn.ExecuteAsync("""
                UPDATE tg.truck_plates SET valid_to = greatest(clock_timestamp(), valid_from + interval '1 microsecond')
                WHERE truck_id = @truckId AND valid_to IS NULL
                """, new { truckId }, tx);
            await conn.ExecuteAsync("INSERT INTO tg.truck_plates (truck_id, plate_text) VALUES (@truckId, @plate)",
                new { truckId, plate = NormalizePlate(plateText) }, tx);
            return 0;
        }, ct);
    }

    public Task SetTruckActiveAsync(int truckId, bool active, Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await EnsureTruckAsync(conn, tx, truckId);
            await conn.ExecuteAsync("UPDATE tg.trucks SET active = @active WHERE id = @truckId", new { truckId, active }, tx);
            return 0;
        }, ct);

    public Task SetDefaultDriverAsync(int truckId, int? driverId, Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await EnsureTruckAsync(conn, tx, truckId);
            await conn.ExecuteAsync("UPDATE tg.trucks SET default_driver_id = @driverId WHERE id = @truckId",
                new { truckId, driverId }, tx);
            return 0;
        }, ct);

    // ------------------------------------------------------------------ helpers

    /// <summary>Plates are stored as digits separated by single spaces (e.g. "01234 216 16").</summary>
    public static string NormalizePlate(string plate) =>
        string.Join(' ', plate.Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries));

    private static async Task EnsureTruckAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int truckId)
    {
        var exists = await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.trucks WHERE id = @truckId)",
            new { truckId }, tx);
        if (!exists) throw TruckGateException.NotFound("TRUCK_NOT_FOUND", $"Truck {truckId} does not exist.");
    }

    /// <summary>Turns a unique-key violation into a readable conflict.</summary>
    private static async Task<T> Guard<T>(Task<T> task, string conflictCode)
    {
        try
        {
            return await task;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw TruckGateException.Conflict(conflictCode, e.ConstraintName ?? "duplicate");
        }
    }

    private static async Task Guard(Task task, string conflictCode)
    {
        try
        {
            await task;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw TruckGateException.Conflict(conflictCode, e.ConstraintName ?? "duplicate");
        }
    }
}
