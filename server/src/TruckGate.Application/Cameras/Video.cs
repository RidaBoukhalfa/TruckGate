using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using TruckGate.Data;

namespace TruckGate.Application.Cameras;

/// <summary>
/// Event video. <paramref name="FfmpegPath"/> and <paramref name="FfprobePath"/> are the programs
/// (bundled with the Windows installer); <paramref name="BufferRoot"/> holds the rolling buffer (a few
/// minutes per camera); finished recordings go under <paramref name="MediaRoot"/>.
/// </summary>
public sealed record VideoOptions(string FfmpegPath, string? FfprobePath, string BufferRoot, string MediaRoot);

/// <summary>One piece of the rolling buffer. Times are the server's clock (UTC).</summary>
public sealed record Segment(string Path, DateTime StartUtc, DateTime EndUtc, bool Complete);

/// <summary>
/// The buffer of one camera: files <c>r{run}_{seq:00000000}.ts</c> written by ffmpeg's segment muxer.
/// A file is complete once a later file of the same run exists or the run has ended. Its end is its
/// last write time; its start is the end of the previous file of the run (the stream is continuous).
/// </summary>
public static class SegmentIndex
{
    private static readonly Regex Name = new(@"^r(\d{1,19})_(\d{1,10})\.ts$", RegexOptions.CultureInvariant);

    public static string FileName(long run, long seq) => $"r{run}_{seq:00000000}.ts";

    public static IReadOnlyList<Segment> Scan(string dir, double nominalSeconds, long? activeRun)
    {
        if (!Directory.Exists(dir)) return [];
        var files = new List<(long Run, long Seq, FileInfo Info)>();
        foreach (var path in Directory.EnumerateFiles(dir, "r*_*.ts"))
        {
            var m = Name.Match(System.IO.Path.GetFileName(path));
            if (!m.Success) continue;
            var info = new FileInfo(path);
            if (info.Length == 0) continue;
            files.Add((long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), info));
        }
        var result = new List<Segment>();
        foreach (var run in files.GroupBy(f => f.Run))
        {
            var list = run.OrderBy(f => f.Seq).ToList();
            DateTime? previousEnd = null;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i].Info;
                var end = info.LastWriteTimeUtc;
                DateTime start;
                if (previousEnd is { } pe && list[i].Seq == list[i - 1].Seq + 1) start = pe;
                else
                {
                    var created = info.CreationTimeUtc;
                    start = created < end && end - created < TimeSpan.FromSeconds(nominalSeconds * 4)
                        ? created : end - TimeSpan.FromSeconds(nominalSeconds);
                }
                bool complete = i < list.Count - 1 || run.Key != activeRun;
                result.Add(new Segment(info.FullName, start, end, complete));
                previousEnd = end;
            }
        }
        return result.OrderBy(s => s.StartUtc).ToList();
    }

    /// <summary>Complete segments overlapping [from, to], in time order.</summary>
    public static IReadOnlyList<Segment> Select(IEnumerable<Segment> all, DateTime fromUtc, DateTime toUtc) =>
        all.Where(s => s.Complete && s.EndUtc > fromUtc && s.StartUtc < toUtc).OrderBy(s => s.StartUtc).ToList();

    /// <summary>True when the segments cover the whole window without a hole longer than the tolerance.</summary>
    public static bool Covers(IReadOnlyList<Segment> selected, DateTime fromUtc, DateTime toUtc, TimeSpan tolerance)
    {
        if (selected.Count == 0) return false;
        if (selected[0].StartUtc > fromUtc + tolerance || selected[^1].EndUtc < toUtc - tolerance) return false;
        for (int i = 1; i < selected.Count; i++)
            if (selected[i].StartUtc - selected[i - 1].EndUtc > tolerance) return false;
        return true;
    }
}

/// <summary>Runs ffmpeg / ffprobe with an argument list (no shell, no quoting problems) and a time limit.</summary>
public static class Ffmpeg
{
    /// <summary>Removes "user:password@" (a password may itself contain '@') from any URL in a line of ffmpeg output.</summary>
    public static string Redact(string line) => Regex.Replace(line, @"(?<=://)[^/\s]*@", "***@");

