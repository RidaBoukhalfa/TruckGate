using System.Security.Cryptography;
using Dapper;
using Npgsql;
using TruckGate.Application;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<DatabaseFixture>
{
}

/// <summary>
/// Creates a throw-away database on the PostgreSQL given by TRUCKGATE_TEST_PG (a superuser
/// connection string), migrates it as the owner and connects the services as truckgate_app,
/// exactly like production. All test classes share it and run one after another.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string AppPassword = "app_test";
    private readonly List<string> _databases = [];
    private NpgsqlDataSource _server = null!;
    private string _baseConnection = "";
    private int _nextId = 100;

    public NpgsqlDataSource Admin { get; private set; } = null!;
    public NpgsqlDataSource App { get; private set; } = null!;
    public Db Db { get; private set; } = null!;
    public GateService Gate { get; private set; } = null!;
    public AdminService AdminSvc { get; private set; } = null!;
    public AuditService Audit { get; private set; } = null!;
    public short GateId { get; private set; }

    public static readonly Actor Tester = Actor.Development("tests");

    public async Task InitializeAsync()
    {
        _baseConnection = Environment.GetEnvironmentVariable("TRUCKGATE_TEST_PG")
                          ?? throw new InvalidOperationException(
                              "Set TRUCKGATE_TEST_PG to a PostgreSQL superuser connection string " +
                              "(e.g. Host=localhost;Username=postgres;Password=postgres).");
        _server = NpgsqlDataSource.Create(_baseConnection);
        await using (var c = await _server.OpenConnectionAsync())
        {
            await c.ExecuteAsync($"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'truckgate_app') THEN
                        CREATE ROLE truckgate_app LOGIN PASSWORD '{AppPassword}';
                    ELSE
                        ALTER ROLE truckgate_app LOGIN PASSWORD '{AppPassword}';
                    END IF;
                END $$;
                """);
        }

        (Admin, App) = await CreateDatabaseAsync();
        Db = new Db(App);
        Gate = new GateService(Db);
        AdminSvc = new AdminService(Db);
        Audit = new AuditService(Db);
        GateId = (short)await AdminSvc.CreateGateAsync("G1", "Main gate", null, Tester);
    }

    /// <summary>A separate database for tests that deliberately corrupt data.</summary>
    public async Task<(NpgsqlDataSource Admin, Db Db)> CreateIsolatedDatabaseAsync()
    {
        var (admin, app) = await CreateDatabaseAsync();
        return (admin, new Db(app));
    }

    /// <summary>Same, plus the application connection string (to start the real web server on it).</summary>
    public async Task<(Db Db, string AppConnection)> CreateIsolatedDatabaseForServerAsync()
    {
        var name = await CreateDatabaseNameAsync();
        // NpgsqlDataSource.ConnectionString drops the password, so the string is built here.
        var appConnection = AppConnectionString(name);
        return (new Db(NpgsqlDataSource.Create(appConnection)), appConnection);
    }

    private string AppConnectionString(string name) => new NpgsqlConnectionStringBuilder(_baseConnection)
    {
        Database = name, Username = "truckgate_app", Password = AppPassword,
    }.ConnectionString;

    private async Task<string> CreateDatabaseNameAsync()
    {
        var name = "tg_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var c = await _server.OpenConnectionAsync())
            await c.ExecuteAsync($"CREATE DATABASE {name}");
        _databases.Add(name);
        await using var admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_baseConnection) { Database = name }.ConnectionString);
        await DbMigrator.MigrateAsync(admin);
        return name;
    }

    private async Task<(NpgsqlDataSource Admin, NpgsqlDataSource App)> CreateDatabaseAsync()
    {
        var name = await CreateDatabaseNameAsync();
        var admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_baseConnection) { Database = name }.ConnectionString);
        return (admin, NpgsqlDataSource.Create(AppConnectionString(name)));
    }

    public int NextId() => Interlocked.Increment(ref _nextId);

    public Task<long> DbNowAsync() =>
        Db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint"));

    /// <summary>A driver, a truck and an enrolled quarry phone, all with fresh ids.</summary>
    public async Task<Fleet> NewFleetAsync()
    {
        var driverId = await AdminSvc.CreateDriverAsync("Driver " + NextId(), null, "EMPLOYEE", Tester);
        var truckId = NextId();
        await AdminSvc.CreateTruckAsync(truckId, "T" + truckId, "Foton", driverId, $"{truckId:D5} 216 16", Tester);
        var phone = new QuarryPhone(NextId());
        await AdminSvc.EnrollDeviceAsync(phone.DeviceId, "QUARRY_PHONE", "Phone " + phone.DeviceId, phone.PublicKey, Tester);
        return new Fleet(truckId, driverId, phone);
    }

    public async Task DisposeAsync()
    {
        Admin?.Dispose();
        App?.Dispose();
        NpgsqlConnection.ClearAllPools();
        await using (var c = await _server.OpenConnectionAsync())
            foreach (var db in _databases)
                await c.ExecuteAsync($"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
        await _server.DisposeAsync();
    }
}

public sealed record Fleet(int TruckId, int DriverId, QuarryPhone Phone);

/// <summary>Software stand-in for the Android Keystore key of a quarry phone.</summary>
public sealed class QuarryPhone(int deviceId) : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public int DeviceId { get; set; } = deviceId;
    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    public string Ticket(long seq, int truckId, int driverId, long issuedAt, byte flags = 0, int? claimDeviceId = null,
        byte[]? photoSha256 = null, int operatorId = 1) =>
        TicketCodec.ToQrText(TicketBytes(seq, truckId, driverId, issuedAt, flags, claimDeviceId, photoSha256, operatorId));

    public byte[] TicketBytes(long seq, int truckId, int driverId, long issuedAt, byte flags = 0, int? claimDeviceId = null,
        byte[]? photoSha256 = null, int operatorId = 1)
    {
        var fields = new TicketFields(TicketFields.KindTransport, (ushort)(claimDeviceId ?? DeviceId), (uint)seq,
            (uint)issuedAt, 0, (ushort)truckId, (ushort)driverId, (ushort)operatorId, 35_000_000, -1_000_000, flags, 1,
            photoSha256 ?? new byte[32]);
        var signed = TicketCodec.EncodeSignedPart(fields);
        return signed.Concat(Sign(signed)).ToArray();
    }

    public byte[] Sign(byte[] data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public string SignVoidB64(string ticketId, long at, string reason) =>
        Convert.ToBase64String(Sign(VoidRecord.Message(ticketId, at, reason)));

    public void Dispose() => _key.Dispose();
}
