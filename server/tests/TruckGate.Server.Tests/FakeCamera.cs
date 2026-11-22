using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TruckGate.Application.Cameras;

namespace TruckGate.Server.Tests;

/// <summary>
/// An ONVIF "camera" running in the test process: answers device and media SOAP calls (checking the
/// WS-Security digest like a real device) and serves JPEG snapshots behind HTTP Basic authentication.
/// It behaves like a 2-channel recorder: two profiles, one snapshot address each.
/// </summary>
public sealed class FakeCamera : IAsyncDisposable
{
    public const string User = "admin";
    public const string Password = "cam-pass-123";
    private readonly WebApplication _app;
    private int _served;

    public string BaseUrl { get; }
    public string DeviceUrl => BaseUrl + "/onvif/device_service";
    public int SnapshotsServed => _served;

    private FakeCamera(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public static async Task<FakeCamera> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeCamera? self = null;

        app.MapPost("/onvif/device_service", async (HttpContext ctx) => await Soap(ctx, self!));
        app.MapPost("/onvif/media_service", async (HttpContext ctx) => await Soap(ctx, self!));
        app.MapGet("/snap/{channel:int}.jpg", (int channel, HttpContext ctx) =>
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));
            if (ctx.Request.Headers.Authorization != expected)
            {
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"cam\"";
                return Results.StatusCode(401);
            }
            var n = Interlocked.Increment(ref self!._served);
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.ASCII.GetBytes($"channel {channel} frame {n}"), 0xFF, 0xD9];
            return Results.File(jpeg, "image/jpeg");
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeCamera(app, address.TrimEnd('/'));
        return self;
    }

    private static async Task<IResult> Soap(HttpContext ctx, FakeCamera cam)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var doc = XDocument.Parse(await reader.ReadToEndAsync());
        var body = doc.Descendants().First(e => e.Name.LocalName == "Body").Elements().First();
        var op = body.Name.LocalName;

        if (op != "GetSystemDateAndTime" && !DigestOk(doc))
            return Xml("""<s:Fault><s:Code><s:Value>s:Sender</s:Value></s:Code><s:Reason><s:Text xml:lang="en">Sender not Authorized</s:Text></s:Reason></s:Fault>""", 400);

        var now = DateTime.UtcNow;
        return op switch
        {
            "GetSystemDateAndTime" => Xml($"""<tds:GetSystemDateAndTimeResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema"><tds:SystemDateAndTime><tt:UTCDateTime><tt:Time><tt:Hour>{now.Hour}</tt:Hour><tt:Minute>{now.Minute}</tt:Minute><tt:Second>{now.Second}</tt:Second></tt:Time><tt:Date><tt:Year>{now.Year}</tt:Year><tt:Month>{now.Month}</tt:Month><tt:Day>{now.Day}</tt:Day></tt:Date></tt:UTCDateTime></tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>"""),
            "GetDeviceInformation" => Xml("""<tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"><tds:Manufacturer>FakeVision</tds:Manufacturer><tds:Model>NVR-2CH</tds:Model><tds:FirmwareVersion>1.0</tds:FirmwareVersion><tds:SerialNumber>SN123</tds:SerialNumber><tds:HardwareId>HW1</tds:HardwareId></tds:GetDeviceInformationResponse>"""),
            // Advertises a wrong host on purpose: the client must keep the host it reached.
            "GetCapabilities" => Xml($"""<tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema"><tds:Capabilities><tt:Media><tt:XAddr>{Rehost(cam.BaseUrl)}/onvif/media_service</tt:XAddr></tt:Media></tds:Capabilities></tds:GetCapabilitiesResponse>"""),
            "GetProfiles" => Xml("""<trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema"><trt:Profiles token="ch1" fixed="true"><tt:Name>Channel 1 - plate</tt:Name><tt:VideoEncoderConfiguration token="v1"><tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution></tt:VideoEncoderConfiguration></trt:Profiles><trt:Profiles token="ch2" fixed="true"><tt:Name>Channel 2 - cargo</tt:Name></trt:Profiles></trt:GetProfilesResponse>"""),
            "GetSnapshotUri" => Xml($"""<trt:GetSnapshotUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema"><trt:MediaUri><tt:Uri>{Rehost(cam.BaseUrl)}/snap/{(body.Value.Contains("ch2") ? 2 : 1)}.jpg</tt:Uri></trt:MediaUri></trt:GetSnapshotUriResponse>"""),
            _ => Xml("""<s:Fault><s:Reason><s:Text>unsupported</s:Text></s:Reason></s:Fault>""", 400),
        };
    }

    private static string Rehost(string baseUrl) => new UriBuilder(baseUrl) { Host = "10.255.255.1" }.Uri.ToString().TrimEnd('/');

    private static bool DigestOk(XDocument doc)
    {
        string? V(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        var user = V("Username");
        var digest = V("Password");
        var nonce = V("Nonce");
        var created = V("Created");
        if (user != User || digest is null || nonce is null || created is null) return false;
        return OnvifClient.PasswordDigest(Convert.FromBase64String(nonce), created, Password) == digest;
    }

    private static IResult Xml(string body, int status = 200) =>
        Results.Content($"""<?xml version="1.0" encoding="UTF-8"?><s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>{body}</s:Body></s:Envelope>""",
            "application/soap+xml", Encoding.UTF8, status);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
