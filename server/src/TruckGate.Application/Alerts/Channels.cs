using System.Globalization;
using System.IO.Ports;
using System.Net.Http.Json;
using System.Text;

namespace TruckGate.Application.Alerts;

public interface ITelegramSender
{
    Task SendTextAsync(string token, string chatId, string text, CancellationToken ct);
    Task SendPhotoAsync(string token, string chatId, byte[] jpeg, string caption, CancellationToken ct);
}

/// <summary>How SMS leave the factory: a serial GSM modem (AT commands) or a 4G/5G router's web interface.</summary>
public sealed record SmsConfig(string Transport, string Port, string RouterUrl, string RouterUser, string? RouterPassword)
{
    public const string Modem = "MODEM";
    public const string ZteRouter = "ZTE_ROUTER";

    public bool IsComplete => Transport == ZteRouter ? RouterUrl.Length > 0 && !string.IsNullOrEmpty(RouterPassword) : Port.Length > 0;
}

public interface ISmsSender
{
    Task SendAsync(SmsConfig config, string number, string text, CancellationToken ct);
}

public interface IWhatsAppSender
{
    /// <summary>Sends an approved template whose body has one parameter (the alert text); optional image header.</summary>
    Task SendTemplateAsync(WhatsAppConfig config, string to, string text, byte[]? jpeg, CancellationToken ct);
}

public sealed record WhatsAppConfig(string Token, string ApiVersion, string PhoneNumberId, string Template, string Language);

public sealed class AlertChannelException(string message) : Exception(message);

/// <summary>Telegram Bot API (needs internet at the factory). Base URL is configurable for tests.</summary>
public sealed class TelegramSender(HttpClient http, string baseUrl = "https://api.telegram.org") : ITelegramSender
{
    public async Task SendTextAsync(string token, string chatId, string text, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/bot{token}/sendMessage",
            new { chat_id = chatId, text, disable_web_page_preview = true }, ct);
        await EnsureOkAsync(response, ct);
    }

    public async Task SendPhotoAsync(string token, string chatId, byte[] jpeg, string caption, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId), "chat_id" },
            { new StringContent(caption), "caption" },
        };
        var photo = new ByteArrayContent(jpeg);
        photo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(photo, "photo", "snapshot.jpg");
        using var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/bot{token}/sendPhoto", form, ct);
        await EnsureOkAsync(response, ct);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new AlertChannelException($"Telegram HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
    }
}