    public static async Task<(int ExitCode, string StdErr, string StdOut)> RunAsync(string exe, IEnumerable<string> args,
        TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {exe}");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync(ct);
        var output = p.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{System.IO.Path.GetFileName(exe)} did not finish in {timeout.TotalSeconds:0} s");
        }
        return (p.ExitCode, Redact(await err), await output);
    }
}

/// <summary>A row waiting to be finalized.</summary>
public sealed class PendingRecording
{
    public long Id { get; set; }
    public long GateScanId { get; set; }
    public int CameraId { get; set; }
    public string CameraLabel { get; set; } = "";
    public int CameraConfigVersion { get; set; }
    public string Direction { get; set; } = "";
    public DateTime ScanAt { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
}

public sealed record RecordingResult(long Id, string Status, int? DurationMs, string? Error);

public sealed record StorageStatus(string Root, long FreeBytes, long TotalBytes, long VideoBytes, int Recordings,
    int MinFreeGb, bool Low);

/// <summary>
/// Event recordings: a row per (scan, camera) is opened when the ticket is scanned; after the window
/// (N seconds before the scan, N after) the segments of the rolling buffer are joined into one MP4 under
/// media/yyyy/MM/dd/trips/T-xxxxxx/ with a JSON description; the SHA-256 is kept and checked when the
/// file is served. A camera without footage gives a FAILED row and an alert — never a fake file.
/// </summary>
public sealed class VideoService(Db db, VideoOptions options)
{
    private static readonly Actor VideoActor = Actor.System("video");
    private readonly ConcurrentDictionary<long, (long Size, DateTime Write, DateTime CheckedAt)> _verified = new();

    public VideoOptions Options => options;

    public string BufferDir(int cameraId) => Path.Combine(options.BufferRoot, "cam" + cameraId.ToString(CultureInfo.InvariantCulture));

    /// <summary>Opens one recording per active recording camera of the scan's gate. Idempotent.</summary>
    public Task<int> RegisterScanAsync(long scanId, CancellationToken ct = default) =>
        db.InTransactionAsync(VideoActor, async (conn, tx) =>
        {
            var s = await SettingsBag.LoadAsync(conn, tx);
            var pre = s.Int("video.pre_s", 30);
            var post = s.Int("video.post_s", 30);
            return await conn.ExecuteAsync("""
                INSERT INTO tg.video_recordings (gate_scan_id, camera_id, camera_config_version, direction, scan_at,
                                                 window_start, window_end)
                SELECT gs.id, c.id, c.config_version, gs.direction, gs.scanned_at,
                       gs.scanned_at - make_interval(secs => @pre), gs.scanned_at + make_interval(secs => @post)
                FROM tg.gate_scans gs
                JOIN tg.cameras c ON c.gate_id = gs.gate_id AND c.active AND c.record_video AND c.stream_url IS NOT NULL
                WHERE gs.id = @scanId
                ON CONFLICT (gate_scan_id, camera_id) DO NOTHING
                """, new { scanId, pre, post }, tx);
        }, ct);

