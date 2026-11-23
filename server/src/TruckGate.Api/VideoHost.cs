using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using TruckGate.Application;
using TruckGate.Application.Cameras;
using TruckGate.Data;

namespace TruckGate.Api;

/// <summary>
/// Event video, every second: keeps one ffmpeg per recording camera (rolling buffer), finalizes the
/// recordings whose window has ended, reports camera health; every 10 s trims the buffers; every
/// 10 minutes applies the retention rule and checks the free disk space.
/// </summary>
internal sealed class VideoRecorderHost(Db db, VideoService video, SecretBox secrets, ILogger<VideoRecorderHost> log)
    : BackgroundService
{
    private static readonly Actor VideoActor = Actor.System("video");
    private readonly Dictionary<int, (CameraRecorder Recorder, string Key)> _recorders = new();
    private readonly Dictionary<int, DateTime> _failingSince = new();
    private int _pre = 30, _post = 30, _segment = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastReload = DateTime.MinValue;
        var lastHealth = DateTime.MinValue;
        var lastPurge = DateTime.UtcNow - TimeSpan.FromMinutes(9); // first purge one minute after start
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                try
                {
                    if (now - lastReload >= TimeSpan.FromSeconds(10))
                    {
                        await ReloadAsync(stoppingToken);
                        lastReload = now;
                    }
                    foreach (var (recorder, _) in _recorders.Values) recorder.EnsureRunning(now);
                    await FinalizeDueAsync(now, stoppingToken);
                    if (now - lastHealth >= TimeSpan.FromSeconds(10))
                    {
                        await HealthAsync(now, stoppingToken);
                        await PruneAsync(now, stoppingToken);
                        lastHealth = now;
                    }
                    if (now - lastPurge >= TimeSpan.FromMinutes(10))
                    {
                        var purged = await video.PurgeAsync(now, stoppingToken);
                        if (purged > 0) log.LogInformation("Video retention: {Count} recordings removed from disk", purged);
                        var storage = await video.StorageAsync(alert: true, stoppingToken);
                        if (storage.Low) log.LogWarning("Disk space low: {Free} GB free", storage.FreeBytes / (1024 * 1024 * 1024));
                        lastPurge = now;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "Video recorder loop failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // service stopping
        }
        finally
        {
            foreach (var (recorder, _) in _recorders.Values) recorder.Dispose();
            _recorders.Clear();
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        var (cameras, settings) = await db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            var rows = (await conn.QueryAsync<CameraRow>("""
                SELECT id, label, config_version, stream_url, username, password_enc FROM tg.cameras
                WHERE active AND record_video AND stream_url IS NOT NULL ORDER BY id
                """)).ToList();
            return (rows, s);
        }, ct);
        _pre = settings.Int("video.pre_s", 30);
        _post = settings.Int("video.post_s", 30);
        var segment = settings.Int("video.segment_s", 2);

        var wanted = new Dictionary<int, (RecordingCamera Camera, string Key)>();
        foreach (var c in cameras)
        {
            string? password = null;
            try { if (c.PasswordEnc is not null) password = secrets.Decrypt(c.PasswordEnc); }
            catch (CryptographicException) { log.LogWarning("Camera {Id}: stored password cannot be decrypted", c.Id); }
            var cam = new RecordingCamera(c.Id, c.Label, c.ConfigVersion, c.StreamUrl, c.Username, password);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{c.ConfigVersion}|{c.StreamUrl}|{c.Username}|{password}|{segment}")));
            wanted[c.Id] = (cam, key);
        }

        foreach (var id in _recorders.Keys.ToList())
        {
            if (wanted.TryGetValue(id, out var w) && w.Key == _recorders[id].Key) continue;
            _recorders[id].Recorder.Dispose();
            _recorders.Remove(id);
            if (!wanted.ContainsKey(id)) await SetHealthAsync(id, "OFF", null, null, ct);
        }
        foreach (var (id, w) in wanted)
        {
            if (_recorders.ContainsKey(id)) continue;
            _recorders[id] = (new CameraRecorder(w.Camera, video.BufferDir(id), video.Options.FfmpegPath, segment), w.Key);
            _segment = segment;
            log.LogInformation("Video: recording camera {Id} ({Label})", id, w.Camera.Label);
        }
        _segment = segment;
    }

    private async Task FinalizeDueAsync(DateTime now, CancellationToken ct)
    {
        var due = await video.DueAsync(now, _segment, ct);
        foreach (var r in due)
        {
            var hasRecorder = _recorders.TryGetValue(r.CameraId, out var rec);
            var segments = hasRecorder ? rec.Recorder.Segments() : SegmentIndex.Scan(video.BufferDir(r.CameraId), _segment, null);
            if (!VideoService.Ready(r, segments, now, _segment)) continue;
            string? reason = !hasRecorder
                ? "CAMERA_NOT_RECORDING: video recording is off for this camera or it was removed"
                : rec.Recorder.LastError is { } e ? "NO_FOOTAGE: " + e : null;
            var result = await video.FinalizeAsync(r, segments, _segment, reason, ct);
            if (result.Status == "OK")
                log.LogInformation("Video {Id}: {Status}, {Duration} ms", result.Id, result.Status, result.DurationMs);
            else
                log.LogWarning("Video {Id}: {Status} {Error}", result.Id, result.Status, result.Error);
        }
    }

    private async Task HealthAsync(DateTime now, CancellationToken ct)
    {
        foreach (var (id, (recorder, _)) in _recorders)
        {
            var segments = recorder.Segments();
            DateTime? lastFrame = null;
            var dir = video.BufferDir(id);
            if (Directory.Exists(dir))
            {
                var newest = new DirectoryInfo(dir).EnumerateFiles("r*_*.ts").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest is not null) lastFrame = newest.LastWriteTimeUtc;
            }
            string status;
            if (recorder.Running && lastFrame is { } lf && recorder.ActiveRun is not null && now - lf < TimeSpan.FromSeconds(10)
                && segments.Any(s => !s.Complete || s.EndUtc > recorder.StartedAt))
                status = "RECORDING";
            else if (recorder.Running && now - recorder.StartedAt < TimeSpan.FromSeconds(20))
                status = "STARTING";
            else
                status = "ERROR";
            var error = status == "ERROR" ? recorder.LastError ?? "no video received from the camera" : null;
            await SetHealthAsync(id, status, error, lastFrame, ct);

            if (status == "ERROR")
            {
                if (!_failingSince.TryGetValue(id, out var since)) _failingSince[id] = since = now;
                if (now - since > TimeSpan.FromSeconds(60))
                {
                    var period = now.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture) + (now.Minute < 30 ? "a" : "b");
                    await db.InTransactionAsync(VideoActor, async (conn, tx) =>
                    {
                        await Watchdog.AlertOnceAsync(conn, tx, "RECORDER_OFFLINE", "MEDIUM", "cameras",
                            id.ToString(CultureInfo.InvariantCulture), period, new { label = recorder.Camera.Label, error });
                        return 0;
                    }, ct);
                }
            }
            else
            {
                _failingSince.Remove(id);
            }
        }
    }

    private async Task PruneAsync(DateTime now, CancellationToken ct)
    {
        var pending = await video.PendingStartsAsync(ct);
        var keep = TimeSpan.FromSeconds(Math.Max(_pre + _post + 30, 120));
        foreach (var (id, (recorder, _)) in _recorders)
        {
            var keepAfter = now - keep;
            if (pending.TryGetValue(id, out var start) && start - TimeSpan.FromSeconds(_segment * 2) < keepAfter)
                keepAfter = start - TimeSpan.FromSeconds(_segment * 2);
            recorder.Prune(keepAfter);
        }
    }

    private Task SetHealthAsync(int id, string status, string? error, DateTime? lastFrame, CancellationToken ct) =>
        db.InTransactionAsync(VideoActor, (conn, tx) => conn.ExecuteAsync("""
            INSERT INTO tg.camera_health (camera_id, recorder_status, recorder_error, last_frame_at, recorder_updated_at)
            VALUES (@id, @status, @error, @lastFrame, now())
            ON CONFLICT (camera_id) DO UPDATE SET recorder_status = EXCLUDED.recorder_status,
                recorder_error = EXCLUDED.recorder_error,
                last_frame_at = coalesce(EXCLUDED.last_frame_at, tg.camera_health.last_frame_at),
                recorder_updated_at = now()
            """, new { id, status, error, lastFrame }, tx), ct);

    private sealed class CameraRow
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public int ConfigVersion { get; set; }
        public string StreamUrl { get; set; } = "";
        public string? Username { get; set; }
        public byte[]? PasswordEnc { get; set; }
    }
}
