using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace TruckGate.Application.Cameras;

public sealed record DiscoveredDevice(string EndpointId, IReadOnlyList<string> XAddrs, string? Name, string? Hardware, string FromIp);

public sealed record OnvifProfile(string Token, string Name, int? Width, int? Height, string? SnapshotUri, string? StreamUri = null);

public sealed record OnvifDeviceInfo(string? Manufacturer, string? Model, string? Firmware, string? Serial,
    IReadOnlyList<OnvifProfile> Profiles);

public sealed class OnvifException(string message) : Exception(message);

/// <summary>
/// WS-Discovery (ONVIF network discovery): a multicast Probe on 239.255.255.250:3702 from every
/// IPv4 interface; cameras and recorders answer with their ONVIF service address.
/// </summary>
public static class WsDiscovery
{
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 3702);

    public static string ProbeMessage(string type, Guid messageId) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:dn="http://www.onvif.org/ver10/network/wsdl" xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
        <e:Header><w:MessageID>uuid:{messageId}</w:MessageID><w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To><w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action></e:Header>
        <e:Body><d:Probe><d:Types>{type}</d:Types></d:Probe></e:Body>
        </e:Envelope>
        """;

    public static async Task<IReadOnlyList<DiscoveredDevice>> ProbeAsync(TimeSpan listen, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

        var tasks = addresses.Select(async local =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0));
                // send the probe out of this interface (Linux ignores the bound address for multicast)
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                foreach (var type in new[] { "dn:NetworkVideoTransmitter", "tds:Device" })
                {
                    var bytes = Encoding.UTF8.GetBytes(ProbeMessage(type, Guid.NewGuid()));
                    await udp.SendAsync(bytes, Multicast, ct);
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(listen);
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await udp.ReceiveAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    foreach (var d in ParseProbeMatches(Encoding.UTF8.GetString(r.Buffer), r.RemoteEndPoint.Address.ToString()))
                        lock (found) found.TryAdd(d.EndpointId, d);
                }
            }
            catch (SocketException)
            {
                // interface without multicast route: skip
            }
        });
        await Task.WhenAll(tasks);
        return found.Values.OrderBy(d => d.FromIp, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<DiscoveredDevice> ParseProbeMatches(string xml, string fromIp)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return []; }
        var result = new List<DiscoveredDevice>();
        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var id = match.Descendants().FirstOrDefault(e => e.Name.LocalName == "Address")?.Value.Trim();
            var xaddrs = (match.Descendants().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                .ToList();
            if (string.IsNullOrEmpty(id) || xaddrs.Count == 0) continue;
            var scopes = (match.Descendants().FirstOrDefault(e => e.Name.LocalName == "Scopes")?.Value ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result.Add(new DiscoveredDevice(id, xaddrs, Scope(scopes, "name"), Scope(scopes, "hardware"), fromIp));
        }
        return result;
    }

    private static string? Scope(string[] scopes, string key)
    {
        var prefix = $"onvif://www.onvif.org/{key}/";
        var s = scopes.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return s is null ? null : Uri.UnescapeDataString(s[prefix.Length..]);
    }
}

/// <summary>
/// Minimal ONVIF client (SOAP 1.2, WS-Security UsernameToken digest): device information,
/// media profiles and the snapshot address of each profile. Recorders (NVR) expose one profile
/// per channel, so cameras behind a recorder are found too.
/// </summary>
public sealed class OnvifClient(HttpClient http, string? username, string? password)
{
    private const string Soap = "http://www.w3.org/2003/05/soap-envelope";
    private const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    private const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";
    private TimeSpan _clockOffset = TimeSpan.Zero;

    public static string PasswordDigest(byte[] nonce, string created, string password) =>
        Convert.ToBase64String(SHA1.HashData([.. nonce, .. Encoding.UTF8.GetBytes(created + password)]));

    public static string Envelope(string body, string? username, string? password, DateTime utcNow, byte[] nonce)
    {
        var header = "";
        if (!string.IsNullOrEmpty(username))
        {
            var created = utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var digest = PasswordDigest(nonce, created, password ?? "");
            header = $"""
                <s:Header><Security s:mustUnderstand="1" xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"><UsernameToken><Username>{System.Security.SecurityElement.Escape(username)}</Username><Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</Password><Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</Nonce><Created xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">{created}</Created></UsernameToken></Security></s:Header>
                """;
        }
        return $"""<?xml version="1.0" encoding="UTF-8"?><s:Envelope xmlns:s="{Soap}">{header}<s:Body>{body}</s:Body></s:Envelope>""";
    }

    public async Task<OnvifDeviceInfo> ProbeAsync(string deviceUrl, CancellationToken ct = default)
    {
        await SyncClockAsync(deviceUrl, ct);
        var info = await CallAsync(deviceUrl, $"""<GetDeviceInformation xmlns="{DeviceNs}"/>""", ct);
        string? Field(string name) => info.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

        var mediaUrl = deviceUrl;
        try
        {
            var caps = await CallAsync(deviceUrl,
                $"""<GetCapabilities xmlns="{DeviceNs}"><Category>Media</Category></GetCapabilities>""", ct);
            var x = caps.Descendants().FirstOrDefault(e => e.Name.LocalName == "Media")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value.Trim();
            if (!string.IsNullOrEmpty(x)) mediaUrl = RebaseToReachableHost(x, deviceUrl);
        }
        catch (OnvifException)
        {
            // some devices serve media on the device address
        }

        var profilesXml = await CallAsync(mediaUrl, $"""<GetProfiles xmlns="{MediaNs}"/>""", ct);
        var profiles = new List<OnvifProfile>();
        foreach (var (token, name, w, h) in ParseProfiles(profilesXml))
        {
            string? snapshot = null;
            try
            {
                var snap = await CallAsync(mediaUrl,
                    $"""<GetSnapshotUri xmlns="{MediaNs}"><ProfileToken>{System.Security.SecurityElement.Escape(token)}</ProfileToken></GetSnapshotUri>""", ct);
                snapshot = ParseSnapshotUri(snap);
                if (snapshot is not null) snapshot = RebaseToReachableHost(snapshot, deviceUrl);
            }
            catch (OnvifException)
            {
                // profile without snapshot support: listed, but cannot be used for capture
            }
            string? stream = null;
            try
            {
                var uri = await CallAsync(mediaUrl,
                    $"""<GetStreamUri xmlns="{MediaNs}"><StreamSetup><Stream xmlns="http://www.onvif.org/ver10/schema">RTP-Unicast</Stream><Transport xmlns="http://www.onvif.org/ver10/schema"><Protocol>RTSP</Protocol></Transport></StreamSetup><ProfileToken>{System.Security.SecurityElement.Escape(token)}</ProfileToken></GetStreamUri>""", ct);
                stream = ParseSnapshotUri(uri);
                if (stream is not null) stream = RebaseToReachableHost(stream, deviceUrl);
            }
            catch (OnvifException)
            {
                // no RTSP stream for this profile: pictures only, no event video
            }
            profiles.Add(new OnvifProfile(token, name, w, h, snapshot, stream));
        }
        return new OnvifDeviceInfo(Field("Manufacturer"), Field("Model"), Field("FirmwareVersion"), Field("SerialNumber"), profiles);
    }

    public static IReadOnlyList<(string Token, string Name, int? Width, int? Height)> ParseProfiles(XDocument doc) =>
        doc.Descendants().Where(e => e.Name.LocalName == "Profiles")
            .Select(p =>
            {
                var token = p.Attribute("token")?.Value ?? "";
                var name = p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? token;
                var res = p.Descendants().FirstOrDefault(e => e.Name.LocalName == "VideoEncoderConfiguration")?
                    .Descendants().FirstOrDefault(e => e.Name.LocalName == "Resolution");
                int? Dim(string n) => int.TryParse(res?.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
                return (token, name, Dim("Width"), Dim("Height"));
            })
            .Where(p => p.token.Length > 0)
            .ToList();

    public static string? ParseSnapshotUri(XDocument doc) =>
        doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "MediaUri")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value.Trim();

    /// <summary>
    /// Devices sometimes advertise an internal or stale IP. If the advertised host differs from the
    /// one we reached, keep the path and port but use the reachable host.
    /// </summary>
    public static string RebaseToReachableHost(string advertised, string reached)
    {
        if (!Uri.TryCreate(advertised, UriKind.Absolute, out var a) || !Uri.TryCreate(reached, UriKind.Absolute, out var r))
            return advertised;
        if (string.Equals(a.Host, r.Host, StringComparison.OrdinalIgnoreCase)) return advertised;
        return new UriBuilder(a) { Host = r.Host }.Uri.ToString();
    }

    private async Task SyncClockAsync(string deviceUrl, CancellationToken ct)
    {
        try
        {
            var doc = await SendAsync(deviceUrl, Envelope($"""<GetSystemDateAndTime xmlns="{DeviceNs}"/>""", null, null,
                DateTime.UtcNow, []), ct);
            var utc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime");
            if (utc is null) return;
            int Part(string section, string name) => int.Parse(utc.Elements().First(e => e.Name.LocalName == section)
                .Elements().First(e => e.Name.LocalName == name).Value, CultureInfo.InvariantCulture);
            var device = new DateTime(Part("Date", "Year"), Part("Date", "Month"), Part("Date", "Day"),
                Part("Time", "Hour"), Part("Time", "Minute"), Part("Time", "Second"), DateTimeKind.Utc);
            _clockOffset = device - DateTime.UtcNow;
        }
        catch (Exception e) when (e is OnvifException or FormatException or InvalidOperationException or ArgumentException)
        {
            _clockOffset = TimeSpan.Zero;
        }
    }

    private Task<XDocument> CallAsync(string url, string body, CancellationToken ct) =>
        SendAsync(url, Envelope(body, username, password, DateTime.UtcNow + _clockOffset, RandomNumberGenerator.GetBytes(16)), ct);

    private async Task<XDocument> SendAsync(string url, string envelope, CancellationToken ct)
    {
        using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, content, ct);
        }
        catch (HttpRequestException e)
        {
            throw new OnvifException($"cannot reach {url}: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OnvifException($"timeout reaching {url}");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            XDocument doc;
            try { doc = XDocument.Parse(text); }
            catch (System.Xml.XmlException) { throw new OnvifException($"HTTP {(int)response.StatusCode}: not a SOAP answer"); }
            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is not null || !response.IsSuccessStatusCode)
            {
                var reason = fault?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value
                             ?? fault?.Descendants().LastOrDefault(e => e.Name.LocalName == "Value")?.Value
                             ?? $"HTTP {(int)response.StatusCode}";
                throw new OnvifException(reason.Trim());
            }
            return doc;
        }
    }
}
