using System.Net;

namespace TruckGate.Application.Cameras;

public sealed class SnapshotException(string message) : Exception(message);

/// <summary>Downloads one JPEG from a camera snapshot address (HTTP Basic or Digest authentication).</summary>
public static class SnapshotFetcher
{
    private const int MaxBytes = 10 * 1024 * 1024;

    public static HttpClient CreateClient(string? username, string? password, TimeSpan timeout,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        HttpMessageHandler handler;
        if (handlerFactory is not null)
        {
            handler = handlerFactory();
        }
        else
        {
            var h = new HttpClientHandler { PreAuthenticate = false, UseCookies = false };
            if (!string.IsNullOrEmpty(username)) h.Credentials = new NetworkCredential(username, password ?? "");
            handler = h;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    public static async Task<byte[]> FetchAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new SnapshotException($"cannot reach camera: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SnapshotException("camera timeout");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new SnapshotException("camera refused the user name or password");
            if (!response.IsSuccessStatusCode) throw new SnapshotException($"camera answered HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new SnapshotException("picture too large");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxBytes) throw new SnapshotException("picture too large");
            if (!IsJpeg(bytes)) throw new SnapshotException("camera did not return a JPEG picture");
            return bytes;
        }
    }

    public static bool IsJpeg(byte[] bytes) => bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8;
}