    public Task<IReadOnlyList<VideoItem>> ListForScanAsync(long scanId, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<VideoItem>>(async conn => (await conn.QueryAsync<VideoItem>(ReviewService.VideoSql + """
            WHERE v.gate_scan_id = @scanId ORDER BY v.camera_id
            """, new { scanId })).ToList(), ct);

    /// <summary>Recordings whose window has ended (plus a margin for the last segment to close).</summary>
    public Task<IReadOnlyList<PendingRecording>> DueAsync(DateTime nowUtc, double segmentSeconds, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<PendingRecording>>(async conn => (await conn.QueryAsync<PendingRecording>("""
            SELECT v.id, v.gate_scan_id, v.camera_id, c.label AS camera_label, v.camera_config_version, v.direction, v.scan_at,
                   v.window_start, v.window_end
            FROM tg.video_recordings v JOIN tg.cameras c ON c.id = v.camera_id
            WHERE v.status = 'RECORDING' AND v.window_end + make_interval(secs => @margin) <= @now
            ORDER BY v.id LIMIT 50
            """, new { now = nowUtc, margin = Math.Max(2.0, segmentSeconds) + 1.0 })).ToList(), ct);

    /// <summary>Oldest window start still waiting, per camera: the buffer keeps everything after it.</summary>
    public Task<IReadOnlyDictionary<int, DateTime>> PendingStartsAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyDictionary<int, DateTime>>(async conn => (await conn.QueryAsync<(int CameraId, DateTime Start)>("""
            SELECT camera_id, min(window_start) FROM tg.video_recordings WHERE status = 'RECORDING' GROUP BY camera_id
            """)).ToDictionary(x => x.CameraId, x => x.Start), ct);

    /// <summary>
    /// Decides whether a due recording can be finalized now: yes when a complete segment reaches the end of
    /// the window, or when the camera had enough time to deliver it (then with what exists).
    /// </summary>
    public static bool Ready(PendingRecording r, IReadOnlyList<Segment> segments, DateTime nowUtc, double segmentSeconds) =>
        segments.Any(s => s.Complete && s.EndUtc >= r.WindowEnd) ||
        nowUtc >= r.WindowEnd + TimeSpan.FromSeconds(Math.Max(15, segmentSeconds * 4));

    public async Task<RecordingResult> FinalizeAsync(PendingRecording r, IReadOnlyList<Segment> segments, double segmentSeconds,
        string? noFootageReason, CancellationToken ct = default)
    {
        var tolerance = TimeSpan.FromSeconds(Math.Max(1.5, segmentSeconds * 0.75));
        var selected = SegmentIndex.Select(segments, r.WindowStart, r.WindowEnd);
        var info = await db.ReadAsync(conn => conn.QuerySingleAsync<ScanInfo>("""
            SELECT gs.id AS scan_id, gs.direction, gs.scanned_at, g.code AS gate_code,
                   tr.id AS trip_id, tr.trip_number, tr.truck_id,
                   CASE WHEN tr.id IS NULL THEN NULL ELSE 'Q' || tr.ticket_device_id || '-' || tg.fmt_seq(tr.ticket_seq) END AS ticket_id,
                   CASE WHEN gs.ticket_device_id IS NULL THEN NULL ELSE 'Q' || gs.ticket_device_id || '-' || tg.fmt_seq(gs.ticket_seq) END AS scanned_ticket_id,
                   t.fleet_number,
                   (SELECT p.plate_text FROM tg.truck_plates p WHERE p.truck_id = tr.truck_id AND p.valid_to IS NULL
                    ORDER BY p.valid_from DESC LIMIT 1) AS plate,
                   (SELECT value #>> '{}' FROM tg.settings WHERE key = 'site.timezone') AS time_zone,
                   h.recorder_status, h.last_frame_at
            FROM tg.gate_scans gs
            JOIN tg.gates g ON g.id = gs.gate_id
            LEFT JOIN tg.trips tr ON tr.entry_scan_id = gs.id OR tr.exit_scan_id = gs.id
            LEFT JOIN tg.trucks t ON t.id = tr.truck_id
            LEFT JOIN tg.camera_health h ON h.camera_id = @cam
            WHERE gs.id = @scan
            """, new { scan = r.GateScanId, cam = r.CameraId }), ct);

        if (selected.Count == 0)
            return await FailAsync(r, info, noFootageReason ?? "NO_FOOTAGE: the camera delivered no video for this window", ct);

        var status = SegmentIndex.Covers(selected, r.WindowStart, r.WindowEnd, tolerance) ? "OK" : "PARTIAL";
        var zone = ResolveZone(info.TimeZone);
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.ScanAt, DateTimeKind.Utc), zone);
        var folder = info.TripNumber is { } tn ? Path.Combine("trips", tn) : Path.Combine("scans", "S-" + r.GateScanId);
        var relDir = Path.Combine("media", localDay.ToString("yyyy", CultureInfo.InvariantCulture),
            localDay.ToString("MM", CultureInfo.InvariantCulture), localDay.ToString("dd", CultureInfo.InvariantCulture), folder);
        var baseName = $"{r.Direction.ToLowerInvariant()}_cam{r.CameraId:00}_v{r.Id}";
        var relMp4 = Path.Combine(relDir, baseName + ".mp4");
        var relJson = Path.Combine(relDir, baseName + ".json");
        var fullDir = Path.Combine(options.MediaRoot, relDir);
        var fullMp4 = Path.Combine(options.MediaRoot, relMp4);
        Directory.CreateDirectory(fullDir);
        if (File.Exists(fullMp4))
            return await FailAsync(r, info, $"REFUSED_OVERWRITE: {relMp4} already exists", ct);

        var list = Path.Combine(fullDir, baseName + ".concat.txt");
        await File.WriteAllLinesAsync(list, selected.Select(s => "file '" + s.Path.Replace("\\", "/").Replace("'", "'\\''") + "'"), ct);
        try
        {
            var (code, err, _) = await Ffmpeg.RunAsync(options.FfmpegPath,
                ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list,
                 "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart", "-n", fullMp4],
                TimeSpan.FromMinutes(2), ct);
            if (code != 0 || !File.Exists(fullMp4))
                return await FailAsync(r, info, "JOIN_FAILED: " + Last(err), ct);
        }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return await FailAsync(r, info, "FFMPEG: " + e.Message, ct);
        }
        finally
        {
            try { File.Delete(list); } catch (IOException) { }
        }

