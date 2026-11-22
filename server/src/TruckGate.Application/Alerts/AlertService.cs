using Dapper;
using Npgsql;
using TruckGate.Data;

namespace TruckGate.Application.Alerts;

public sealed record AlertItem(long Id, string Type, string Severity, string? Entity, string? EntityId, string Params,
    DateTime CreatedAt, DateTime? SmsSentAt, DateTime? TelegramSentAt, string? AckByName, DateTime? AckAt, int FailedDeliveries);

public sealed record ChannelStatus(bool SmsEnabled, string SmsPort, IReadOnlyList<string> SmsRecipients, string SmsMinSeverity,
    bool TelegramEnabled, bool TelegramTokenSet, IReadOnlyList<string> TelegramChatIds, string TelegramMinSeverity, string Language);

public sealed record DispatchReport(int Sent, int Failed, int Skipped);

/// <summary>
/// Alerts: list and acknowledge on screen; deliver by SMS (GSM modem, no internet needed) and by
/// Telegram. Every delivery attempt is logged; an alert is retried up to 5 times per channel.
/// </summary>
public sealed class AlertService(Db db, SecretBox secrets, ITelegramSender telegram, ISmsSender sms, StorageOptions storage)
{
    private static readonly Actor DispatchActor = Actor.System("alerts");
    public const string TelegramTokenKey = "telegram.bot_token";
    private const int MaxAttempts = 5;

