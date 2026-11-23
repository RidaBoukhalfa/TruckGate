using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using TruckGate.Application;
using TruckGate.Application.Cameras;
using TruckGate.Crypto;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

/// <summary>Rolling-buffer arithmetic, without cameras.</summary>
public class SegmentIndexTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);

    private static Segment S(double from, double to, bool complete = true) =>
        new($"/b/{from}.ts", T0.AddSeconds(from), T0.AddSeconds(to), complete);

    [Fact]
    public void Window_is_covered_only_without_holes_and_up_to_both_ends()
    {
        var all = new[] { S(-34, -32), S(-32, -30), S(-30, -28) }.Concat(Enumerable.Range(-14, 30).Select(i => S(i * 2, i * 2 + 2)))
            .Append(S(32, 34, complete: false)).ToList();
        var full = SegmentIndex.Select(all, T0.AddSeconds(-30), T0.AddSeconds(30));
        Assert.True(SegmentIndex.Covers(full, T0.AddSeconds(-30), T0.AddSeconds(30), TimeSpan.FromSeconds(1.5)));
        Assert.DoesNotContain(full, s => !s.Complete);

        var hole = full.Where(s => s.StartUtc != T0.AddSeconds(10)).ToList();
        Assert.False(SegmentIndex.Covers(hole, T0.AddSeconds(-30), T0.AddSeconds(30), TimeSpan.FromSeconds(1.5)));
        var lateStart = full.Skip(3).ToList();
        Assert.False(SegmentIndex.Covers(lateStart, T0.AddSeconds(-30), T0.AddSeconds(30), TimeSpan.FromSeconds(1.5)));
        Assert.Empty(SegmentIndex.Select(all, T0.AddSeconds(100), T0.AddSeconds(130)));
    }

    [Fact]
    public void Credentials_never_reach_logs_and_ffmpeg_copies_the_stream()
    {
        Assert.Equal("Input #0, rtsp, from 'rtsp://***@192.168.1.64:554/ch1':",
            Ffmpeg.Redact("Input #0, rtsp, from 'rtsp://admin:S3cret@192.168.1.64:554/ch1':"));
        Assert.Equal("rtsp://***@192.168.1.64/ch1 failed", Ffmpeg.Redact("rtsp://admin:p@ss:1@192.168.1.64/ch1 failed"));
        var url = CameraRecorder.InputUrl("rtsp://192.168.1.64:554/Streaming/Channels/102", "admin", "p@ss:1");
        Assert.Equal("rtsp://admin:p@ss:1@192.168.1.64:554/Streaming/Channels/102", url);
        var args = CameraRecorder.Arguments(url, "/buf/r1_%08d.ts", 2);
        Assert.Contains("copy", args);
        Assert.Contains("tcp", args);
        Assert.Equal("segment", args[args.ToList().IndexOf("-f") + 1]);
        Assert.Throws<TruckGateException>(() => CameraService.ValidateStreamUrl("rtsp://admin:x@10.0.0.2/ch1"));
        Assert.Equal("rtsp://10.0.0.2/ch1", CameraService.ValidateStreamUrl(" rtsp://10.0.0.2/ch1 "));
    }

    [Fact]
    public void Segment_files_are_timed_from_their_writes_and_the_running_file_is_incomplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tg-seg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 3; i++)
        {
            var p = Path.Combine(dir, SegmentIndex.FileName(7, i));
            File.WriteAllBytes(p, [1, 2, 3]);
            File.SetLastWriteTimeUtc(p, T0.AddSeconds(2 * (i + 1)));
        }
        var s = SegmentIndex.Scan(dir, 2, activeRun: 7);
        Assert.Equal(3, s.Count);
        Assert.Equal(T0.AddSeconds(2), s[1].StartUtc);
        Assert.Equal(T0.AddSeconds(4), s[1].EndUtc);
        Assert.False(s[2].Complete);
        Assert.True(SegmentIndex.Scan(dir, 2, activeRun: null)[2].Complete);
    }
}

/// <summary>
/// Real event video: a test RTSP server (mediamtx) fed by ffmpeg's synthetic test pattern stands in
/// for a camera. These recordings prove the software path (buffer, window, join, fingerprint,
/// metadata, failure handling); they are NOT a test of the factory's cameras.
/// Needs ffmpeg in PATH and TRUCKGATE_TEST_MEDIAMTX (the CI job installs both).
/// </summary>
[Collection("db")]
public class VideoTests(DatabaseFixture fx)
{
    private static readonly Actor T = DatabaseFixture.Tester;

