using System.Text;
using System.Text.Json;
using Dapper;
using TruckGate.Application;
using TruckGate.Application.Alerts;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

/// <summary>Alert delivery (Telegram, SMS through a GSM modem), acknowledgement and admin settings.</summary>
[Collection("db")]
public class AlertTests(DatabaseFixture fx)
{
    private static readonly Actor T = DatabaseFixture.Tester;

    private sealed class FakeTelegram : ITelegramSender
    {
        public List<(string Chat, string Text, bool Photo)> Sent { get; } = [];
        public bool Fail { get; set; }

        public Task SendTextAsync(string token, string chatId, string text, CancellationToken ct) => Record(chatId, text, false);

        public Task SendPhotoAsync(string token, string chatId, byte[] jpeg, string caption, CancellationToken ct) =>
            Record(chatId, caption, true);

        private Task Record(string chat, string text, bool photo)
        {
            if (Fail) throw new AlertChannelException("network down");
            Sent.Add((chat, text, photo));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSms : ISmsSender
    {
        public List<(string Number, string Text, SmsConfig Config)> Sent { get; } = [];

        public Task SendAsync(SmsConfig config, string number, string text, CancellationToken ct)
        {
            Sent.Add((number, text, config));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWhatsApp : IWhatsAppSender
    {
        public List<(string To, string Text, WhatsAppConfig Config)> Sent { get; } = [];

        public Task SendTemplateAsync(WhatsAppConfig config, string to, string text, byte[]? jpeg, CancellationToken ct)
        {
            Sent.Add((to, text, config));
            return Task.CompletedTask;
        }
    }

    private static Task Set(SettingsService s, string key, object value) =>
        s.SetAsync(key, JsonSerializer.SerializeToElement(value), T);

    [Fact]
    public async Task Critical_alert_goes_by_sms_and_telegram_once_and_medium_only_to_telegram()
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var tg = new FakeTelegram();
        var sms = new FakeSms();
        var wa = new FakeWhatsApp();
        var alerts = new AlertService(db, SecretBox.Ephemeral(), tg, sms, wa, new StorageOptions(Path.GetTempPath()));
        var settings = new SettingsService(db);
        await Set(settings, "alerts.sms_enabled", true);
        await Set(settings, "alerts.sms_port", "COM3");
        await Set(settings, "alerts.sms_recipients", new[] { "+213550000001" });
        await Set(settings, "alerts.telegram_enabled", true);
        await Set(settings, "alerts.telegram_chat_ids", new[] { "-100123", "456" });
        await alerts.SetTelegramTokenAsync("123456:ABCdefGHIjklMNOpqrSTUvwxYZ", T);
        await Set(settings, "alerts.whatsapp_enabled", true);
        await Set(settings, "alerts.whatsapp_phone_number_id", "106540352242922");
        await Set(settings, "alerts.whatsapp_recipients", new[] { "+213 550 000 002" });
        await Set(settings, "alerts.whatsapp_min_severity", "CRITICAL");
        await alerts.SetSecretAsync(AlertService.WhatsAppTokenKey, "EAAG-secret-whatsapp-token-0123456789", T);

        await db.InTransactionAsync(T, async (c, tx) =>
        {
            await GateService.InsertAlertAsync(c, tx, "GATE_BAD_SIGNATURE", "CRITICAL", "gate_scans", "1", new { ticket_id = "Q1-000482" });
            await GateService.InsertAlertAsync(c, tx, "CAMERA_FAILED", "MEDIUM", "cameras", "2", new { error = "timeout" });
            return 0;
        });

        var first = await alerts.DispatchOnceAsync();
        var second = await alerts.DispatchOnceAsync();

        Assert.Equal(6, first.Sent); // critical: 1 SMS + 2 Telegram + 1 WhatsApp; medium: 2 Telegram
        Assert.Equal(0, second.Sent);
        var text = Assert.Single(sms.Sent).Text;
        Assert.Contains("وصل مزور", text);
        Assert.Contains("Q1-000482", text);
        Assert.Equal(4, tg.Sent.Count);
        var w = Assert.Single(wa.Sent);
        Assert.Equal("truckgate_alert", w.Config.Template);
        Assert.Equal("v26.0", w.Config.ApiVersion);
        Assert.Equal("EAAG-secret-whatsapp-token-0123456789", w.Config.Token);
        Assert.Equal("COM3", sms.Sent[0].Config.Port);
        // the token is stored encrypted and never in the audit log
        var stored = await db.ReadAsync(c => c.ExecuteScalarAsync<byte[]>("SELECT value_enc FROM tg.channel_secrets"));
        Assert.DoesNotContain("ABCdef", Encoding.UTF8.GetString(stored!));
        Assert.Equal(0L, await db.ReadAsync(c => c.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM tg.audit_log WHERE payload::text LIKE '%value_enc%' OR payload::text LIKE '%ABCdef%' OR payload::text LIKE '%EAAG%'")));

        var list = await alerts.ListAsync(openOnly: true, limit: 10);
        Assert.Equal("CRITICAL", list[0].Severity);
        await alerts.AcknowledgeAsync(list[0].Id, await new AuthService(db).CreateFirstAdminAsync("boss", "Boss", "a-long-password"), T);
        Assert.Single(await alerts.ListAsync(openOnly: true, limit: 10));
    }

    [Fact]
    public async Task Failed_delivery_is_logged_and_retried_then_given_up()
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var tg = new FakeTelegram { Fail = true };
        var alerts = new AlertService(db, SecretBox.Ephemeral(), tg, new FakeSms(), new FakeWhatsApp(), new StorageOptions(Path.GetTempPath()));
        var settings = new SettingsService(db);
        await Set(settings, "alerts.telegram_enabled", true);
        await Set(settings, "alerts.telegram_chat_ids", new[] { "456" });
        await alerts.SetTelegramTokenAsync("123456:ABCdefGHIjklMNOpqrSTUvwxYZ", T);
        await db.InTransactionAsync(T, (c, tx) => GateService.InsertAlertAsync(c, tx, "NO_EXIT_SCAN", "MEDIUM", "trips", "9", new { trip_number = "T-000009" }));

        for (int i = 0; i < 7; i++) await alerts.DispatchOnceAsync();
        Assert.Equal(5L, await db.ReadAsync(c => c.ExecuteScalarAsync<long>("SELECT count(*) FROM tg.alert_deliveries WHERE NOT ok")));

        tg.Fail = false;
        await alerts.DispatchOnceAsync();
        Assert.Empty(tg.Sent); // gave up after 5 attempts; still visible on screen
        Assert.Equal(5, (await alerts.ListAsync(true, 10))[0].FailedDeliveries);
    }

    [Fact]
    public async Task Settings_accept_only_known_keys_and_valid_values()
    {
        var (_, db) = await fx.CreateIsolatedDatabaseAsync();
        var s = new SettingsService(db);

        await Set(s, "trip.price_per_trip", 1500.5);
        await Set(s, "alerts.language", "fr");
        await Set(s, "site.timezone", "Africa/Algiers");
        foreach (var (key, value) in new (string, object)[]
                 {
                     ("unknown.key", 1), ("trip.price_per_trip", -1), ("gate.min_travel_s", "600"), ("alerts.language", "en"),
                     ("alerts.sms_recipients", new[] { "call me" }), ("site.timezone", "Mars/Olympus"), ("alerts.sms_enabled", 1),
                     ("alerts.sms_router_url", "ftp://router"), ("alerts.whatsapp_template", "Truck Gate"),
                     ("alerts.whatsapp_api_version", "latest"), ("alerts.sms_transport", "PIGEON"),
                 })
            await Assert.ThrowsAsync<TruckGateException>(() => Set(s, key, value));

        var list = await s.ListAsync();
        Assert.Equal("1500.5", list.Single(x => x.Key == "trip.price_per_trip").Value);
        Assert.Equal("\"fr\"", list.Single(x => x.Key == "alerts.language").Value);
    }

    [Fact]
    public async Task Modem_dialogue_sends_ucs2_text_and_reports_errors()
    {
        var modem = new FakeModem(ok: true);
        await new AtModem(modem).SendSmsAsync("+213550000001", "تنبيه", CancellationToken.None);
        Assert.Contains("AT+CMGF=1\r", modem.Written);
        Assert.Contains("AT+CSCS=\"UCS2\"\r", modem.Written);
        Assert.Contains($"AT+CMGS=\"{AtModem.Ucs2Hex("+213550000001")}\"\r", modem.Written);
        Assert.Contains(AtModem.Ucs2Hex("تنبيه") + "\u001A", modem.Written);
        Assert.Equal("062A", AtModem.Ucs2Hex("ت"));

        var failing = new FakeModem(ok: false);
        var e = await Assert.ThrowsAsync<AlertChannelException>(() =>
            new AtModem(failing).SendSmsAsync("+213550000001", "x", CancellationToken.None));
        Assert.Contains("+CMS ERROR: 38", e.Message);
        await Assert.ThrowsAsync<AlertChannelException>(() => new AtModem(new FakeModem(true)).SendSmsAsync("call me", "x", default));
    }

    [Fact]
    public void Alert_text_is_short_and_translated()
    {
        var zone = TimeZoneInfo.Utc;
        var ar = AlertText.Format("GATE_EXIT_LOADED", "CRITICAL", """{"trip_number":"T-000123"}""", new DateTime(2026, 11, 22, 7, 5, 0), "ar", zone);
        var fr = AlertText.Format("GATE_EXIT_LOADED", "CRITICAL", """{"trip_number":"T-000123"}""", new DateTime(2026, 11, 22, 7, 5, 0), "fr", zone);
        Assert.Equal("[حرج] شاحنة خرجت محمّلة — T-000123 07:05 22/11", ar);
        Assert.Equal("[CRITIQUE] Camion sorti chargé — T-000123 07:05 22/11", fr);
        Assert.True(ar.Length <= AtModem.MaxUcs2Chars);
    }

    /// <summary>Answers AT commands like a GSM modem: OK, the "> " prompt, then +CMGS or an error.</summary>
    private sealed class FakeModem(bool ok) : Stream
    {
        private readonly Queue<byte> _out = new();
        private readonly SemaphoreSlim _available = new(0);
        public string Written { get; private set; } = "";

        public override void Write(byte[] buffer, int offset, int count)
        {
            var s = Encoding.ASCII.GetString(buffer, offset, count);
            Written += s;
            string reply = s.StartsWith("AT+CMGS", StringComparison.Ordinal) ? "\r\n> "
                : s.EndsWith('\u001A') ? (ok ? "\r\n+CMGS: 17\r\n\r\nOK\r\n" : "\r\n+CMS ERROR: 38\r\n")
                : "\r\nOK\r\n";
            foreach (var b in Encoding.ASCII.GetBytes(reply)) _out.Enqueue(b);
            _available.Release();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_out.Count == 0) await _available.WaitAsync(ct);
            int n = 0;
            while (n < buffer.Length && _out.Count > 0) buffer.Span[n++] = _out.Dequeue();
            return n;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var copy = buffer.ToArray();
            Write(copy, 0, copy.Length);
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }
}