    public Task<IReadOnlyList<AlertItem>> ListAsync(bool openOnly, int limit, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<AlertItem>>(async conn => (await conn.QueryAsync<AlertItem>("""
            SELECT a.id, a.type, a.severity, a.entity, a.entity_id AS entityid, a.params::text AS params, a.created_at AS createdat,
                   a.sms_sent_at AS smssentat, a.telegram_sent_at AS telegramsentat, u.full_name AS ackbyname, a.ack_at AS ackat,
                   (SELECT count(*)::int FROM tg.alert_deliveries d WHERE d.alert_id = a.id AND NOT d.ok) AS faileddeliveries
            FROM tg.alerts a LEFT JOIN tg.users u ON u.id = a.ack_by
            WHERE (NOT @openOnly OR a.ack_at IS NULL)
            ORDER BY a.ack_at IS NULL DESC, CASE a.severity WHEN 'CRITICAL' THEN 0 WHEN 'MEDIUM' THEN 1 ELSE 2 END, a.id DESC
            LIMIT @limit
            """, new { openOnly, limit = Math.Clamp(limit, 1, 1000) })).ToList(), ct);

    public Task AcknowledgeAsync(long id, int userId, Actor actor, CancellationToken ct = default) =>
        db.InTransactionAsync(actor, async (conn, tx) =>
        {
            var n = await conn.ExecuteAsync("UPDATE tg.alerts SET ack_by = @userId, ack_at = now() WHERE id = @id AND ack_at IS NULL",
                new { id, userId }, tx);
            if (n == 0 && !await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.alerts WHERE id = @id)", new { id }, tx))
                throw TruckGateException.NotFound("ALERT_NOT_FOUND", $"Alert {id} does not exist.");
            return 0;
        }, ct);

    public Task<ChannelStatus> ChannelsAsync(CancellationToken ct = default) =>
        db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            var tokenSet = await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM tg.channel_secrets WHERE key = @k)",
                new { k = TelegramTokenKey });
            return new ChannelStatus(s.Bool("alerts.sms_enabled", false), s.String("alerts.sms_port", ""),
                s.Strings("alerts.sms_recipients"), s.String("alerts.sms_min_severity", "CRITICAL"),
                s.Bool("alerts.telegram_enabled", false), tokenSet, s.Strings("alerts.telegram_chat_ids"),
                s.String("alerts.telegram_min_severity", "MEDIUM"), s.String("alerts.language", "ar"));
        }, ct);

    public Task SetTelegramTokenAsync(string token, Actor actor, CancellationToken ct = default)
    {
        var t = (token ?? "").Trim();
        if (t.Length is < 20 or > 100 || !t.Contains(':'))
            throw TruckGateException.Validation("TOKEN_INVALID", "This does not look like a Telegram bot token (123456:ABC...).");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO tg.channel_secrets (key, value_enc) VALUES (@k, @v)
                ON CONFLICT (key) DO UPDATE SET value_enc = @v, updated_at = now()
                """, new { k = TelegramTokenKey, v = secrets.Encrypt(t) }, tx);
            return 0;
        }, ct);
    }

    /// <summary>Sends a test message on one channel to every recipient; returns the errors (empty = all sent).</summary>
    public async Task<IReadOnlyList<string>> TestAsync(string channel, CancellationToken ct = default)
    {
        var (s, token) = await LoadConfigAsync(ct);
        var lang = s.String("alerts.language", "ar");
        var text = AlertText.Format("TEST", "INFO", "{}", DateTime.UtcNow, lang, Zone(s));
        var errors = new List<string>();
        if (channel == "SMS")
        {
            var port = s.String("alerts.sms_port", "");
            var to = s.Strings("alerts.sms_recipients");
            if (port.Length == 0 || to.Count == 0) errors.Add("SMS: port or recipients not set");
            foreach (var n in to)
                try { await sms.SendAsync(port, n, text, ct); }
                catch (AlertChannelException e) { errors.Add($"{n}: {e.Message}"); }
        }
        else if (channel == "TELEGRAM")
        {
            var chats = s.Strings("alerts.telegram_chat_ids");
            if (token is null || chats.Count == 0) errors.Add("Telegram: token or chat ids not set");
            else
                foreach (var c in chats)
                    try { await telegram.SendTextAsync(token, c, text, ct); }
                    catch (Exception e) when (e is AlertChannelException or HttpRequestException or TaskCanceledException)
                    { errors.Add($"{c}: {e.Message}"); }
        }
        else throw TruckGateException.Validation("CHANNEL_INVALID", "Channel must be SMS or TELEGRAM.");
        return errors;
    }

    /// <summary>Delivers pending alerts of the last 24 hours. Called every few seconds by the server.</summary>
    public async Task<DispatchReport> DispatchOnceAsync(CancellationToken ct = default)
    {
        var (s, token) = await LoadConfigAsync(ct);
        bool smsOn = s.Bool("alerts.sms_enabled", false) && s.String("alerts.sms_port", "").Length > 0
                     && s.Strings("alerts.sms_recipients").Count > 0;
        bool tgOn = s.Bool("alerts.telegram_enabled", false) && token is not null && s.Strings("alerts.telegram_chat_ids").Count > 0;
        if (!smsOn && !tgOn) return new DispatchReport(0, 0, 0);

        var lang = s.String("alerts.language", "ar");
        var zone = Zone(s);
        int smsRank = AlertText.Rank(s.String("alerts.sms_min_severity", "CRITICAL"));
        int tgRank = AlertText.Rank(s.String("alerts.telegram_min_severity", "MEDIUM"));

        var pending = await db.ReadAsync(async conn => (await conn.QueryAsync<PendingRow>("""
            SELECT a.id, a.type, a.severity, a.entity, a.entity_id, a.params::text AS params, a.created_at,
                   a.sms_sent_at IS NULL AS sms_pending, a.telegram_sent_at IS NULL AS telegram_pending,
                   (SELECT count(*)::int FROM tg.alert_deliveries d WHERE d.alert_id = a.id AND d.channel = 'SMS' AND NOT d.ok) AS sms_failures,
                   (SELECT count(*)::int FROM tg.alert_deliveries d WHERE d.alert_id = a.id AND d.channel = 'TELEGRAM' AND NOT d.ok) AS telegram_failures
            FROM tg.alerts a
            WHERE a.created_at > now() - interval '24 hours' AND (a.sms_sent_at IS NULL OR a.telegram_sent_at IS NULL)
            ORDER BY a.id LIMIT 50
            """)).ToList(), ct);

        int sent = 0, failed = 0, skipped = 0;
        int smsBudget = smsOn ? await SmsBudgetAsync(s.Int("alerts.sms_max_per_hour", 10), ct) : 0;
        foreach (var a in pending)
        {
            var text = AlertText.Format(a.Type, a.Severity, a.Params, a.CreatedAt, lang, zone);
            if (smsOn && a.SmsPending && AlertText.Rank(a.Severity) >= smsRank && a.SmsFailures < MaxAttempts)
            {
                if (smsBudget <= 0) skipped++;
                else
                {
                    var allOk = true;
                    var done = await DeliveredToAsync(a.Id, "SMS", ct);
                    foreach (var number in s.Strings("alerts.sms_recipients").Where(n => !done.Contains(n)))
                    {
                        smsBudget--;
                        var error = await TryAsync(() => sms.SendAsync(s.String("alerts.sms_port", ""), number, text, ct));
                        await LogAsync(a.Id, "SMS", number, error, ct);
                        if (error is null) sent++; else { failed++; allOk = false; }
                    }
                    if (allOk) await MarkAsync(a.Id, "sms_sent_at", ct);
                }
            }
            if (tgOn && a.TelegramPending && AlertText.Rank(a.Severity) >= tgRank && a.TelegramFailures < MaxAttempts)
            {
                var photo = s.Bool("alerts.telegram_photos", true) ? await PhotoForAsync(a, ct) : null;
                var allOk = true;
                var done = await DeliveredToAsync(a.Id, "TELEGRAM", ct);
                foreach (var chat in s.Strings("alerts.telegram_chat_ids").Where(c => !done.Contains(c)))
                {
                    var error = await TryAsync(() => photo is null
                        ? telegram.SendTextAsync(token!, chat, text, ct)
                        : telegram.SendPhotoAsync(token!, chat, photo, text, ct));
                    await LogAsync(a.Id, "TELEGRAM", chat, error, ct);
                    if (error is null) sent++; else { failed++; allOk = false; }
                }
                if (allOk) await MarkAsync(a.Id, "telegram_sent_at", ct);
            }
        }
        return new DispatchReport(sent, failed, skipped);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(SettingsBag Settings, string? Token)> LoadConfigAsync(CancellationToken ct) =>
        await db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            var enc = await conn.ExecuteScalarAsync<byte[]?>("SELECT value_enc FROM tg.channel_secrets WHERE key = @k",
                new { k = TelegramTokenKey });
            return (s, enc is null ? null : secrets.Decrypt(enc));
        }, ct);

    private static TimeZoneInfo Zone(SettingsBag s)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(s.String("site.timezone", "Africa/Algiers")); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    private static async Task<string?> TryAsync(Func<Task> send)
    {
        try
        {
            await send();
            return null;
        }
        catch (Exception e) when (e is AlertChannelException or HttpRequestException or TaskCanceledException or IOException)
        {
            return e.Message.Length > 300 ? e.Message[..300] : e.Message;
        }
    }

    private Task<HashSet<string>> DeliveredToAsync(long alertId, string channel, CancellationToken ct) =>
        db.ReadAsync(async conn => (await conn.QueryAsync<string>(
            "SELECT recipient FROM tg.alert_deliveries WHERE alert_id = @alertId AND channel = @channel AND ok",
            new { alertId, channel })).ToHashSet(StringComparer.Ordinal), ct);

    private Task<int> SmsBudgetAsync(int maxPerHour, CancellationToken ct) =>
        db.ReadAsync(async conn => maxPerHour - await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)::int FROM tg.alert_deliveries WHERE channel = 'SMS' AND attempted_at > now() - interval '1 hour'
            """), ct);

    private Task LogAsync(long alertId, string channel, string recipient, string? error, CancellationToken ct) =>
        db.InTransactionAsync(DispatchActor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO tg.alert_deliveries (alert_id, channel, recipient, ok, error) VALUES (@alertId, @channel, @recipient, @ok, @error)
                """, new { alertId, channel, recipient, ok = error is null, error }, tx);
            return 0;
        }, ct);

    private Task MarkAsync(long alertId, string column, CancellationToken ct) =>
        db.InTransactionAsync(DispatchActor, async (conn, tx) =>
        {
            // column is one of two constants, never user input
            await conn.ExecuteAsync($"UPDATE tg.alerts SET {column} = now() WHERE id = @alertId AND {column} IS NULL", new { alertId }, tx);
            return 0;
        }, ct);

    /// <summary>First plate or cargo picture of the scan the alert is about, when the file is intact.</summary>
    private async Task<byte[]?> PhotoForAsync(PendingRow a, CancellationToken ct)
    {
        if (a.Entity is not ("gate_scans" or "trips") || !long.TryParse(a.EntityId, out var id)) return null;
        var row = await db.ReadAsync(conn => conn.QuerySingleOrDefaultAsync<(string Path, byte[] Sha)>("""
            SELECT s.path, s.sha256 FROM tg.snapshots s
            WHERE s.gate_scan_id = CASE WHEN @entity = 'gate_scans' THEN @id
                                        ELSE (SELECT coalesce(tr.exit_scan_id, tr.entry_scan_id) FROM tg.trips tr WHERE tr.id = @id) END
            ORDER BY s.camera_role = 'CARGO' DESC, s.camera_role = 'PLATE' DESC, s.frame_index LIMIT 1
            """, new { entity = a.Entity, id }), ct);
        if (row.Path is null) return null;
        var root = Path.GetFullPath(storage.Root);
        var full = Path.GetFullPath(Path.Combine(root, row.Path));
        if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) return null;
        var bytes = await File.ReadAllBytesAsync(full, ct);
        return System.Security.Cryptography.SHA256.HashData(bytes).AsSpan().SequenceEqual(row.Sha) ? bytes : null;
    }

    private sealed class PendingRow
    {
        public long Id { get; set; }
        public string Type { get; set; } = "";
        public string Severity { get; set; } = "";
        public string? Entity { get; set; }
        public string? EntityId { get; set; }
        public string Params { get; set; } = "{}";
        public DateTime CreatedAt { get; set; }
        public bool SmsPending { get; set; }
        public bool TelegramPending { get; set; }
        public int SmsFailures { get; set; }
        public int TelegramFailures { get; set; }
    }
}
