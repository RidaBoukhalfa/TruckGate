using System.Net;
using System.Text;
using System.Text.Json;
using TruckGate.Application.Alerts;

namespace TruckGate.Server.Tests;

/// <summary>WhatsApp Cloud API requests and SMS through a ZTE router's web interface, against in-process fakes.</summary>
public class ChannelTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(object o, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    // ------------------------------------------------------------------ WhatsApp

    private static readonly WhatsAppConfig Wa = new("EAAG-token", "v26.0", "106540352242922", "truckgate_alert", "ar");

    [Fact]
    public async Task WhatsApp_sends_the_approved_template_with_one_clean_parameter_and_an_optional_picture()
    {
        var handler = new Handler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/media")
            ? Json(new { id = "MEDIA-1" })
            : Json(new { messaging_product = "whatsapp", messages = new[] { new { id = "wamid.1" } } }));
        var sender = new WhatsAppSender(new HttpClient(handler), "https://graph.test");

        await sender.SendTemplateAsync(Wa, "+213 550 000 002", "[حرج] شاحنة خرجت محمّلة\n—    T-000123\t07:05", [0xFF, 0xD8, 1, 2], default);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal("https://graph.test/v26.0/106540352242922/media", handler.Calls[0].Request.RequestUri!.ToString());
        var (msg, body) = handler.Calls[1];
        Assert.Equal("https://graph.test/v26.0/106540352242922/messages", msg.RequestUri!.ToString());
        Assert.Equal("Bearer EAAG-token", msg.Headers.Authorization!.ToString());
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("213550000002", json.GetProperty("to").GetString());
        Assert.Equal("template", json.GetProperty("type").GetString());
        var template = json.GetProperty("template");
        Assert.Equal("truckgate_alert", template.GetProperty("name").GetString());
        Assert.Equal("ar", template.GetProperty("language").GetProperty("code").GetString());
        var components = template.GetProperty("components");
        Assert.Equal("MEDIA-1", components[0].GetProperty("parameters")[0].GetProperty("image").GetProperty("id").GetString());
        var text = components[1].GetProperty("parameters")[0].GetProperty("text").GetString()!;
        Assert.Equal("[حرج] شاحنة خرجت محمّلة — T-000123 07:05", text);
        Assert.DoesNotContain('\n', text);
    }

    [Fact]
    public async Task WhatsApp_errors_carry_metas_code_and_details()
    {
        var handler = new Handler((_, _) => Json(new
        {
            error = new
            {
                message = "(#132001) Template name does not exist in the translation", code = 132001,
                error_data = new { details = "template name (truckgate_alert) does not exist in ar" },
            },
        }, HttpStatusCode.NotFound));
        var sender = new WhatsAppSender(new HttpClient(handler), "https://graph.test");

        var e = await Assert.ThrowsAsync<AlertChannelException>(() => sender.SendTemplateAsync(Wa, "213550000002", "x", null, default));

        Assert.Contains("132001", e.Message);
        Assert.Contains("does not exist in ar", e.Message);
        Assert.Single(handler.Calls); // no picture: no upload
    }

    // ------------------------------------------------------------------ ZTE router

    /// <summary>Behaves like the web interface of a ZTE MiFi / CPE (goform API).</summary>
    private sealed class FakeZte(string password, bool saltedLogin = true, string adHash = "md5")
    {
        public const string Ld = "ABCDEF0123456789";
        public const string Inner = "BD_MU5001V1.0.0B06";
        public const string Cr = "CR_MU5001_V1.0";
        public const string Rd = "8f14e45fceea167a";
        private bool _loggedIn;
        public List<(string Number, string Text)> Sms { get; } = [];
        public bool RefererSeen { get; private set; } = true;

        public HttpResponseMessage Respond(HttpRequestMessage req, string body)
        {
            if (req.Headers.Referrer is null) RefererSeen = false;
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            if (req.Method == HttpMethod.Get)
                return query["cmd"] switch
                {
                    "LD" => Json(new { LD = saltedLogin ? Ld : "" }),
                    "wa_inner_version,cr_version" => Json(new { wa_inner_version = Inner, cr_version = Cr }),
                    "RD" => Json(new { RD = Rd }),
                    "sms_cmd_status_info" => Json(new { sms_cmd = "4", sms_cmd_status_result = "3" }),
                    _ => Json(new { }),
                };
            var form = System.Web.HttpUtility.ParseQueryString(body);
            switch (form["goformId"])
            {
                case "LOGIN":
                    var expected = saltedLogin
                        ? ZteRouterSmsSender.Sha256Upper(ZteRouterSmsSender.Sha256Upper(password) + Ld)
                        : Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
                    _loggedIn = form["password"] == expected;
                    return Json(new { result = _loggedIn ? "0" : "1" });
                case "SEND_SMS":
                    var basis = Inner + Cr;
                    var ad = adHash == "md5"
                        ? ZteRouterSmsSender.Md5Lower(ZteRouterSmsSender.Md5Lower(basis) + Rd)
                        : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
                            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(basis))) + Rd)));
                    if (!_loggedIn || form["AD"] != ad || form["encode_type"] != "UNICODE") return Json(new { result = "failure" });
                    var hex = form["MessageBody"]!;
                    Sms.Add((form["Number"]!, Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex))));
                    return Json(new { result = "success" });
                default:
                    return Json(new { result = "success" });
            }
        }
    }

    private static SmsConfig Router(string password) => new(SmsConfig.ZteRouter, "", "http://192.168.0.1", "", password);

    [Theory]
    [InlineData(true, "md5")]
    [InlineData(false, "md5")]
    [InlineData(true, "sha256")]
    public async Task Zte_router_logs_in_and_sends_unicode_sms(bool saltedLogin, string adHash)
    {
        var router = new FakeZte("admin-pass", saltedLogin, adHash);
        var sender = new ZteRouterSmsSender(() => new Handler(router.Respond));

        await sender.SendAsync(Router("admin-pass"), "+213550000001", "تنبيه حرج: وصل مزور Q1-000482", default);

        var sms = Assert.Single(router.Sms);
        Assert.Equal("+213550000001", sms.Number);
        Assert.Equal("تنبيه حرج: وصل مزور Q1-000482", sms.Text);
        Assert.True(router.RefererSeen);
    }

    [Fact]
    public async Task Zte_router_wrong_password_is_reported_and_nothing_is_sent()
    {
        var router = new FakeZte("admin-pass");
        var sender = new ZteRouterSmsSender(() => new Handler(router.Respond));

        var e = await Assert.ThrowsAsync<AlertChannelException>(() =>
            sender.SendAsync(Router("wrong"), "+213550000001", "x", default));

        Assert.Contains("login refused", e.Message);
        Assert.Empty(router.Sms);
    }

    [Fact]
    public void Router_time_format_matches_the_web_interface()
    {
        Assert.Equal("26;11;22;08;42;05;+1", ZteRouterSmsSender.SmsTime(new DateTimeOffset(2026, 11, 22, 8, 42, 5, TimeSpan.FromHours(1))));
        Assert.Equal("26;01;02;03;04;05;-3.5", ZteRouterSmsSender.SmsTime(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-3.5))));
    }
}
