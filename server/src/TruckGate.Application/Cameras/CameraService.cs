using System.Security.Cryptography;
using Dapper;
using TruckGate.Data;

namespace TruckGate.Application.Cameras;

public sealed record CameraInfo(
    int Id, string Label, int? GateId, string Role, bool CaptureOnScan, int Frames, int FrameIntervalMs, string Source,
    string? OnvifUrl, string? ProfileToken, string SnapshotUrl, string? Username, bool HasPassword, string? Manufacturer,
    string? Model, int ConfigVersion, bool Active, DateTime? LastOkAt, string? LastError, DateTime? LastErrorAt);

public sealed record CameraInput(
    string Label, int? GateId, string Role, bool CaptureOnScan, int Frames, int FrameIntervalMs, string Source,
    string? OnvifUrl, string? ProfileToken, string SnapshotUrl, string? Username, string? Password,
    string? Manufacturer, string? Model, string? Serial);

/// <summary>Null fields are left unchanged. An empty password removes it.</summary>
public sealed record CameraUpdate(
    string? Label, int? GateId, bool ClearGate, string? Role, bool? CaptureOnScan, int? Frames, int? FrameIntervalMs,
    string? SnapshotUrl, string? Username, string? Password, bool? Active);

public sealed record CapturedFrame(int CameraId, string Role, int ConfigVersion, int FrameIndex, DateTime CapturedAt, byte[] Jpeg);

public sealed record SnapshotInfo(long Id, int CameraId, string CameraLabel, string Role, int FrameIndex, DateTime CapturedAt);

/// <summary>
/// Gate cameras: discovery and selection (admin), picture burst when a ticket is scanned,
/// storage with SHA-256, integrity check when a picture is served.
/// </summary>
public sealed class CameraService(Db db, SecretBox secrets, StorageOptions storage, Func<HttpMessageHandler>? handlerFactory = null)
{
    public static readonly string[] Roles = ["PLATE", "CARGO", "OVERVIEW"];
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(3);
    private static readonly Actor CaptureActor = Actor.System("cameras");

