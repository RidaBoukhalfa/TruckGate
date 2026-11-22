using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Application;

public sealed record EnrollmentOptions(string PublicServerUrl, TimeSpan CodeLifetime, TimeSpan MaxRequestClockSkew)
{
    public static EnrollmentOptions Default(string publicServerUrl) =>
        new(publicServerUrl.TrimEnd('/'), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5));
}

public sealed record EnrollmentInvite(string QrText, DateTime ExpiresAt);

public sealed record EnrollRequest(string Token, string TicketKeySpki, string AuthKeySpki, string? Attestation, string? Label);

public sealed record EnrollResult(int DeviceId, long ServerTime);

public sealed record DeviceAuthHeaders(int DeviceId, long UnixTime, string Signature);

public sealed record SignedDeviceConfig(string Payload, string Signature, int Version);

/// <summary>
/// Quarry phone onboarding and configuration:
///  1. an admin creates a one-time enrollment code (QR) that also carries the server public key;
///  2. the phone sends its two public keys with the code and receives its device id;
///  3. the phone downloads its configuration, signed by the server, authenticating with its auth key.
/// </summary>
public sealed class EnrollmentService(Db db, ServerSigningKey serverKey, EnrollmentOptions options)
{
    private static readonly Actor EnrollmentActor = Actor.System("enrollment");

