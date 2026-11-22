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

public interface ISmsSender
{
    Task SendAsync(string port, string number, string text, CancellationToken ct);
}

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

    public async Task SendAsync(string port, string number, string text, CancellationToken ct)
    {
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
