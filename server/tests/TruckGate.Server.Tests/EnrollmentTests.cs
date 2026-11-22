using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using TruckGate.Application;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

[Collection("db")]
public class EnrollmentTests(DatabaseFixture fx)
{
    private sealed class Env
    {
        public required Db Db { get; init; }
        public required ServerSigningKey ServerKey { get; init; }
        public required EnrollmentService Enrollment { get; init; }
        public required AdminService Admin { get; init; }
        public required GateService Gate { get; init; }
    }

    private async Task<Env> NewEnvAsync(TimeSpan? codeLifetime = null)
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var key = ServerSigningKey.Ephemeral();
        var options = EnrollmentOptions.Default("http://192.168.1.10:5080/") with
        {
            CodeLifetime = codeLifetime ?? TimeSpan.FromMinutes(15),
        };
        return new Env
        {
            Db = db, ServerKey = key, Enrollment = new EnrollmentService(db, key, options),
            Admin = new AdminService(db), Gate = new GateService(db),
        };
    }

    private static (QuarryPhone Ticket, ECDsa Auth) NewPhoneKeys() =>
        (new QuarryPhone(0), ECDsa.Create(ECCurve.NamedCurves.nistP256));

    private static string Token(EnrollmentInvite invite) => invite.QrText.Split('|')[2];

    private static async Task<int> EnrollAsync(Env env, QuarryPhone phone, ECDsa auth)
    {
        var invite = await env.Enrollment.CreateInviteAsync("Quarry phone", DatabaseFixture.Tester);
        var result = await env.Enrollment.EnrollAsync(new EnrollRequest(Token(invite),
            Convert.ToBase64String(phone.PublicKey), Convert.ToBase64String(auth.ExportSubjectPublicKeyInfo()), null, null));
        phone.DeviceId = result.DeviceId;
        return result.DeviceId;
    }

    private static DeviceAuthHeaders Sign(ECDsa auth, int deviceId, long time, string path)
    {
        var message = DeviceAuthMessage.Build(deviceId, time, "GET", path, []);
        var sig = auth.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new DeviceAuthHeaders(deviceId, time, Convert.ToBase64String(sig));
    }

    private static Task<long> Now(Env env) =>
        env.Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint"));

    [Fact]
    public async Task Invite_QR_carries_server_url_token_and_server_key()
    {
        var env = await NewEnvAsync();
        var invite = await env.Enrollment.CreateInviteAsync("Quarry phone 1", DatabaseFixture.Tester);

        var parts = invite.QrText.Split('|');
        Assert.Equal(4, parts.Length);
        Assert.Equal("TGE1", parts[0]);
        Assert.Equal("http://192.168.1.10:5080", parts[1]);
        Assert.True(parts[2].Length >= 30);
        Assert.Equal(env.ServerKey.PublicKeySpki, Convert.FromBase64String(parts[3]));
    }

    [Fact]
    public async Task Enrolled_phone_can_issue_tickets_that_the_gate_accepts()
    {
        var env = await NewEnvAsync();
        var (phone, auth) = NewPhoneKeys();
        var deviceId = await EnrollAsync(env, phone, auth);
        var gateId = (short)await env.Admin.CreateGateAsync("G1", "Gate", null, DatabaseFixture.Tester);
        var driver = await env.Admin.CreateDriverAsync("Ahmed", null, "EMPLOYEE", DatabaseFixture.Tester);
        await env.Admin.CreateTruckAsync(5, "05", null, driver, null, DatabaseFixture.Tester);
        var now = await Now(env);

        var scan = await env.Gate.ScanEntryAsync(
            new ScanEntryRequest(gateId, phone.Ticket(1, 5, driver, now - 900), null), DatabaseFixture.Tester);

        Assert.Equal(1, deviceId);
        Assert.Equal("ACCEPT", scan.Outcome);
    }

    [Fact]
    public async Task An_enrollment_code_works_once()
    {
        var env = await NewEnvAsync();
        var invite = await env.Enrollment.CreateInviteAsync("Quarry phone", DatabaseFixture.Tester);
        var (p1, a1) = NewPhoneKeys();
        var (p2, a2) = NewPhoneKeys();
        await env.Enrollment.EnrollAsync(new EnrollRequest(Token(invite), Convert.ToBase64String(p1.PublicKey),
            Convert.ToBase64String(a1.ExportSubjectPublicKeyInfo()), null, null));

        var e = await Assert.ThrowsAsync<TruckGateException>(() => env.Enrollment.EnrollAsync(new EnrollRequest(
            Token(invite), Convert.ToBase64String(p2.PublicKey), Convert.ToBase64String(a2.ExportSubjectPublicKeyInfo()), null, null)));
        Assert.Equal("ENROLLMENT_CODE_USED", e.Code);
    }

    [Fact]
    public async Task Expired_or_unknown_codes_are_refused()
    {
        var env = await NewEnvAsync(TimeSpan.FromSeconds(1));
        var invite = await env.Enrollment.CreateInviteAsync("Quarry phone", DatabaseFixture.Tester);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var (p, a) = NewPhoneKeys();
        var keys = (Convert.ToBase64String(p.PublicKey), Convert.ToBase64String(a.ExportSubjectPublicKeyInfo()));

        var expired = await Assert.ThrowsAsync<TruckGateException>(() =>
            env.Enrollment.EnrollAsync(new EnrollRequest(Token(invite), keys.Item1, keys.Item2, null, null)));
        var unknown = await Assert.ThrowsAsync<TruckGateException>(() =>
            env.Enrollment.EnrollAsync(new EnrollRequest("not-a-real-code-at-all-000000", keys.Item1, keys.Item2, null, null)));

        Assert.Equal("ENROLLMENT_CODE_EXPIRED", expired.Code);
        Assert.Equal("ENROLLMENT_CODE_UNKNOWN", unknown.Code);
    }

    [Fact]
    public async Task Signed_config_contains_fleet_operators_and_geofence_and_verifies()
    {
        var env = await NewEnvAsync();
        var (phone, auth) = NewPhoneKeys();
        var deviceId = await EnrollAsync(env, phone, auth);
        var site = await env.Admin.CreateSiteAsync("Q1", "Carrière", "QUARRY", DatabaseFixture.Tester);
        await env.Admin.SetSiteGeofenceAsync(site, [[35_000_000, -1_000_000], [35_000_000, -990_000], [35_010_000, -990_000]],
            DatabaseFixture.Tester);
        var driver = await env.Admin.CreateDriverAsync("أحمد ب.", null, "EMPLOYEE", DatabaseFixture.Tester);
        await env.Admin.CreateTruckAsync(5, "05", "Foton", driver, "01234 216 16", DatabaseFixture.Tester);
        var op = await env.Admin.CreateUserAsync("omar", "عمر م.", "QUARRY_OPERATOR", "ar", DatabaseFixture.Tester);
        await env.Admin.SetUserPinAsync(op, "4821", DatabaseFixture.Tester);
        var path = $"/api/devices/{deviceId}/config";

        await env.Enrollment.AuthenticateAsync(Sign(auth, deviceId, await Now(env), path), "GET", path, []);
        var config = await env.Enrollment.GetSignedConfigAsync(deviceId);

        Assert.True(EcdsaP256.Verify(env.ServerKey.PublicKeySpki, Encoding.UTF8.GetBytes(config.Payload),
            Convert.FromBase64String(config.Signature)));
        using var doc = JsonDocument.Parse(config.Payload);
        var root = doc.RootElement;
        Assert.Equal("TGCFG1", root.GetProperty("format").GetString());
        Assert.Equal(deviceId, root.GetProperty("deviceId").GetInt32());
        var content = root.GetProperty("content");
        Assert.Equal("01234 216 16", content.GetProperty("trucks")[0].GetProperty("plate").GetString());
        Assert.Equal("أحمد ب.", content.GetProperty("drivers")[0].GetProperty("name").GetString());
        Assert.Equal(3, content.GetProperty("site").GetProperty("geofence").GetArrayLength());
        Assert.True(PinHasher.Verify("4821", content.GetProperty("operators")[0].GetProperty("pinHash").GetString()!));
        Assert.Equal(1200, content.GetProperty("params").GetProperty("truckCooldownS").GetInt32());

        var again = await env.Enrollment.GetSignedConfigAsync(deviceId);
        Assert.Equal(config.Version, again.Version);
        await env.Admin.CreateTruckAsync(6, "06", null, driver, null, DatabaseFixture.Tester);
        var changed = await env.Enrollment.GetSignedConfigAsync(deviceId);
        Assert.NotEqual(config.Version, changed.Version);

        var pinInAudit = await env.Db.ReadAsync(c => c.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM tg.audit_log WHERE payload::text LIKE '%pbkdf2%'"));
        Assert.Equal(0L, pinInAudit);
    }

    [Fact]
    public async Task Config_requests_need_a_fresh_valid_signature_from_an_active_phone()
    {
        var env = await NewEnvAsync();
        var (phone, auth) = NewPhoneKeys();
        var deviceId = await EnrollAsync(env, phone, auth);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var path = $"/api/devices/{deviceId}/config";
        var now = await Now(env);

        async Task<string> Fails(DeviceAuthHeaders h) =>
            (await Assert.ThrowsAsync<TruckGateException>(() => env.Enrollment.AuthenticateAsync(h, "GET", path, []))).Code;

        Assert.Equal("SIGNATURE_INVALID", await Fails(Sign(stranger, deviceId, now, path)));
        Assert.Equal("SIGNATURE_INVALID", await Fails(Sign(auth, deviceId, now, path + "x")));
        Assert.Equal("REQUEST_TIME", await Fails(Sign(auth, deviceId, now - 3600, path)));
        Assert.Equal("DEVICE_NOT_ALLOWED", await Fails(Sign(auth, deviceId + 50, now, path)));

        await env.Admin.RevokeDeviceAsync(deviceId, "lost", DatabaseFixture.Tester);
        Assert.Equal("DEVICE_NOT_ALLOWED", await Fails(Sign(auth, deviceId, now, path)));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("12a4")]
    [InlineData("1111")]
    [InlineData("1234")]
    [InlineData("9876")]
    public void Weak_or_malformed_PINs_are_refused(string pin) =>
        Assert.Throws<TruckGateException>(() => PinHasher.Hash(pin));

    [Fact]
    public void PIN_hashes_match_the_reference_used_by_the_phone()
    {
        // Same vector as quarry-core CoreChecks (Python hashlib.pbkdf2_hmac).
        const string reference = "pbkdf2-sha256$100000$MDEyMzQ1Njc4OWFiY2RlZg==$1XoFd9w4yN5wybLJRH2X7gyB/cRvKe8JHMBKQ3mu1nM=";
        Assert.True(PinHasher.Verify("4821", reference));
        Assert.False(PinHasher.Verify("4822", reference));
        Assert.True(PinHasher.Verify("5937", PinHasher.Hash("5937")));
    }
}