    public Task<EnrollmentInvite> CreateInviteAsync(string label, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label)) throw TruckGateException.Validation("LABEL_REQUIRED", "Label is required.");
        var token = Base64Url(RandomNumberGenerator.GetBytes(24));
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var expiresAt = await conn.ExecuteScalarAsync<DateTime>("""
                INSERT INTO tg.enrollment_tokens (token_sha256, label, expires_at)
                VALUES (@hash, @label, now() + make_interval(secs => @ttl))
                RETURNING expires_at
                """, new { hash = SHA256.HashData(Encoding.UTF8.GetBytes(token)), label = label.Trim(),
                ttl = options.CodeLifetime.TotalSeconds }, tx);
            var qr = $"TGE1|{options.PublicServerUrl}|{token}|{Convert.ToBase64String(serverKey.PublicKeySpki)}";
            return new EnrollmentInvite(qr, expiresAt);
        }, ct);
    }

    public Task<EnrollResult> EnrollAsync(EnrollRequest req, CancellationToken ct = default)
    {
        var ticketKey = DecodeKey(req.TicketKeySpki);
        var authKey = DecodeKey(req.AuthKeySpki);
        if (ticketKey.AsSpan().SequenceEqual(authKey))
            throw TruckGateException.Validation("KEYS_MUST_DIFFER", "Ticket key and auth key must be different.");
        byte[]? attestation = null;
        if (!string.IsNullOrEmpty(req.Attestation))
        {
            try { attestation = Convert.FromBase64String(req.Attestation); }
            catch (FormatException) { throw TruckGateException.Validation("ATTESTATION_INVALID", "Attestation is not base64."); }
        }

        return db.InTransactionAsync(EnrollmentActor, async (conn, tx) =>
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(req.Token ?? ""));
            var token = await conn.QuerySingleOrDefaultAsync<TokenRow>("""
                SELECT id, label, used_at IS NOT NULL AS used, expires_at <= now() AS expired
                FROM tg.enrollment_tokens WHERE token_sha256 = @hash FOR UPDATE
                """, new { hash }, tx)
                ?? throw TruckGateException.Unauthorized("ENROLLMENT_CODE_UNKNOWN", "Unknown enrollment code.");
            if (token.Used) throw TruckGateException.Conflict("ENROLLMENT_CODE_USED", "This enrollment code was already used.");
            if (token.Expired) throw TruckGateException.Unauthorized("ENROLLMENT_CODE_EXPIRED", "This enrollment code has expired.");

            await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended('device-id', 0))", transaction: tx);
            var deviceId = await conn.ExecuteScalarAsync<int>("SELECT coalesce(max(id), 0) + 1 FROM tg.devices", transaction: tx);
            if (deviceId > 65535) throw TruckGateException.Conflict("DEVICE_IDS_EXHAUSTED", "No device id left.");
            var label = string.IsNullOrWhiteSpace(req.Label) ? token.Label : req.Label.Trim();
            try
            {
                await conn.ExecuteAsync("""
                    INSERT INTO tg.devices (id, kind, label, public_key_spki, auth_key_spki, attestation)
                    VALUES (@deviceId, 'QUARRY_PHONE', @label, @ticketKey, @authKey, @attestation)
                    """, new { deviceId, label, ticketKey, authKey, attestation }, tx);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw TruckGateException.Conflict("KEY_ALREADY_ENROLLED", "One of these keys is already registered.");
            }
            await conn.ExecuteAsync("UPDATE tg.enrollment_tokens SET used_at = now(), device_id = @deviceId WHERE id = @id",
                new { deviceId, id = token.Id }, tx);
            await GateService.InsertAlertAsync(conn, tx, "DEVICE_ENROLLED", "INFO", "devices", deviceId.ToString(),
                new { label });
            var now = await conn.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint", transaction: tx);
            return new EnrollResult(deviceId, now);
        }, ct);
    }

    /// <summary>Checks a phone's request signature. Returns the device id when valid.</summary>
    public Task<int> AuthenticateAsync(DeviceAuthHeaders h, string method, string path, byte[] body,
        CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<AuthRow>("""
                SELECT auth_key_spki, revoked_at IS NOT NULL AS revoked, extract(epoch FROM now())::bigint AS now
                FROM tg.devices WHERE id = @id AND kind = 'QUARRY_PHONE'
                """, new { id = h.DeviceId });
            if (row?.AuthKeySpki is null || row.Revoked)
                throw TruckGateException.Unauthorized("DEVICE_NOT_ALLOWED", "Unknown or revoked device.");
            if (Math.Abs(row.Now - h.UnixTime) > (long)options.MaxRequestClockSkew.TotalSeconds)
                throw TruckGateException.Unauthorized("REQUEST_TIME", "Request time is too far from server time.");
            byte[] signature;
            try { signature = Convert.FromBase64String(h.Signature); }
            catch (FormatException) { throw TruckGateException.Unauthorized("SIGNATURE_INVALID", "Bad signature."); }
            var message = DeviceAuthMessage.Build(h.DeviceId, h.UnixTime, method, path, body);
            if (!EcdsaP256.Verify(row.AuthKeySpki, message, signature))
                throw TruckGateException.Unauthorized("SIGNATURE_INVALID", "Bad signature.");
            return h.DeviceId;
        }, ct);

    /// <summary>
    /// Builds the configuration for a phone (trucks, drivers, operators with PIN hashes, quarry
    /// geofence, parameters), gives each distinct content a version number and signs it.
    /// </summary>
    public Task<SignedDeviceConfig> GetSignedConfigAsync(int deviceId, CancellationToken ct = default) =>
        db.InTransactionAsync(EnrollmentActor, async (conn, tx) =>
        {
            var site = await conn.QuerySingleOrDefaultAsync<SiteRow>(
                "SELECT id::int AS id, name, geofence::text AS geofence FROM tg.sites WHERE kind = 'QUARRY' ORDER BY id LIMIT 1",
                transaction: tx);
            var trucks = (await conn.QueryAsync<TruckRow>("""
                SELECT t.id, t.fleet_number, t.default_driver_id, t.active,
                       (SELECT p.plate_text FROM tg.truck_plates p
                        WHERE p.truck_id = t.id AND p.valid_to IS NULL ORDER BY p.valid_from DESC LIMIT 1) AS plate
                FROM tg.trucks t ORDER BY t.id
                """, transaction: tx)).ToList();
            var drivers = (await conn.QueryAsync<NameRow>(
                "SELECT id, full_name AS name FROM tg.drivers WHERE active ORDER BY id", transaction: tx)).ToList();
            var operators = (await conn.QueryAsync<OperatorRow>("""
                SELECT id, full_name AS name, pin_hash FROM tg.users
                WHERE role = 'QUARRY_OPERATOR' AND active AND pin_hash IS NOT NULL ORDER BY id
                """, transaction: tx)).ToList();
            var settings = (await conn.QueryAsync<(string Key, string Value)>(
                    "SELECT key, value::text FROM tg.settings WHERE key LIKE 'quarry.%'", transaction: tx))
                .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
            string S(string key, string fallback) => settings.TryGetValue(key, out var v) ? v : fallback;

            var content = new
            {
                site = site is null ? null : new
                {
                    id = site.Id,
                    name = site.Name,
                    geofence = string.IsNullOrEmpty(site.Geofence) ? null : JsonSerializer.Deserialize<int[][]>(site.Geofence),
                },
                trucks = trucks.Select(t => new
                {
                    id = t.Id, fleetNumber = t.FleetNumber, plate = t.Plate, defaultDriverId = t.DefaultDriverId, active = t.Active,
                }),
                drivers = drivers.Select(d => new { id = d.Id, name = d.Name }),
                operators = operators.Select(o => new { id = o.Id, name = o.Name, pinHash = o.PinHash }),
                @params = new
                {
                    truckCooldownS = int.Parse(S("quarry.truck_cooldown_s", "1200")),
                    clockMismatchS = int.Parse(S("quarry.clock_mismatch_s", "300")),
                    syncMaxAgeS = int.Parse(S("quarry.sync_max_age_s", "86400")),
                    gpsMaxAgeS = int.Parse(S("quarry.gps_max_age_s", "120")),
                    gpsMaxAccuracyM = int.Parse(S("quarry.gps_max_accuracy_m", "150")),
                    maxUnsyncedTickets = int.Parse(S("quarry.max_unsynced_tickets", "500")),
                },
            };
            var contentJson = JsonSerializer.Serialize(content);
            var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(contentJson));
            var summary = JsonSerializer.Serialize(new
            {
                site = site?.Id,
                trucks = trucks.Count,
                drivers = drivers.Count,
                operators = operators.Select(o => o.Id),
                @params = content.@params,
            });

            await conn.ExecuteAsync("""
                INSERT INTO tg.device_configs (content_sha256, summary) VALUES (@h, CAST(@s AS jsonb))
                ON CONFLICT (content_sha256) DO NOTHING
                """, new { h = contentHash, s = summary }, tx);
            var version = await conn.ExecuteScalarAsync<int>(
                "SELECT id FROM tg.device_configs WHERE content_sha256 = @h", new { h = contentHash }, tx);
            var now = await conn.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint", transaction: tx);

            var payload = $"{{\"format\":\"TGCFG1\",\"version\":{version},\"deviceId\":{deviceId},\"generatedAt\":{now},\"content\":{contentJson}}}";
            var signature = Convert.ToBase64String(serverKey.Sign(Encoding.UTF8.GetBytes(payload)));
            return new SignedDeviceConfig(payload, signature, version);
        }, ct);

    private static byte[] DecodeKey(string? base64)
    {
        try
        {
            var key = Convert.FromBase64String(base64 ?? "");
            if (EcdsaP256.IsValidP256PublicKey(key)) return key;
        }
        catch (FormatException)
        {
        }
        throw TruckGateException.Validation("PUBLIC_KEY_INVALID", "Keys must be ECDSA P-256 SubjectPublicKeyInfo in base64.");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenRow
    {
        public long Id { get; set; }
        public string Label { get; set; } = "";
        public bool Used { get; set; }
        public bool Expired { get; set; }
    }

    private sealed class AuthRow
    {
        public byte[]? AuthKeySpki { get; set; }
        public bool Revoked { get; set; }
        public long Now { get; set; }
    }

    private sealed class SiteRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Geofence { get; set; }
    }

    private sealed class TruckRow
    {
        public int Id { get; set; }
        public string FleetNumber { get; set; } = "";
        public int? DefaultDriverId { get; set; }
        public bool Active { get; set; }
        public string? Plate { get; set; }
    }

    private sealed class NameRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class OperatorRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string PinHash { get; set; } = "";
    }
}