    private static string? MediaMtx()
    {
        var path = Environment.GetEnvironmentVariable("TRUCKGATE_TEST_MEDIAMTX");
        if (string.IsNullOrEmpty(path) && Environment.GetEnvironmentVariable("CI") == "true")
            throw new InvalidOperationException("CI must provide TRUCKGATE_TEST_MEDIAMTX and ffmpeg for the video tests.");
        return string.IsNullOrEmpty(path) ? null : path;
    }

    [Fact]
    public async Task Scan_saves_30_seconds_before_and_after_per_camera_and_a_dead_camera_fails_visibly()
    {
        var mediamtx = MediaMtx();
        if (mediamtx is null) return; // not available outside CI: see the class comment

        var work = Path.Combine(Path.GetTempPath(), "tg-video-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var config = Path.Combine(work, "mediamtx.yml");
        await File.WriteAllTextAsync(config, """
            logLevel: warn
            rtspAddress: :18554
            rtmp: no
            hls: no
            webrtc: no
            srt: no
            api: no
            metrics: no
            pprof: no
            playback: no
            paths:
              all_others:
            """);
        using var server = Process.Start(new ProcessStartInfo(mediamtx, config) { UseShellExecute = false })!;
        await Task.Delay(1500);
        using var publisher = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=15",
                "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "15",
                "-f", "rtsp", "-rtsp_transport", "tcp", "rtsp://127.0.0.1:18554/cam1",
            },
        })!;
        var recorders = new List<CameraRecorder>();
        using var stop = new CancellationTokenSource();
        try
        {
            var (_, db) = await fx.CreateIsolatedDatabaseAsync();
            var admin = new AdminService(db);
            var gateId = (short)await admin.CreateGateAsync("G1", "Main gate", null, T);
            var driver = await admin.CreateDriverAsync("سائق 1", null, "CONTRACTOR", T);
            await admin.CreateTruckAsync(5, "05", "Foton", driver, "01234 216 16", T);
            var phone = new QuarryPhone(3);
            await admin.EnrollDeviceAsync(phone.DeviceId, "QUARRY_PHONE", "Phone", phone.PublicKey, T);
            var storage = new StorageOptions(Path.Combine(work, "data"));
            var secrets = SecretBox.Ephemeral();
            var cams = new CameraService(db, secrets, storage);
            var good = await cams.CreateAsync(new CameraInput("Entry camera", gateId, "OVERVIEW", false, 1, 0, "URL", null, null,
                "http://127.0.0.1:1/snapshot.jpg", null, null, null, null, null, "rtsp://127.0.0.1:18554/cam1", true, "entry lane"), T);
            var dead = await cams.CreateAsync(new CameraInput("Broken camera", gateId, "PLATE", false, 1, 0, "URL", null, null,
                "http://127.0.0.1:1/snapshot.jpg", null, null, null, null, null, "rtsp://127.0.0.1:18555/nothing", true, null), T);
            await db.InTransactionAsync(T, (c, tx) => c.ExecuteAsync(
                "UPDATE tg.settings SET value = '0' WHERE key IN ('gate.min_truck_gap_s', 'gate.min_unload_s')", transaction: tx));

            var video = new VideoService(db, new VideoOptions("ffmpeg", "ffprobe", Path.Combine(work, "buffer"), storage.Root));
            foreach (var id in new[] { good, dead })
                recorders.Add(new CameraRecorder(new RecordingCamera(id, "cam" + id, 1,
                    id == good ? "rtsp://127.0.0.1:18554/cam1" : "rtsp://127.0.0.1:18555/nothing", null, null),
                    video.BufferDir(id), "ffmpeg", 2));
            var loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var r in recorders) r.EnsureRunning(DateTime.UtcNow);
                    await Task.Delay(500);
                }
            });

            // fill the rolling buffer, then two trucks are scanned one second apart
            await Task.Delay(TimeSpan.FromSeconds(36));
            var gate = new GateService(db);
            var now = await db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT extract(epoch FROM now())::bigint"));
            var scan1 = await gate.ScanEntryAsync(new ScanEntryRequest(gateId, phone.Ticket(1, 5, driver, now - 900), null), T);
            Assert.Equal(2, await video.RegisterScanAsync(scan1.ScanId));
            await Task.Delay(1000);
            var scan2 = await gate.ScanEntryAsync(new ScanEntryRequest(gateId, phone.Ticket(2, 5, driver, now - 800), null), T);
            Assert.Equal(2, await video.RegisterScanAsync(scan2.ScanId));
            Assert.All(await video.ListForScanAsync(scan1.ScanId), v => Assert.Equal("RECORDING", v.Status));

            // finalize like the service does, until the four recordings are done (window end + margin)
            var deadline = DateTime.UtcNow.AddSeconds(75);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var r in await video.DueAsync(DateTime.UtcNow, 2))
                {
                    var rec = recorders.Single(x => x.Camera.Id == r.CameraId);
                    var segs = rec.Segments();
                    if (!VideoService.Ready(r, segs, DateTime.UtcNow, 2)) continue;
                    await video.FinalizeAsync(r, segs, 2, rec.LastError is { } e ? "NO_FOOTAGE: " + e : null);
                }
                if (await db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT count(*) FROM tg.video_recordings WHERE status = 'RECORDING'")) == 0)
                    break;
                await Task.Delay(1000);
            }

            var first = (await video.ListForScanAsync(scan1.ScanId)).Single(v => v.CameraId == good);
            var second = (await video.ListForScanAsync(scan2.ScanId)).Single(v => v.CameraId == good);
            Assert.Equal("OK", first.Status);
            Assert.Equal("OK", second.Status);
            Assert.InRange(first.DurationMs!.Value, 58_000, 66_000); // 30 s before + 30 s after, cut at keyframes
            Assert.Equal(TimeSpan.FromSeconds(30), first.ScanAt - first.WindowStart);
            Assert.Equal(TimeSpan.FromSeconds(30), first.WindowEnd - first.ScanAt);

            // separate files, never mixed, in the trip folder, fingerprint and description kept
            var rows = (await db.ReadAsync(c => c.QueryAsync<(long Id, string Path, string MetadataPath, byte[] Sha)>(
                "SELECT id, path, metadata_path, sha256 FROM tg.video_recordings WHERE camera_id = @good ORDER BY id", new { good }))).ToList();
            Assert.Equal(2, rows.Count);
            Assert.NotEqual(rows[0].Path, rows[1].Path);
            Assert.Matches(@"^media/\d{4}/\d{2}/\d{2}/trips/T-\d{6}/entry_cam\d{2}_v\d+\.mp4$", rows[0].Path);
            var file = Path.Combine(storage.Root, rows[0].Path);
            Assert.Equal(rows[0].Sha, SHA256.HashData(await File.ReadAllBytesAsync(file)));
            using (var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(storage.Root, rows[0].MetadataPath))))
            {
                Assert.Equal("Q3-000001", meta.RootElement.GetProperty("receipt_id").GetString());
                Assert.Equal(scan1.TripNumber, meta.RootElement.GetProperty("trip_number").GetString());
                Assert.Equal("ENTRY", meta.RootElement.GetProperty("gate_operation").GetString());
            }

            // the dead camera: FAILED, alerted, sent to review — never shown as recorded
            var failed = (await video.ListForScanAsync(scan1.ScanId)).Single(v => v.CameraId == dead);
            Assert.Equal("FAILED", failed.Status);
            Assert.NotNull(failed.Error);
            Assert.True(await db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT count(*) FROM tg.alerts WHERE type = 'RECORDING_FAILED'")) >= 2);
            Assert.True(await db.ReadAsync(c => c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM tg.reviews WHERE 'VIDEO_MISSING_ENTRY' = ANY(reason_codes))")));

            // served only while it matches its fingerprint
            Assert.Equal(Path.GetFullPath(file), await video.VerifiedPathAsync(rows[0].Id));
            await File.AppendAllTextAsync(Path.Combine(storage.Root, rows[1].Path), "x");
            var tampered = await Assert.ThrowsAsync<TruckGateException>(() => video.VerifiedPathAsync(rows[1].Id));
            Assert.Equal("VIDEO_TAMPERED", tampered.Code);

            // retention removes the file, keeps the row and its fingerprint
            await db.InTransactionAsync(T, (c, tx) => c.ExecuteAsync(
                "UPDATE tg.settings SET value = 'false' WHERE key = 'video.keep_reviewed'", transaction: tx));
            Assert.True(await video.PurgeAsync(DateTime.UtcNow.AddDays(40)) >= 2);
            Assert.False(File.Exists(file));
            Assert.Equal("PURGED", (await video.ListForScanAsync(scan1.ScanId)).Single(v => v.CameraId == good).Status);
            Assert.True((await new AuditService(db).VerifyAsync()).Ok);

            stop.Cancel();
            await loop;
        }
        finally
        {
            stop.Cancel();
            foreach (var r in recorders) r.Dispose();
            try { publisher.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}