/// <summary>
/// SMS through a GSM modem (USB dongle or serial modem with a SIM card) using standard AT commands.
/// Works without internet. Texts are sent in UCS-2 so Arabic is readable; one SMS holds 70 characters.
/// </summary>
public sealed class SerialSmsSender : ISmsSender
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SendAsync(SmsConfig config, string number, string text, CancellationToken ct)
    {
        var port = config.Port;
        await _lock.WaitAsync(ct);
        try
        {
            using var serial = new SerialPort(port, 115200) { ReadTimeout = 2000, WriteTimeout = 2000, Handshake = Handshake.None };
            serial.Open();
            await new AtModem(serial.BaseStream).SendSmsAsync(number, text, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            throw new AlertChannelException($"modem {port}: {e.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>AT command dialogue for one SMS, independent of the transport (tested with a fake modem).</summary>
public sealed class AtModem(Stream stream)
{
    public const int MaxUcs2Chars = 70;
    private readonly StringBuilder _buffer = new();
    private readonly byte[] _read = new byte[256];

    public async Task SendSmsAsync(string number, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(number) || !number.TrimStart('+').All(char.IsAsciiDigit))
            throw new AlertChannelException($"invalid phone number '{number}'");
        var body = text.Length > MaxUcs2Chars ? text[..(MaxUcs2Chars - 1)] + "…" : text;

        await CommandAsync("AT", "OK", ct);
        await CommandAsync("ATE0", "OK", ct);
        await CommandAsync("AT+CMGF=1", "OK", ct);
        await CommandAsync("AT+CSCS=\"UCS2\"", "OK", ct);
        await CommandAsync("AT+CSMP=17,167,0,8", "OK", ct);
        await CommandAsync($"AT+CMGS=\"{Ucs2Hex(number.Trim())}\"", ">", ct);
        await WriteAsync(Ucs2Hex(body) + "\u001A", ct);
        await ExpectAsync("+CMGS:", TimeSpan.FromSeconds(60), ct);
        await ExpectAsync("OK", TimeSpan.FromSeconds(10), ct);
    }

    public static string Ucs2Hex(string s) =>
        string.Concat(Encoding.BigEndianUnicode.GetBytes(s).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private async Task CommandAsync(string command, string expect, CancellationToken ct)
    {
        await WriteAsync(command + "\r", ct);
        await ExpectAsync(expect, TimeSpan.FromSeconds(5), ct);
    }

    private async Task WriteAsync(string s, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private async Task ExpectAsync(string token, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            var text = _buffer.ToString();
            var at = text.IndexOf(token, StringComparison.Ordinal);
            var error = Math.Max(text.IndexOf("ERROR", StringComparison.Ordinal), -1);
            if (error >= 0 && (at < 0 || error < at))
            {
                var line = text[error..].Split('\n')[0].Trim();
                var cms = text.IndexOf("+CMS ERROR", StringComparison.Ordinal);
                throw new AlertChannelException("modem: " + (cms >= 0 ? text[cms..].Split('\n')[0].Trim() : line));
            }
            if (at >= 0)
            {
                _buffer.Remove(0, at + token.Length);
                return;
            }
            int n;
            try
            {
                n = await stream.ReadAsync(_read, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AlertChannelException($"modem: no '{token}' within {timeout.TotalSeconds:0} s");
            }
            catch (TimeoutException)
            {
                if (cts.IsCancellationRequested) throw new AlertChannelException($"modem: no '{token}' within {timeout.TotalSeconds:0} s");
                continue;
            }
            if (n == 0) throw new AlertChannelException("modem: connection closed");
            _buffer.Append(Encoding.ASCII.GetString(_read, 0, n));
        }
    }
}

/// <summary>Chooses the SMS transport configured in the settings.</summary>
public sealed class SmsSender(SerialSmsSender modem, ZteRouterSmsSender router) : ISmsSender
{
    public Task SendAsync(SmsConfig config, string number, string text, CancellationToken ct) =>
        config.Transport == SmsConfig.ZteRouter ? router.SendAsync(config, number, text, ct) : modem.SendAsync(config, number, text, ct);
}

/// <summary>
/// SMS through the web interface of a ZTE 4G/5G router or MiFi (the SIM inside the router sends it), as
/// the router's own web page does: /goform/goform_set_cmd_process with goformId=SEND_SMS. The interface
/// is not documented by ZTE and varies by model and firmware; this follows the common variant (LD-salted
/// SHA-256 or Base64 login, AD token = MD5(MD5(wa_inner_version + cr_version) + RD)). Use the Test button.
/// </summary>
public sealed class ZteRouterSmsSender(Func<HttpMessageHandler>? handlerFactory = null) : ISmsSender
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SendAsync(SmsConfig config, string number, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(number) || !number.Trim().TrimStart('+').All(char.IsAsciiDigit))
            throw new AlertChannelException($"invalid phone number '{number}'");
        if (!Uri.TryCreate(config.RouterUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new AlertChannelException("router address must be http://...");
        var body = text.Length > AtModem.MaxUcs2Chars ? text[..(AtModem.MaxUcs2Chars - 1)] + "…" : text;

        await _lock.WaitAsync(ct);
        try
        {
            var cookies = new System.Net.CookieContainer();
            var handler = handlerFactory?.Invoke() ?? new HttpClientHandler();
            if (handler is HttpClientHandler h) { h.CookieContainer = cookies; h.UseCookies = true; }
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), BaseAddress = new Uri(config.RouterUrl.Trim().TrimEnd('/') + "/") };
            http.DefaultRequestHeaders.Referrer = new Uri(baseUri, "/index.html");
            http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

            await LoginAsync(http, config, ct);
            var adCandidates = await AdCandidatesAsync(http, ct);
            string? lastResult = null;
            foreach (var ad in adCandidates)
            {
                var form = new Dictionary<string, string>
                {
                    ["isTest"] = "false", ["goformId"] = "SEND_SMS", ["notCallback"] = "true", ["Number"] = number.Trim(),
                    ["sms_time"] = SmsTime(DateTimeOffset.Now), ["MessageBody"] = AtModem.Ucs2Hex(body), ["ID"] = "-1",
                    ["encode_type"] = "UNICODE",
                };
                if (ad is not null) form["AD"] = ad;
                lastResult = (await PostAsync(http, form, ct)).GetValueOrDefault("result");
                if (lastResult == "success") break;
            }
            if (lastResult != "success") throw new AlertChannelException($"router refused the SMS (result={lastResult ?? "none"})");
            await WaitForSendResultAsync(http, ct);
            try { await PostAsync(http, new Dictionary<string, string> { ["isTest"] = "false", ["goformId"] = "LOGOUT" }, ct); }
            catch (Exception e) when (e is AlertChannelException or HttpRequestException or TaskCanceledException) { /* best effort */ }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new AlertChannelException($"router {baseUri.Host}: {e.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task LoginAsync(HttpClient http, SmsConfig config, CancellationToken ct)
    {
        var ld = (await GetAsync(http, "LD", ct)).GetValueOrDefault("LD") ?? "";
        var password = config.RouterPassword ?? "";
        var encoded = ld.Length > 0
            ? Sha256Upper(Sha256Upper(password) + ld.ToUpperInvariant())
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        var form = new Dictionary<string, string> { ["isTest"] = "false", ["password"] = encoded };
        if (config.RouterUser.Length > 0)
        {
            form["goformId"] = "LOGIN_MULTI_USER";
            form["user"] = config.RouterUser;
        }
        else form["goformId"] = "LOGIN";
        var result = (await PostAsync(http, form, ct)).GetValueOrDefault("result");
        if (result is not ("0" or "3"))
            throw new AlertChannelException(result switch
            {
                "1" or "2" => "router login refused (wrong password?)",
                "5" => "router login locked after failed attempts; wait and retry",
                _ => $"router login failed (result={result ?? "none"})",
            });
    }

    /// <summary>The AD anti-forgery token: MD5 variant first (most firmware), then SHA-256, then none.</summary>
    private static async Task<IReadOnlyList<string?>> AdCandidatesAsync(HttpClient http, CancellationToken ct)
    {
        var v = await GetAsync(http, "wa_inner_version,cr_version", ct, multi: true);
        var rd = (await GetAsync(http, "RD", ct)).GetValueOrDefault("RD");
        if (string.IsNullOrEmpty(rd)) return [null];
        var basis = (v.GetValueOrDefault("wa_inner_version") ?? "") + (v.GetValueOrDefault("cr_version") ?? "");
        return [Md5Lower(Md5Lower(basis) + rd), Sha256Lower(Sha256Lower(basis) + rd)];
    }

    /// <summary>Asks the router whether the SMS left (sms_cmd 4: 3 = sent, 2 = failed); unknown answers count as sent.</summary>
    private static async Task WaitForSendResultAsync(HttpClient http, CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000, ct);
            var status = await GetAsync(http, "sms_cmd_status_info&sms_cmd=4", ct);
            switch (status.GetValueOrDefault("sms_cmd_status_result"))
            {
                case "3": return;
                case "2": throw new AlertChannelException("router reports the SMS was not sent (network or SIM credit)");
                case "1": continue;
                default: return;
            }
        }
    }

    private static async Task<Dictionary<string, string?>> GetAsync(HttpClient http, string cmd, CancellationToken ct, bool multi = false)
    {
        var url = $"goform/goform_get_cmd_process?isTest=false&cmd={cmd}{(multi ? "&multi_data=1" : "")}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var response = await http.GetAsync(url, ct);
        return await ParseAsync(response, ct);
    }

    private static async Task<Dictionary<string, string?>> PostAsync(HttpClient http, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync("goform/goform_set_cmd_process", new FormUrlEncodedContent(form), ct);
        return await ParseAsync(response, ct);
    }

    private static async Task<Dictionary<string, string?>> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new AlertChannelException($"router HTTP {(int)response.StatusCode}");
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : p.Value.ToString())
            : new Dictionary<string, string?>();
    }

    /// <summary>Router format: yy;MM;dd;HH;mm;ss;+H (local time and offset in hours).</summary>
    public static string SmsTime(DateTimeOffset now) =>
        now.ToString("yy;MM;dd;HH;mm;ss;", CultureInfo.InvariantCulture) +
        (now.Offset >= TimeSpan.Zero ? "+" : "-") + Math.Abs(now.Offset.TotalHours).ToString("0.##", CultureInfo.InvariantCulture);

    public static string Sha256Upper(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static string Sha256Lower(string s) => Sha256Upper(s).ToLowerInvariant();
    public static string Md5Lower(string s) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>
/// WhatsApp Business Platform, Cloud API (official): an approved UTILITY template with one body
/// parameter, sent from the business phone number. Needs internet. Optional image header (the template
/// must then have an IMAGE header); the picture is uploaded first and referenced by its media id.
/// </summary>
public sealed class WhatsAppSender(HttpClient http, string baseUrl = "https://graph.facebook.com") : IWhatsAppSender
{
    public async Task SendTemplateAsync(WhatsAppConfig c, string to, string text, byte[]? jpeg, CancellationToken ct)
    {
        var root = $"{baseUrl.TrimEnd('/')}/{c.ApiVersion}/{c.PhoneNumberId}";
        string? mediaId = null;
        if (jpeg is not null)
        {
            using var form = new MultipartFormDataContent
            {
                { new StringContent("whatsapp"), "messaging_product" },
                { new StringContent("image/jpeg"), "type" },
            };
            var file = new ByteArrayContent(jpeg);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "file", "snapshot.jpg");
            using var upload = new HttpRequestMessage(HttpMethod.Post, root + "/media") { Content = form };
            upload.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", c.Token);
            using var uploaded = await http.SendAsync(upload, ct);
            mediaId = (await ReadAsync(uploaded, ct)).GetProperty("id").GetString();
        }

        var components = new List<object>();
        if (mediaId is not null)
            components.Add(new { type = "header", parameters = new object[] { new { type = "image", image = new { id = mediaId } } } });
        components.Add(new { type = "body", parameters = new object[] { new { type = "text", text = Clean(text) } } });
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = new string(to.Where(char.IsAsciiDigit).ToArray()),
            type = "template",
            template = new { name = c.Template, language = new { code = c.Language }, components },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, root + "/messages") { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", c.Token);
        using var response = await http.SendAsync(request, ct);
        var json = await ReadAsync(response, ct);
        if (!json.TryGetProperty("messages", out var m) || m.GetArrayLength() == 0)
            throw new AlertChannelException("WhatsApp: no message id in the answer");
    }

    /// <summary>Template parameters cannot hold new lines, tabs or more than 4 spaces in a row.</summary>
    public static string Clean(string text)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length > 900 ? flat[..900] : flat;
    }

    private static async Task<System.Text.Json.JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        System.Text.Json.JsonElement json;
        try { json = System.Text.Json.JsonDocument.Parse(body.Length == 0 ? "{}" : body).RootElement.Clone(); }
        catch (System.Text.Json.JsonException) { throw new AlertChannelException($"WhatsApp HTTP {(int)response.StatusCode}"); }
        if (response.IsSuccessStatusCode) return json;
        var error = json.TryGetProperty("error", out var e)
            ? $"{(e.TryGetProperty("code", out var code) ? code.ToString() : "?")} {(e.TryGetProperty("message", out var msg) ? msg.GetString() : "")}" +
              (e.TryGetProperty("error_data", out var d) && d.TryGetProperty("details", out var det) ? $" ({det.GetString()})" : "")
            : body[..Math.Min(body.Length, 200)];
        throw new AlertChannelException($"WhatsApp HTTP {(int)response.StatusCode}: {error}");
    }
}