    // ------------------------------------------------------------------ discovery (admin)

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken ct = default) =>
        WsDiscovery.ProbeAsync(TimeSpan.FromSeconds(3), ct);

    public async Task<OnvifDeviceInfo> ProbeOnvifAsync(string deviceUrl, string? username, string? password,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(deviceUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw TruckGateException.Validation("CAMERA_URL", "The ONVIF address must be http(s)://...");
        using var http = SnapshotFetcher.CreateClient(username, password, TimeSpan.FromSeconds(6), handlerFactory);
        try
        {
            return await new OnvifClient(http, username, password).ProbeAsync(deviceUrl, ct);
        }
        catch (OnvifException e)
        {
            throw TruckGateException.Validation("ONVIF_FAILED", e.Message);
        }
    }

    /// <summary>One picture, not stored — for the admin preview.</summary>
    public async Task<byte[]> TestAsync(string snapshotUrl, string? username, string? password, CancellationToken ct = default)
    {
        ValidateUrl(snapshotUrl);
        using var http = SnapshotFetcher.CreateClient(username, password, FetchTimeout, handlerFactory);
        try
        {
            return await SnapshotFetcher.FetchAsync(http, snapshotUrl, ct);
        }
        catch (SnapshotException e)
        {
            throw TruckGateException.Validation("SNAPSHOT_FAILED", e.Message);
        }
    }

    public async Task<byte[]> TestCameraAsync(int id, CancellationToken ct = default)
    {
        var cam = await LoadAsync(id, ct) ?? throw TruckGateException.NotFound("CAMERA_NOT_FOUND", $"Camera {id} does not exist.");
        return await TestAsync(cam.SnapshotUrl, cam.Username, cam.Password, ct);
    }

    // ------------------------------------------------------------------ configuration (admin)

    public Task<IReadOnlyList<CameraInfo>> ListAsync(CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<CameraInfo>>(async conn => (await conn.QueryAsync<CameraInfo>("""
            SELECT c.id, c.label, c.gate_id::int AS gateid, c.role, c.capture_on_scan AS captureonscan, c.frames,
                   c.frame_interval_ms AS frameintervalms, c.source, c.onvif_url AS onvifurl,
                   c.profile_token AS profiletoken, c.snapshot_url AS snapshoturl, c.username,
                   c.password_enc IS NOT NULL AS haspassword, c.manufacturer, c.model,
                   c.config_version AS configversion, c.active, h.last_ok_at AS lastokat, h.last_error AS lasterror,
                   h.last_error_at AS lasterrorat
            FROM tg.cameras c LEFT JOIN tg.camera_health h ON h.camera_id = c.id
            ORDER BY c.id
            """)).ToList(), ct);

    public Task<int> CreateAsync(CameraInput c, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.Label)) throw TruckGateException.Validation("LABEL_REQUIRED", "Label is required.");
        if (!Roles.Contains(c.Role)) throw TruckGateException.Validation("CAMERA_ROLE", "Role must be PLATE, CARGO or OVERVIEW.");
        if (c.Source is not ("ONVIF" or "URL")) throw TruckGateException.Validation("CAMERA_SOURCE", "Source must be ONVIF or URL.");
        if (c.Source == "ONVIF" && (string.IsNullOrEmpty(c.OnvifUrl) || string.IsNullOrEmpty(c.ProfileToken)))
            throw TruckGateException.Validation("CAMERA_PROFILE", "ONVIF cameras need the device address and a profile.");
        ValidateUrl(c.SnapshotUrl);
        ValidateFrames(c.Frames, c.FrameIntervalMs);
        var enc = string.IsNullOrEmpty(c.Password) ? null : secrets.Encrypt(c.Password);
        return db.InTransactionAsync(actor, (conn, tx) => conn.ExecuteScalarAsync<int>("""
            INSERT INTO tg.cameras (label, gate_id, role, capture_on_scan, frames, frame_interval_ms, source, onvif_url,
                                    profile_token, snapshot_url, username, password_enc, manufacturer, model, serial)
            VALUES (@Label, @GateId, @Role, @CaptureOnScan, @Frames, @FrameIntervalMs, @Source, @OnvifUrl,
                    @ProfileToken, @SnapshotUrl, @Username, @enc, @Manufacturer, @Model, @Serial)
            RETURNING id
            """, new
        {
            Label = c.Label.Trim(), c.GateId, c.Role, c.CaptureOnScan, c.Frames, c.FrameIntervalMs, c.Source, c.OnvifUrl,
            c.ProfileToken, c.SnapshotUrl, c.Username, enc, c.Manufacturer, c.Model, c.Serial,
        }, tx), ct);
    }

    public Task UpdateAsync(int id, CameraUpdate u, Actor actor, CancellationToken ct = default)
    {
        if (u.Role is not null && !Roles.Contains(u.Role)) throw TruckGateException.Validation("CAMERA_ROLE", "Unknown role.");
        if (u.SnapshotUrl is not null) ValidateUrl(u.SnapshotUrl);
        if (u.Frames is not null || u.FrameIntervalMs is not null) ValidateFrames(u.Frames ?? 1, u.FrameIntervalMs ?? 0);
        byte[]? enc = u.Password is { Length: > 0 } p ? secrets.Encrypt(p) : null;
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var n = await conn.ExecuteAsync("""
                UPDATE tg.cameras SET
                    label = coalesce(@Label, label),
                    gate_id = CASE WHEN @ClearGate THEN NULL ELSE coalesce(@GateId::smallint, gate_id) END,
                    role = coalesce(@Role, role),
                    capture_on_scan = coalesce(@CaptureOnScan, capture_on_scan),
                    frames = coalesce(@Frames, frames),
                    frame_interval_ms = coalesce(@FrameIntervalMs, frame_interval_ms),
                    snapshot_url = coalesce(@SnapshotUrl, snapshot_url),
                    username = coalesce(@Username, username),
                    password_enc = CASE WHEN @Password IS NULL THEN password_enc WHEN @Password = '' THEN NULL ELSE @enc END,
                    active = coalesce(@Active, active)
                WHERE id = @id
                """, new
            {
                id, Label = u.Label?.Trim(), u.GateId, u.ClearGate, u.Role, u.CaptureOnScan, u.Frames, u.FrameIntervalMs,
                u.SnapshotUrl, u.Username, u.Password, enc, u.Active,
            }, tx);
            if (n == 0) throw TruckGateException.NotFound("CAMERA_NOT_FOUND", $"Camera {id} does not exist.");
            return 0;
        }, ct);
    }

    // ------------------------------------------------------------------ capture during a scan

    /// <summary>
    /// Takes the configured burst from every active "capture on scan" camera of the gate, in parallel.
    /// A failing camera never blocks the scan: it is recorded in camera health and raises an alert.
    /// </summary>
    public async Task<IReadOnlyList<CapturedFrame>> CaptureAsync(short gateId, CancellationToken ct = default)
    {
        var cameras = await db.ReadAsync(async conn => (await conn.QueryAsync<CameraRow>("""
            SELECT id, role, config_version, frames, frame_interval_ms, snapshot_url, username, password_enc
            FROM tg.cameras WHERE gate_id = @gateId AND active AND capture_on_scan ORDER BY id
            """, new { gateId = (int)gateId })).ToList(), ct);

        var results = await Task.WhenAll(cameras.Select(async cam =>
        {
            var frames = new List<CapturedFrame>();
            string? error = null;
            try
            {
                var password = cam.PasswordEnc is null ? null : secrets.Decrypt(cam.PasswordEnc);
                using var http = SnapshotFetcher.CreateClient(cam.Username, password, FetchTimeout, handlerFactory);
                for (int i = 0; i < cam.Frames; i++)
                {
                    if (i > 0 && cam.FrameIntervalMs > 0) await Task.Delay(cam.FrameIntervalMs, ct);
                    var jpeg = await SnapshotFetcher.FetchAsync(http, cam.SnapshotUrl, ct);
                    frames.Add(new CapturedFrame(cam.Id, cam.Role, cam.ConfigVersion, i, DateTime.UtcNow, jpeg));
                }
            }
            catch (Exception e) when (e is SnapshotException or CryptographicException)
            {
                error = e.Message;
            }
            await RecordHealthAsync(cam.Id, error, ct);
            return frames;
        }));
        return results.SelectMany(f => f).ToList();
    }

    /// <summary>Writes the pictures under the storage root and records them (path, SHA-256, size).</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> StoreAsync(long scanId, IReadOnlyList<CapturedFrame> frames,
        CancellationToken ct = default)
    {
        if (frames.Count == 0) return [];
        var day = DateTime.UtcNow;
        var relDir = Path.Combine(day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"), scanId.ToString());
        Directory.CreateDirectory(Path.Combine(storage.Root, relDir));
        var rows = new List<(CapturedFrame Frame, string Rel, byte[] Sha)>();
        foreach (var f in frames)
        {
            var rel = Path.Combine(relDir, $"{f.CameraId}_{f.FrameIndex}.jpg");
            await using (var fs = new FileStream(Path.Combine(storage.Root, rel), FileMode.CreateNew, FileAccess.Write))
                await fs.WriteAsync(f.Jpeg, ct);
            rows.Add((f, rel.Replace('\\', '/'), SHA256.HashData(f.Jpeg)));
        }

        await db.InTransactionAsync(CaptureActor, async (conn, tx) =>
        {
            foreach (var (f, rel, sha) in rows)
                await conn.ExecuteAsync("""
                    INSERT INTO tg.snapshots (gate_scan_id, camera_id, camera_role, camera_config_version, frame_index,
                                              captured_at, path, sha256, size_bytes)
                    VALUES (@scanId, @cam, @role, @ver, @idx, @at, @path, @sha, @size)
                    """, new
                {
                    scanId, cam = f.CameraId, role = f.Role, ver = f.ConfigVersion, idx = (short)f.FrameIndex,
                    at = f.CapturedAt, path = rel, sha, size = f.Jpeg.Length,
                }, tx);
            return 0;
        }, ct);
        return await ListForScanAsync(scanId, ct);
    }

    public Task<IReadOnlyList<SnapshotInfo>> ListForScanAsync(long scanId, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<SnapshotInfo>>(async conn => (await conn.QueryAsync<SnapshotInfo>("""
            SELECT s.id, s.camera_id AS cameraid, c.label AS cameralabel, s.camera_role AS role,
                   s.frame_index::int AS frameindex, s.captured_at AS capturedat
            FROM tg.snapshots s JOIN tg.cameras c ON c.id = s.camera_id
            WHERE s.gate_scan_id = @scanId ORDER BY s.camera_id, s.frame_index
            """, new { scanId })).ToList(), ct);

    /// <summary>
    /// Reads a stored picture and checks it against the SHA-256 recorded at capture time.
    /// A modified or missing file raises a critical alert and is refused.
    /// </summary>
    public async Task<byte[]> ReadSnapshotAsync(long id, CancellationToken ct = default)
    {
        var row = await db.ReadAsync(conn => conn.QuerySingleOrDefaultAsync<SnapshotRow>(
            "SELECT path, sha256 FROM tg.snapshots WHERE id = @id", new { id }), ct)
            ?? throw TruckGateException.NotFound("SNAPSHOT_NOT_FOUND", $"Picture {id} does not exist.");
        var root = Path.GetFullPath(storage.Root);
        var full = Path.GetFullPath(Path.Combine(root, row.Path));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw TruckGateException.Conflict("SNAPSHOT_PATH", "Invalid picture path.");
        byte[]? bytes = File.Exists(full) ? await File.ReadAllBytesAsync(full, ct) : null;
        if (bytes is null || !SHA256.HashData(bytes).AsSpan().SequenceEqual(row.Sha256))
        {
            await db.InTransactionAsync(CaptureActor, async (conn, tx) =>
            {
                await GateService.InsertAlertAsync(conn, tx, bytes is null ? "SNAPSHOT_MISSING" : "SNAPSHOT_TAMPERED",
                    "CRITICAL", "snapshots", id.ToString(), new { path = row.Path });
                return 0;
            }, ct);
            throw TruckGateException.Conflict(bytes is null ? "SNAPSHOT_MISSING" : "SNAPSHOT_TAMPERED",
                "The stored picture does not match its recorded fingerprint.");
        }
        return bytes;
    }

    // ------------------------------------------------------------------ helpers

    private async Task RecordHealthAsync(int cameraId, string? error, CancellationToken ct)
    {
        await db.InTransactionAsync(CaptureActor, async (conn, tx) =>
        {
            if (error is null)
            {
                await conn.ExecuteAsync("""
                    INSERT INTO tg.camera_health (camera_id, last_ok_at) VALUES (@cameraId, now())
                    ON CONFLICT (camera_id) DO UPDATE SET last_ok_at = now()
                    """, new { cameraId }, tx);
                return 0;
            }
            await conn.ExecuteAsync("""
                INSERT INTO tg.camera_health (camera_id, last_error, last_error_at) VALUES (@cameraId, @error, now())
                ON CONFLICT (camera_id) DO UPDATE SET last_error = @error, last_error_at = now()
                """, new { cameraId, error }, tx);
            // At most one alert per camera every 30 minutes.
            var recent = await conn.ExecuteScalarAsync<bool>("""
                SELECT EXISTS (SELECT 1 FROM tg.alerts WHERE type = 'CAMERA_FAILED' AND entity_id = @id
                               AND created_at > now() - interval '30 minutes')
                """, new { id = cameraId.ToString() }, tx);
            if (!recent)
                await GateService.InsertAlertAsync(conn, tx, "CAMERA_FAILED", "MEDIUM", "cameras", cameraId.ToString(),
                    new { error });
            return 0;
        }, ct);
    }

    private async Task<(string SnapshotUrl, string? Username, string? Password)?> LoadAsync(int id, CancellationToken ct)
    {
        var row = await db.ReadAsync(conn => conn.QuerySingleOrDefaultAsync<CameraRow>(
            "SELECT id, role, config_version, frames, frame_interval_ms, snapshot_url, username, password_enc FROM tg.cameras WHERE id = @id",
            new { id }), ct);
        if (row is null) return null;
        return (row.SnapshotUrl, row.Username, row.PasswordEnc is null ? null : secrets.Decrypt(row.PasswordEnc));
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw TruckGateException.Validation("SNAPSHOT_URL", "The picture address must be http(s)://...");
    }

    private static void ValidateFrames(int frames, int intervalMs)
    {
        if (frames is < 1 or > 10 || intervalMs is < 0 or > 2000)
            throw TruckGateException.Validation("CAMERA_FRAMES", "1–10 pictures, 0–2000 ms apart.");
    }

    private sealed class CameraRow
    {
        public int Id { get; set; }
        public string Role { get; set; } = "";
        public int ConfigVersion { get; set; }
        public int Frames { get; set; }
        public int FrameIntervalMs { get; set; }
        public string SnapshotUrl { get; set; } = "";
        public string? Username { get; set; }
        public byte[]? PasswordEnc { get; set; }
    }

    private sealed class SnapshotRow
    {
        public string Path { get; set; } = "";
        public byte[] Sha256 { get; set; } = [];
    }
}
