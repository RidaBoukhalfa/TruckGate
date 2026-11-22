using Dapper;
using Npgsql;
using TruckGate.Data;

namespace TruckGate.Application.Alerts;

public sealed record AlertItem(long Id, string Type, string Severity, string? Entity, string? EntityId, string Params,
    DateTime CreatedAt, DateTime? SmsSentAt, DateTime? TelegramSentAt, DateTime? WhatsappSentAt, string? AckByName, DateTime? AckAt,
    int FailedDeliveries);

public sealed record ChannelStatus(bool SmsEnabled, string SmsTransport, string SmsPort, string SmsRouterUrl, bool SmsRouterPasswordSet,
    IReadOnlyList<string> SmsRecipients, string SmsMinSeverity,
    bool TelegramEnabled, bool TelegramTokenSet, IReadOnlyList<string> TelegramChatIds, string TelegramMinSeverity,
    bool WhatsappEnabled, bool WhatsappTokenSet, string WhatsappPhoneNumberId, IReadOnlyList<string> WhatsappRecipients,
    string WhatsappTemplate, string WhatsappMinSeverity, string Language);

public sealed record DispatchReport(int Sent, int Failed, int Skipped);

/// <summary>
/// Alerts: list and acknowledge on screen; deliver by SMS (GSM modem or the 4G/5G router, no internet
/// needed), by WhatsApp (official Cloud API) and by Telegram. Every delivery attempt is logged; an alert
/// is retried up to 5 times per channel.
/// </summary>
public sealed class AlertService(Db db, SecretBox secrets, ITelegramSender telegram, ISmsSender sms, IWhatsAppSender whatsapp,
    StorageOptions storage)
{
    private static readonly Actor DispatchActor = Actor.System("alerts");
    public const string TelegramTokenKey = "telegram.bot_token";
    public const string WhatsAppTokenKey = "whatsapp.access_token";
    public const string RouterPasswordKey = "sms.router_password";
    public static readonly string[] SecretKeys = [TelegramTokenKey, WhatsAppTokenKey, RouterPasswordKey];
    private const int MaxAttempts = 5;

    public Task<IReadOnlyList<AlertItem>> ListAsync(bool openOnly, int limit, CancellationToken ct = default) =>
        db.ReadAsync<IReadOnlyList<AlertItem>>(async conn => (await conn.QueryAsync<AlertItem>("""
            SELECT a.id, a.type, a.severity, a.entity, a.entity_id AS entityid, a.params::text AS params, a.created_at AS createdat,
                   a.sms_sent_at AS smssentat, a.telegram_sent_at AS telegramsentat, a.whatsapp_sent_at AS whatsappsentat,
                   u.full_name AS ackbyname, a.ack_at AS ackat,
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
            var set = (await conn.QueryAsync<string>("SELECT key FROM tg.channel_secrets")).ToHashSet(StringComparer.Ordinal);
            return new ChannelStatus(s.Bool("alerts.sms_enabled", false), s.String("alerts.sms_transport", SmsConfig.Modem),
                s.String("alerts.sms_port", ""), s.String("alerts.sms_router_url", ""), set.Contains(RouterPasswordKey),
                s.Strings("alerts.sms_recipients"), s.String("alerts.sms_min_severity", "CRITICAL"),
                s.Bool("alerts.telegram_enabled", false), set.Contains(TelegramTokenKey), s.Strings("alerts.telegram_chat_ids"),
                s.String("alerts.telegram_min_severity", "MEDIUM"),
                s.Bool("alerts.whatsapp_enabled", false), set.Contains(WhatsAppTokenKey), s.String("alerts.whatsapp_phone_number_id", ""),
                s.Strings("alerts.whatsapp_recipients"), s.String("alerts.whatsapp_template", "truckgate_alert"),
                s.String("alerts.whatsapp_min_severity", "MEDIUM"), s.String("alerts.language", "ar"));
        }, ct);

    public Task SetTelegramTokenAsync(string token, Actor actor, CancellationToken ct = default) =>
        SetSecretAsync(TelegramTokenKey, token, actor, ct);

    /// <summary>Stores a channel secret encrypted (key file outside the database); never copied to the audit log.</summary>
    public Task SetSecretAsync(string key, string value, Actor actor, CancellationToken ct = default)
    {
        var v = (value ?? "").Trim();
        var ok = key switch
        {
            TelegramTokenKey => v.Length is >= 20 and <= 100 && v.Contains(':'),
            WhatsAppTokenKey => v.Length is >= 20 and <= 1000 && !v.Any(char.IsWhiteSpace),
            RouterPasswordKey => v.Length is >= 1 and <= 128,
            _ => false,
        };
        if (!ok)
            throw TruckGateException.Validation("TOKEN_INVALID", key == TelegramTokenKey
                ? "This does not look like a Telegram bot token (123456:ABC...)."
                : "Invalid value.");
        return db.InTransactionAsync(actor, async (conn, tx) =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO tg.channel_secrets (key, value_enc) VALUES (@key, @v)
                ON CONFLICT (key) DO UPDATE SET value_enc = @v, updated_at = now()
                """, new { key, v = secrets.Encrypt(v) }, tx);
            return 0;
        }, ct);
    }

    /// <summary>Sends a test message on one channel to every recipient; returns the errors (empty = all sent).</summary>
    public async Task<IReadOnlyList<string>> TestAsync(string channel, CancellationToken ct = default)
    {
        var (s, keys) = await LoadConfigAsync(ct);
        var lang = s.String("alerts.language", "ar");
        var text = AlertText.Format("TEST", "INFO", "{}", DateTime.UtcNow, lang, Zone(s));
        var errors = new List<string>();
        switch (channel)
        {
            case "SMS":
                var sc = SmsConfigFrom(s, keys);
                var to = s.Strings("alerts.sms_recipients");
                if (!sc.IsComplete || to.Count == 0) errors.Add("SMS: transport or recipients not set");
                else
                    foreach (var n in to)
                        if (await TryAsync(() => sms.SendAsync(sc, n, text, ct)) is { } e) errors.Add($"{n}: {e}");
                break;
            case "TELEGRAM":
                var chats = s.Strings("alerts.telegram_chat_ids");
                if (keys.GetValueOrDefault(TelegramTokenKey) is not { } token || chats.Count == 0)
                    errors.Add("Telegram: token or chat ids not set");
                else
                    foreach (var c in chats)
                        if (await TryAsync(() => telegram.SendTextAsync(token, c, text, ct)) is { } e) errors.Add($"{c}: {e}");
                break;
            case "WHATSAPP":
                var wa = WhatsAppConfigFrom(s, keys);
                var recipients = s.Strings("alerts.whatsapp_recipients");
                if (wa is null || recipients.Count == 0) errors.Add("WhatsApp: token, phone number id or recipients not set");
                else
                    foreach (var r in recipients)
                        if (await TryAsync(() => whatsapp.SendTemplateAsync(wa, r, text, null, ct)) is { } e) errors.Add($"{r}: {e}");
                break;
            default:
                throw TruckGateException.Validation("CHANNEL_INVALID", "Channel must be SMS, TELEGRAM or WHATSAPP.");
        }
        return errors;
    }

    /// <summary>Delivers pending alerts of the last 24 hours. Called every few seconds by the server.</summary>
    public async Task<DispatchReport> DispatchOnceAsync(CancellationToken ct = default)
    {
        var (s, keys) = await LoadConfigAsync(ct);
        var smsConfig = SmsConfigFrom(s, keys);
        var waConfig = WhatsAppConfigFrom(s, keys);
        var tgToken = keys.GetValueOrDefault(TelegramTokenKey);
        bool smsOn = s.Bool("alerts.sms_enabled", false) && smsConfig.IsComplete && s.Strings("alerts.sms_recipients").Count > 0;
        bool tgOn = s.Bool("alerts.telegram_enabled", false) && tgToken is not null && s.Strings("alerts.telegram_chat_ids").Count > 0;
        bool waOn = s.Bool("alerts.whatsapp_enabled", false) && waConfig is not null && s.Strings("alerts.whatsapp_recipients").Count > 0;
        if (!smsOn && !tgOn && !waOn) return new DispatchReport(0, 0, 0);

        var lang = s.String("alerts.language", "ar");
        var zone = Zone(s);
        int smsRank = AlertText.Rank(s.String("alerts.sms_min_severity", "CRITICAL"));
        int tgRank = AlertText.Rank(s.String("alerts.telegram_min_severity", "MEDIUM"));
        int waRank = AlertText.Rank(s.String("alerts.whatsapp_min_severity", "MEDIUM"));

        var pending = await db.ReadAsync(async conn => (await conn.QueryAsync<PendingRow>("""
            SELECT a.id, a.type, a.severity, a.entity, a.entity_id, a.params::text AS params, a.created_at,
                   a.sms_sent_at IS NULL AS sms_pending, a.telegram_sent_at IS NULL AS telegram_pending,
                   a.whatsapp_sent_at IS NULL AS whatsapp_pending,
                   (SELECT count(*)::int FROM tg.alert_deliveries d WHERE d.alert_id = a.id AND d.channel = 'SMS' AND NOT d.ok) AS sms_failures,
                   (SELECT count(*)::int FROM tg.alert_deliveries d WHERE d.alert_id = a.id AND d.channel = 'TELEGRAM' AND NOT d.ok) AS telegram_failures,
                   (SELECT count(*)::int FROM tg.alert_deliveries d WHERE d.alert_id = a.id AND d.channel = 'WHATSAPP' AND NOT d.ok) AS whatsapp_failures
            FROM tg.alerts a
            WHERE a.created_at > now() - interval '24 hours'
              AND (a.sms_sent_at IS NULL OR a.telegram_sent_at IS NULL OR a.whatsapp_sent_at IS NULL)
            ORDER BY a.id LIMIT 50
            """)).ToList(), ct);

        int sent = 0, failed = 0, skipped = 0;
        int smsBudget = smsOn ? await SmsBudgetAsync(s.Int("alerts.sms_max_per_hour", 10), ct) : 0;

        async Task DeliverAsync(PendingRow a, string channel, string column, IReadOnlyList<string> recipients, Func<string, Task> send)
        {
            var allOk = true;
            var done = await DeliveredToAsync(a.Id, channel, ct);
            foreach (var to in recipients.Where(r => !done.Contains(r)))
            {
                var error = await TryAsync(() => send(to));
                await LogAsync(a.Id, channel, to, error, ct);
                if (error is null) sent++; else { failed++; allOk = false; }
            }
            if (allOk) await MarkAsync(a.Id, column, ct);
        }

        foreach (var a in pending)
        {
            var text = AlertText.Format(a.Type, a.Severity, a.Params, a.CreatedAt, lang, zone);
            int rank = AlertText.Rank(a.Severity);
            if (smsOn && a.SmsPending && rank >= smsRank && a.SmsFailures < MaxAttempts)
            {
                var recipients = s.Strings("alerts.sms_recipients");
                if (smsBudget < recipients.Count) skipped++;
                else
                {
                    smsBudget -= recipients.Count;
                    await DeliverAsync(a, "SMS", "sms_sent_at", recipients, to => sms.SendAsync(smsConfig, to, text, ct));
                }
            }
            if (tgOn && a.TelegramPending && rank >= tgRank && a.TelegramFailures < MaxAttempts)
            {
                var photo = s.Bool("alerts.telegram_photos", true) ? await PhotoForAsync(a, ct) : null;
                await DeliverAsync(a, "TELEGRAM", "telegram_sent_at", s.Strings("alerts.telegram_chat_ids"), chat => photo is null
                    ? telegram.SendTextAsync(tgToken!, chat, text, ct)
                    : telegram.SendPhotoAsync(tgToken!, chat, photo, text, ct));
            }
            if (waOn && a.WhatsappPending && rank >= waRank && a.WhatsappFailures < MaxAttempts)
            {
                var photo = s.Bool("alerts.whatsapp_photos", false) ? await PhotoForAsync(a, ct) : null;
                await DeliverAsync(a, "WHATSAPP", "whatsapp_sent_at", s.Strings("alerts.whatsapp_recipients"),
                    to => whatsapp.SendTemplateAsync(waConfig!, to, text, photo, ct));
            }
        }
        return new DispatchReport(sent, failed, skipped);
    }

    private static SmsConfig SmsConfigFrom(SettingsBag s, IReadOnlyDictionary<string, string> keys) =>
        new(s.String("alerts.sms_transport", SmsConfig.Modem), s.String("alerts.sms_port", ""),
            s.String("alerts.sms_router_url", ""), s.String("alerts.sms_router_user", ""), keys.GetValueOrDefault(RouterPasswordKey));

    private static WhatsAppConfig? WhatsAppConfigFrom(SettingsBag s, IReadOnlyDictionary<string, string> keys)
    {
        var phoneId = s.String("alerts.whatsapp_phone_number_id", "");
        return keys.GetValueOrDefault(WhatsAppTokenKey) is { } token && phoneId.Length > 0
            ? new WhatsAppConfig(token, s.String("alerts.whatsapp_api_version", "v26.0"), phoneId,
                s.String("alerts.whatsapp_template", "truckgate_alert"), s.String("alerts.whatsapp_template_language", "ar"))
            : null;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(SettingsBag Settings, IReadOnlyDictionary<string, string> Secrets)> LoadConfigAsync(CancellationToken ct) =>
        await db.ReadAsync(async conn =>
        {
            var s = await SettingsBag.LoadAsync(conn);
            var rows = await conn.QueryAsync<(string Key, byte[] Value)>("SELECT key, value_enc FROM tg.channel_secrets");
            IReadOnlyDictionary<string, string> keys = rows.ToDictionary(r => r.Key, r => secrets.Decrypt(r.Value), StringComparer.Ordinal);
            return (s, keys);
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
        public bool WhatsappPending { get; set; }
        public int SmsFailures { get; set; }
        public int TelegramFailures { get; set; }
        public int WhatsappFailures { get; set; }
    }
}