        byte[] sha;
        long size;
        await using (var fs = new FileStream(fullMp4, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sha = await SHA256.HashDataAsync(fs, ct);
            size = fs.Length;
        }
        var mediaStart = selected[0].StartUtc;
        var mediaEnd = selected[^1].EndUtc;
        var durationMs = await ProbeDurationMsAsync(fullMp4, ct) ?? (int)(mediaEnd - mediaStart).TotalMilliseconds;
        var metadata = new
        {
            format = "TGVIDEO1",
            recording_id = r.Id, trip_id = info.TripId, trip_number = info.TripNumber,
            receipt_id = info.TicketId ?? info.ScannedTicketId, truck_id = info.TruckId, fleet_number = info.FleetNumber,
            plate_on_record = info.Plate, camera_id = r.CameraId, camera_label = r.CameraLabel,
            camera_config_version = r.CameraConfigVersion, gate = info.GateCode, gate_operation = r.Direction,
            scan_id = r.GateScanId, scan_timestamp = r.ScanAt, window_start = r.WindowStart, window_end = r.WindowEnd,
            recording_start = mediaStart, recording_end = mediaEnd, duration_ms = durationMs, segments = selected.Count,
            camera_health = new { status = info.RecorderStatus, last_frame_at = info.LastFrameAt },
            recording_integrity = new { status, sha256 = Convert.ToHexStringLower(sha), size_bytes = size },
            clock = "server UTC (segments timed by the server that recorded them)",
        };
        await using (var js = new FileStream(Path.Combine(options.MediaRoot, relJson), FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(js, metadata, new JsonSerializerOptions { WriteIndented = true }, ct);

        await db.InTransactionAsync(VideoActor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                UPDATE tg.video_recordings SET status = @status, media_start = @mediaStart, media_end = @mediaEnd,
                       duration_ms = @durationMs, segments = @segments, path = @path, metadata_path = @json, sha256 = @sha,
                       size_bytes = @size, finalized_at = now(),
                       error = CASE WHEN @status = 'PARTIAL' THEN 'PARTIAL: part of the window is missing' END
                WHERE id = @id AND status = 'RECORDING'
                """, new
            {
                id = r.Id, status, mediaStart, mediaEnd, durationMs, segments = selected.Count,
                path = relMp4.Replace('\\', '/'), json = relJson.Replace('\\', '/'), sha, size,
            }, tx);
            if (status == "PARTIAL")
                await AlertRecordingAsync(conn, tx, r, info, "PARTIAL: part of the window is missing");
            return 0;
        }, ct);
        return new RecordingResult(r.Id, status, durationMs, status == "PARTIAL" ? "PARTIAL" : null);
    }

    private async Task<RecordingResult> FailAsync(PendingRecording r, ScanInfo info, string error, CancellationToken ct)
    {
        await db.InTransactionAsync(VideoActor, async (conn, tx) =>
        {
            var n = await conn.ExecuteAsync("""
                UPDATE tg.video_recordings SET status = 'FAILED', error = @error, finalized_at = now()
                WHERE id = @id AND status = 'RECORDING'
                """, new { id = r.Id, error }, tx);
            if (n == 0) return 0;
            await AlertRecordingAsync(conn, tx, r, info, error);
            // missing footage of a trip is a review reason when the supervisor wants it
            var s = await SettingsBag.LoadAsync(conn, tx);
            if (info.TripId is { } tripId && s.Bool("video.failure_opens_review", true))
            {
                var reason = r.Direction == "EXIT" ? "VIDEO_MISSING_EXIT" : "VIDEO_MISSING_ENTRY";
                var open = await conn.ExecuteScalarAsync<bool>("""
                    SELECT EXISTS (SELECT 1 FROM tg.reviews WHERE trip_id = @tripId AND decision IS NULL AND @reason = ANY(reason_codes))
                    """, new { tripId, reason }, tx);
                if (!open) await Trips.OpenReviewAsync(conn, tx, tripId, [reason]);
            }
            return 0;
        }, ct);
        return new RecordingResult(r.Id, "FAILED", null, error);
    }

    private static Task AlertRecordingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PendingRecording r, ScanInfo info,
        string error) =>
        GateService.InsertAlertAsync(conn, tx, "RECORDING_FAILED", "MEDIUM", "video_recordings",
            r.Id.ToString(CultureInfo.InvariantCulture),
            new { recording_id = r.Id, camera_id = r.CameraId, label = r.CameraLabel, trip_number = info.TripNumber, error });

    private async Task<int?> ProbeDurationMsAsync(string file, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.FfprobePath)) return null;
        try
        {
            var (code, _, output) = await Ffmpeg.RunAsync(options.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", file],
                TimeSpan.FromSeconds(30), ct);
            return code == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)
                ? (int)Math.Round(sec * 1000) : null;
        }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The recorded file, after checking it against the SHA-256 kept at recording time (cached for a few
    /// minutes while the file is unchanged, since a player asks for many ranges). A modified or missing
    /// file raises a critical alert and is refused.
    /// </summary>
    public async Task<string> VerifiedPathAsync(long id, CancellationToken ct = default)
    {
        var row = await db.ReadAsync(conn => conn.QuerySingleOrDefaultAsync<FileRow>(
            "SELECT path, sha256, status FROM tg.video_recordings WHERE id = @id", new { id }), ct)
            ?? throw TruckGateException.NotFound("VIDEO_NOT_FOUND", $"Recording {id} does not exist.");
        if (row.Status == "PURGED") throw TruckGateException.NotFound("VIDEO_PURGED", "This video was removed by the retention rule.");
        if (row.Path is null || row.Sha256 is null) throw TruckGateException.NotFound("VIDEO_NOT_READY", "No video for this recording.");
        var root = Path.GetFullPath(options.MediaRoot);
        var full = Path.GetFullPath(Path.Combine(root, row.Path));
        if (!full.StartsWith(root, StringComparison.Ordinal)) throw TruckGateException.Conflict("VIDEO_PATH", "Invalid video path.");
        var fi = new FileInfo(full);
        if (fi.Exists && _verified.TryGetValue(id, out var v) && v.Size == fi.Length && v.Write == fi.LastWriteTimeUtc
            && DateTime.UtcNow - v.CheckedAt < TimeSpan.FromMinutes(10))
            return full;
        bool ok = false;
        if (fi.Exists)
        {
            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            ok = (await SHA256.HashDataAsync(fs, ct)).AsSpan().SequenceEqual(row.Sha256);
        }
        if (!ok)
        {
            _verified.TryRemove(id, out _);
            await db.InTransactionAsync(VideoActor, async (conn, tx) =>
            {
                await GateService.InsertAlertAsync(conn, tx, fi.Exists ? "VIDEO_TAMPERED" : "VIDEO_MISSING", "CRITICAL",
                    "video_recordings", id.ToString(CultureInfo.InvariantCulture), new { path = row.Path });
                return 0;
            }, ct);
            throw TruckGateException.Conflict(fi.Exists ? "VIDEO_TAMPERED" : "VIDEO_MISSING",
                "The stored video does not match its recorded fingerprint.");
        }
        _verified[id] = (fi.Length, fi.LastWriteTimeUtc, DateTime.UtcNow);
        return full;
    }

    /// <summary>
    /// Retention: videos older than video.retention_days are deleted from disk and their rows become PURGED
    /// (fingerprint and description stay). Videos of trips under review, labelled for the dataset or never
    /// closed are kept when video.keep_reviewed is on.
    /// </summary>
    public async Task<int> PurgeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var candidates = await db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            var days = s.Int("video.retention_days", 30);
            var keep = s.Bool("video.keep_reviewed", true);
            return (await conn.QueryAsync<(long Id, string Path)>("""
                SELECT v.id, v.path FROM tg.video_recordings v
                LEFT JOIN tg.trips tr ON tr.entry_scan_id = v.gate_scan_id OR tr.exit_scan_id = v.gate_scan_id
                WHERE v.status IN ('OK', 'PARTIAL') AND v.scan_at < @cutoff
                  AND (NOT @keep OR tr.id IS NULL OR (
                       NOT EXISTS (SELECT 1 FROM tg.reviews r WHERE r.trip_id = tr.id AND r.decision IS NULL)
                   AND NOT EXISTS (SELECT 1 FROM tg.trip_annotations a WHERE a.trip_id = tr.id)
                   AND tr.current_state IN ('CLOSED', 'PAID', 'REJECTED')))
                ORDER BY v.id LIMIT 500
                """, new { cutoff = nowUtc.AddDays(-days), keep })).ToList();
        }, ct);
        var root = Path.GetFullPath(options.MediaRoot);
        int purged = 0;
        foreach (var (id, rel) in candidates)
        {
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
            try { if (File.Exists(full)) File.Delete(full); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            await db.InTransactionAsync(VideoActor, (conn, tx) => conn.ExecuteAsync(
                "UPDATE tg.video_recordings SET status = 'PURGED', purged_at = now() WHERE id = @id AND status IN ('OK', 'PARTIAL')",
                new { id }, tx), ct);
            _verified.TryRemove(id, out _);
            purged++;
        }
        return purged;
    }

    /// <summary>Free space where the videos are written; STORAGE_LOW once a day when under storage.min_free_gb.</summary>
    public async Task<StorageStatus> StorageAsync(bool alert, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.MediaRoot);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.MediaRoot)) ?? "/");
        var (minGb, bytes, count) = await db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            var totals = await conn.QuerySingleAsync<(long Bytes, int Count)>("""
                SELECT coalesce(sum(size_bytes), 0)::bigint, count(*)::int FROM tg.video_recordings WHERE status IN ('OK', 'PARTIAL')
                """);
            return (s.Int("storage.min_free_gb", 20), totals.Bytes, totals.Count);
        }, ct);
        var low = drive.AvailableFreeSpace < (long)minGb * 1024 * 1024 * 1024;
        if (low && alert)
            await db.InTransactionAsync(VideoActor, async (conn, tx) =>
            {
                await Watchdog.AlertOnceAsync(conn, tx, "STORAGE_LOW", "CRITICAL", "storage", drive.Name,
                    DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    new { label = drive.Name, free_gb = drive.AvailableFreeSpace / (1024 * 1024 * 1024) });
                return 0;
            }, ct);
        return new StorageStatus(options.MediaRoot, drive.AvailableFreeSpace, drive.TotalSize, bytes, count, minGb, low);
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id ?? "Africa/Algiers"); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static string Last(string err)
    {
        var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "ffmpeg failed" : lines[^1];
    }

    private sealed class ScanInfo
    {
        public long ScanId { get; set; }
        public string Direction { get; set; } = "";
        public DateTime ScannedAt { get; set; }
        public string GateCode { get; set; } = "";
        public long? TripId { get; set; }
        public string? TripNumber { get; set; }
        public int? TruckId { get; set; }
        public string? TicketId { get; set; }
        public string? ScannedTicketId { get; set; }
        public string? FleetNumber { get; set; }
        public string? Plate { get; set; }
        public string? TimeZone { get; set; }
        public string? RecorderStatus { get; set; }
        public DateTime? LastFrameAt { get; set; }
    }

    private sealed class FileRow
    {
        public string? Path { get; set; }
        public byte[]? Sha256 { get; set; }
        public string Status { get; set; } = "";
    }
}

/// <summary>A recording camera as the recorder needs it (password decrypted in memory only).</summary>
public sealed record RecordingCamera(int Id, string Label, int ConfigVersion, string StreamUrl, string? Username, string? Password);

/// <summary>
/// Keeps one ffmpeg process per recording camera writing short segments into the camera's buffer
/// directory (stream copied, never re-encoded). Restarts it with a back-off when the camera drops.
/// </summary>
public sealed class CameraRecorder(RecordingCamera camera, string bufferDir, string ffmpegPath, int segmentSeconds) : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private DateTime _startedAt;
    private DateTime _nextStart = DateTime.MinValue;
    private int _failures;

    public RecordingCamera Camera => camera;
    public int SegmentSeconds => segmentSeconds;
    public long? ActiveRun { get; private set; }
    public DateTime StartedAt => _startedAt;
    public bool Running { get { lock (_gate) return _process is { HasExited: false }; } }

    public string? LastError
    {
        get { lock (_gate) return _stderr.Count == 0 ? null : _stderr.Last(); }
    }

    /// <summary>
    /// The address given to ffmpeg, with the user and password inserted (never logged). ffmpeg reads them
    /// as typed (it takes the last '@' before the path and splits user and password at the first ':'), so
    /// they are not percent-encoded, except '/', '?' and '#' which would end the host part.
    /// </summary>
    public static string InputUrl(string streamUrl, string? username, string? password)
    {
        if (string.IsNullOrEmpty(username)) return streamUrl;
        var i = streamUrl.IndexOf("://", StringComparison.Ordinal);
        if (i < 0) return streamUrl;
        static string Safe(string v) => v.Replace("/", "%2F").Replace("?", "%3F").Replace("#", "%23");
        return streamUrl[..(i + 3)] + Safe(username) + ":" + Safe(password ?? "") + "@" + streamUrl[(i + 3)..];
    }

    public static IReadOnlyList<string> Arguments(string inputUrl, string outputPattern, int segmentSeconds)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (inputUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["-rtsp_transport", "tcp", "-timeout", "10000000"]);
        args.AddRange(["-i", inputUrl, "-map", "0:v:0", "-an", "-c", "copy", "-f", "segment",
            "-segment_time", segmentSeconds.ToString(CultureInfo.InvariantCulture), "-segment_format", "mpegts",
            "-reset_timestamps", "1", outputPattern]);
        return args;
    }

    /// <summary>Starts ffmpeg if it is not running and the back-off allows it.</summary>
    public void EnsureRunning(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            if (_process is not null)
            {
                // ran less than a minute: count as a failure and wait longer next time
                _failures = nowUtc - _startedAt < TimeSpan.FromMinutes(1) ? _failures + 1 : 0;
                _nextStart = nowUtc + TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(_failures, 4))));
                _process.Dispose();
                _process = null;
                ActiveRun = null;
            }
            if (nowUtc < _nextStart) return;
            Directory.CreateDirectory(bufferDir);
            var run = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pattern = Path.Combine(bufferDir, $"r{run}_%08d.ts");
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in Arguments(InputUrl(camera.StreamUrl, camera.Username, camera.Password), pattern, segmentSeconds))
                psi.ArgumentList.Add(a);
            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (_gate)
                    {
                        _stderr.Enqueue(Ffmpeg.Redact(e.Data.Trim()));
                        while (_stderr.Count > 20) _stderr.Dequeue();
                    }
                };
                p.Start();
                p.BeginErrorReadLine();
                _process = p;
                _startedAt = nowUtc;
                ActiveRun = run;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _stderr.Enqueue("cannot start ffmpeg: " + e.Message);
                _failures++;
                _nextStart = nowUtc + TimeSpan.FromSeconds(60);
            }
        }
    }

    public IReadOnlyList<Segment> Segments() => SegmentIndex.Scan(bufferDir, segmentSeconds, ActiveRun);

    /// <summary>Deletes buffer files that ended before <paramref name="keepAfterUtc"/>.</summary>
    public int Prune(DateTime keepAfterUtc)
    {
        int n = 0;
        foreach (var s in Segments().Where(s => s.Complete && s.EndUtc < keepAfterUtc))
        {
            try { File.Delete(s.Path); n++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            _process?.Dispose();
            _process = null;
            ActiveRun = null;
        }
    }
}
