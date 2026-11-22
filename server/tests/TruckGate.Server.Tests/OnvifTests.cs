using System.Text;
using System.Xml.Linq;
using TruckGate.Application.Cameras;

namespace TruckGate.Server.Tests;

public class OnvifTests
{
    [Fact]
    public void Password_digest_matches_the_WS_Security_reference()
    {
        // Python: base64(sha1(nonce + created + password))
        var digest = OnvifClient.PasswordDigest(Encoding.ASCII.GetBytes("0123456789abcdef"), "2026-11-22T06:00:00.000Z", "admin123");
        Assert.Equal("vj/bAVgpcg3S+csjJGZ42iC9YoI=", digest);
    }

    [Fact]
    public void Probe_matches_give_address_name_and_hardware()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope" xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <SOAP-ENV:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <wsa:EndpointReference><wsa:Address>urn:uuid:4d454930-0000-1000-8000-bcbac2f00001</wsa:Address></wsa:EndpointReference>
                    <d:Types>dn:NetworkVideoTransmitter</d:Types>
                    <d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/name/Gate%20NVR onvif://www.onvif.org/hardware/DS-7608 onvif://www.onvif.org/location/</d:Scopes>
                    <d:XAddrs>http://192.168.1.60/onvif/device_service http://[fe80::1]/onvif/device_service</d:XAddrs>
                    <d:MetadataVersion>10</d:MetadataVersion>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </SOAP-ENV:Body>
            </SOAP-ENV:Envelope>
            """;

        var devices = WsDiscovery.ParseProbeMatches(xml, "192.168.1.60");

        var d = Assert.Single(devices);
        Assert.Equal("urn:uuid:4d454930-0000-1000-8000-bcbac2f00001", d.EndpointId);
        Assert.Equal("Gate NVR", d.Name);
        Assert.Equal("DS-7608", d.Hardware);
        Assert.Equal("http://192.168.1.60/onvif/device_service", d.XAddrs[0]);
    }

    [Fact]
    public void Garbage_on_the_discovery_port_is_ignored()
    {
        Assert.Empty(WsDiscovery.ParseProbeMatches("not xml at all", "10.0.0.1"));
        Assert.Empty(WsDiscovery.ParseProbeMatches("<a><ProbeMatch/></a>", "10.0.0.1"));
    }

    [Fact]
    public void Profiles_and_snapshot_uri_are_parsed()
    {
        var profiles = OnvifClient.ParseProfiles(XDocument.Parse("""
            <Envelope><Body><GetProfilesResponse>
              <Profiles token="Profile_101"><Name>mainStream</Name>
                <VideoEncoderConfiguration><Resolution><Width>2688</Width><Height>1520</Height></Resolution></VideoEncoderConfiguration>
              </Profiles>
              <Profiles token="Profile_201"><Name>ch2</Name></Profiles>
            </GetProfilesResponse></Body></Envelope>
            """));
        Assert.Equal(2, profiles.Count);
        Assert.Equal(("Profile_101", "mainStream", (int?)2688, (int?)1520), profiles[0]);
        Assert.Null(profiles[1].Width);

        var uri = OnvifClient.ParseSnapshotUri(XDocument.Parse(
            "<Envelope><Body><GetSnapshotUriResponse><MediaUri><Uri>http://192.168.1.60/ISAPI/Streaming/channels/101/picture</Uri></MediaUri></GetSnapshotUriResponse></Body></Envelope>"));
        Assert.Equal("http://192.168.1.60/ISAPI/Streaming/channels/101/picture", uri);
    }

    [Theory]
    [InlineData("http://10.0.0.9:8080/snap.jpg", "http://192.168.1.60/onvif/device_service", "http://192.168.1.60:8080/snap.jpg")]
    [InlineData("http://192.168.1.60/snap.jpg", "http://192.168.1.60/onvif", "http://192.168.1.60/snap.jpg")]
    public void Advertised_address_is_rebased_to_the_reachable_host(string advertised, string reached, string expected) =>
        Assert.Equal(expected, OnvifClient.RebaseToReachableHost(advertised, reached));

    [Fact]
    public void Envelope_without_credentials_has_no_security_header()
    {
        var plain = OnvifClient.Envelope("<X/>", null, null, DateTime.UtcNow, []);
        var secured = OnvifClient.Envelope("<X/>", "admin", "p", new DateTime(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc),
            Encoding.ASCII.GetBytes("0123456789abcdef"));
        Assert.DoesNotContain("UsernameToken", plain);
        Assert.Contains("<Created xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">2026-11-22T06:00:00.000Z</Created>", secured);
        XDocument.Parse(secured); // well-formed
    }
}
