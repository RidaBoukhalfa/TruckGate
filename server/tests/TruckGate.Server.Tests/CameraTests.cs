using Dapper;
using TruckGate.Application;
using TruckGate.Application.Cameras;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

[Collection("db")]
public class CameraTests(DatabaseFixture fx)
{
    private sealed record Env(Db Db, AdminService Admin, GateService Gate, CameraService Cameras, string Storage, short GateId);

    private async Task<Env> NewEnvAsync()
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var storage = Path.Combine(Path.GetTempPath(), "tg-snapshots-" + Guid.NewGuid().ToString("N")[..8]);
        var admin = new AdminService(db);
        var gateId = (short)await admin.CreateGateAsync("G1", "Main gate", null, DatabaseFixture.Tester);
        return new Env(db, admin, new GateService(db), new CameraService(db, SecretBox.Ephemeral(), new StorageOptions(storage)),
            storage, gateId);
    }

    private static CameraInput Onvif(FakeCamera cam, OnvifProfile p, short gateId, string role, bool capture = true) =>
        new(p.Name, gateId, role, capture, 3, 50, "ONVIF", cam.DeviceUrl, p.Token, p.SnapshotUri!, FakeCamera.User,
            FakeCamera.Password, "FakeVision", "NVR-2CH", "SN123");

    [Fact]
    public async Task Probing_finds_the_recorder_channels_and_their_snapshot_addresses()
    {
        await using var cam = await FakeCamera.StartAsync();
        var env = await NewEnvAsync();

        var info = await env.Cameras.ProbeOnvifAsync(cam.DeviceUrl, FakeCamera.User, FakeCamera.Password);

        Assert.Equal("FakeVision", info.Manufacturer);
        Assert.Equal("NVR-2CH", info.Model);
        Assert.Equal(new[] { "ch1", "ch2" }, info.Profiles.Select(p => p.Token));
        Assert.Equal(1920, info.Profiles[0].Width);
        // advertised host was wrong on purpose; the reachable one is kept
        Assert.Equal(cam.BaseUrl + "/snap/2.jpg", info.Profiles[1].SnapshotUri);
    }

    [Fact]
    public async Task Wrong_camera_password_is_reported()
    {
        await using var cam = await FakeCamera.StartAsync();
        var env = await NewEnvAsync();

        var e = await Assert.ThrowsAsync<TruckGateException>(() =>
            env.Cameras.ProbeOnvifAsync(cam.DeviceUrl, FakeCamera.User, "wrong"));

        Assert.Equal("ONVIF_FAILED", e.Code);
        Assert.Contains("Authorized", e.Message);
    }

    [Fact]
    public async Task Selected_cameras_take_a_burst_that_is_stored_with_its_fingerprint()
    {
        await using var cam = await FakeCamera.StartAsync();
        var env = await NewEnvAsync();
        var info = await env.Cameras.ProbeOnvifAsync(cam.DeviceUrl, FakeCamera.User, FakeCamera.Password);
        var plate = await env.Cameras.CreateAsync(Onvif(cam, info.Profiles[0], env.GateId, "PLATE"), DatabaseFixture.Tester);
        await env.Cameras.CreateAsync(Onvif(cam, info.Profiles[1], env.GateId, "CARGO"), DatabaseFixture.Tester);
        // a camera that is not selected for capture takes no picture
        await env.Cameras.CreateAsync(Onvif(cam, info.Profiles[1], env.GateId, "OVERVIEW", capture: false), DatabaseFixture.Tester);

        var scan = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, "TG1:NOT-A-TICKET", null), DatabaseFixture.Tester);
        var frames = await env.Cameras.CaptureAsync(env.GateId);
        var stored = await env.Cameras.StoreAsync(scan.ScanId, frames);

        Assert.Equal(6, frames.Count);
        Assert.Equal(6, stored.Count);
        Assert.Equal(3, stored.Count(s => s.Role == "PLATE"));
        var bytes = await env.Cameras.ReadSnapshotAsync(stored[0].Id);
        Assert.True(SnapshotFetcher.IsJpeg(bytes));

        var lastOk = await env.Db.ReadAsync(c => c.ExecuteScalarAsync<DateTime?>(
            "SELECT last_ok_at FROM tg.camera_health WHERE camera_id = @plate", new { plate }));
        Assert.NotNull(lastOk);
    }

    [Fact]
    public async Task A_replaced_picture_file_is_detected_and_refused()
    {
        await using var cam = await FakeCamera.StartAsync();
        var env = await NewEnvAsync();
        var info = await env.Cameras.ProbeOnvifAsync(cam.DeviceUrl, FakeCamera.User, FakeCamera.Password);
        await env.Cameras.CreateAsync(Onvif(cam, info.Profiles[0], env.GateId, "CARGO"), DatabaseFixture.Tester);
        var scan = await env.Gate.ScanEntryAsync(new ScanEntryRequest(env.GateId, "TG1:X", null), DatabaseFixture.Tester);
        var stored = await env.Cameras.StoreAsync(scan.ScanId, await env.Cameras.CaptureAsync(env.GateId));

        var path = await env.Db.ReadAsync(c => c.ExecuteScalarAsync<string>(
            "SELECT path FROM tg.snapshots WHERE id = @id", new { id = stored[0].Id }));
        await File.WriteAllBytesAsync(Path.Combine(env.Storage, path!), [0xFF, 0xD8, 0x00, 0x01, 0xFF, 0xD9]);

        var e = await Assert.ThrowsAsync<TruckGateException>(() => env.Cameras.ReadSnapshotAsync(stored[0].Id));
        Assert.Equal("SNAPSHOT_TAMPERED", e.Code);
        Assert.Equal("CRITICAL", await env.Db.ReadAsync(c => c.ExecuteScalarAsync<string>(
            "SELECT severity FROM tg.alerts WHERE type = 'SNAPSHOT_TAMPERED'")));
    }

    [Fact]
    public async Task A_dead_camera_does_not_block_the_scan_and_raises_one_alert()
    {
        var env = await NewEnvAsync();
        await env.Cameras.CreateAsync(new CameraInput("Dead", env.GateId, "PLATE", true, 2, 0, "URL", null, null,
            "http://127.0.0.1:1/snap.jpg", null, null, null, null, null), DatabaseFixture.Tester);

        var first = await env.Cameras.CaptureAsync(env.GateId);
        var second = await env.Cameras.CaptureAsync(env.GateId);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(1L, await env.Db.ReadAsync(c => c.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM tg.alerts WHERE type = 'CAMERA_FAILED'")));
        Assert.NotNull(await env.Db.ReadAsync(c => c.ExecuteScalarAsync<string?>(
            "SELECT last_error FROM tg.camera_health LIMIT 1")));
    }

    [Fact]
    public async Task Camera_password_is_encrypted_and_never_in_the_audit_log()
    {
        await using var cam = await FakeCamera.StartAsync();
        var env = await NewEnvAsync();
        var info = await env.Cameras.ProbeOnvifAsync(cam.DeviceUrl, FakeCamera.User, FakeCamera.Password);
        var id = await env.Cameras.CreateAsync(Onvif(cam, info.Profiles[0], env.GateId, "PLATE"), DatabaseFixture.Tester);

        var stored = await env.Db.ReadAsync(c => c.ExecuteScalarAsync<byte[]>(
            "SELECT password_enc FROM tg.cameras WHERE id = @id", new { id }));
        var leaks = await env.Db.ReadAsync(c => c.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM tg.audit_log WHERE payload::text LIKE '%password_enc%' OR payload::text LIKE @p",
            new { p = "%" + FakeCamera.Password + "%" }));

        Assert.NotNull(stored);
        Assert.DoesNotContain(FakeCamera.Password, System.Text.Encoding.UTF8.GetString(stored!));
        Assert.Equal(0L, leaks);
        Assert.True((await env.Cameras.ListAsync()).Single().HasPassword);
    }

    [Fact]
    public async Task Changing_a_camera_role_bumps_its_version_for_training_data()
    {
        await using var cam = await FakeCamera.StartAsync();
        var env = await NewEnvAsync();
        var info = await env.Cameras.ProbeOnvifAsync(cam.DeviceUrl, FakeCamera.User, FakeCamera.Password);
        var id = await env.Cameras.CreateAsync(Onvif(cam, info.Profiles[0], env.GateId, "PLATE"), DatabaseFixture.Tester);

        await env.Cameras.UpdateAsync(id, new CameraUpdate(null, null, false, null, false, null, null, null, null, null, null),
            DatabaseFixture.Tester);
        var afterToggle = (await env.Cameras.ListAsync()).Single().ConfigVersion;
        await env.Cameras.UpdateAsync(id, new CameraUpdate(null, null, false, "CARGO", null, null, null, null, null, null, null),
            DatabaseFixture.Tester);
        var afterRole = (await env.Cameras.ListAsync()).Single();

        Assert.Equal(1, afterToggle);
        Assert.Equal(2, afterRole.ConfigVersion);
        Assert.Equal("CARGO", afterRole.Role);
        Assert.True(afterRole.HasPassword);
    }
}
