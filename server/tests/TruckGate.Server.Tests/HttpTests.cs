using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TruckGate.Application;
using TruckGate.Data;

namespace TruckGate.Server.Tests;

/// <summary>The real web server (login, roles, CSRF, cameras, scan with pictures) on a throw-away database.</summary>
[Collection("db")]
public class HttpTests(DatabaseFixture fx)
{
    private sealed class Server : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }

    private static async Task<HttpResponseMessage> Post(HttpClient c, string url, object body, bool csrf = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (csrf) req.Headers.Add("X-TG-CSRF", "1");
        return await c.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Gatekeeper_scans_a_ticket_and_the_selected_cameras_record_the_burn()
    {
        var (db, appConnection) = await fx.CreateIsolatedDatabaseForServerAsync();
        var dataDir = Path.Combine(Path.GetTempPath(), "tg-server-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("ConnectionStrings__App", appConnection);
        Environment.SetEnvironmentVariable("ConnectionStrings__Owner", "");
        Environment.SetEnvironmentVariable("Server__PublicUrl", "http://test.local:5080");
        Environment.SetEnvironmentVariable("Data__Directory", dataDir);
        Environment.SetEnvironmentVariable("Background__Disabled", "true");

        // reference data
        var admin = new AdminService(db);
        var t = DatabaseFixture.Tester;
        var gateId = await admin.CreateGateAsync("G1", "Main gate", null, t);
        var driver = await admin.CreateDriverAsync("أحمد ب.", null, "EMPLOYEE", t);
        await admin.CreateTruckAsync(5, "05", "Foton", driver, "01234 216 16", t);
        await admin.CreateUserAsync("omar", "عمر م.", "QUARRY_OPERATOR", "ar", t);
        var keeper = await admin.CreateUserAsync("salim", "سليم ك.", "GATEKEEPER", "ar", t);
        await admin.SetUserPinAsync(keeper, "5937", t);
        await new AuthService(db).CreateFirstAdminAsync("boss", "Admin", "a-long-password");
        using var phone = new QuarryPhone(fx.NextId());
        await admin.EnrollDeviceAsync(phone.DeviceId, "QUARRY_PHONE", "Phone", phone.PublicKey, t);
        await using var cam = await FakeCamera.StartAsync();

        await using var server = new Server();
        var client = server.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        // not signed in → 401; no CSRF header → 403
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/gate/gates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(client, "/api/auth/login", new { username = "salim", secret = "5937" }, csrf: false)).StatusCode);

        // gatekeeper cannot reach admin pages
        await Json(await Post(client, "/api/auth/login", new { username = "salim", secret = "5937" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        await Post(client, "/api/auth/logout", new { });

        // admin discovers the camera channels and selects two for the burn
        await Json(await Post(client, "/api/auth/login", new { username = "boss", secret = "a-long-password" }));
        var probe = await Json(await Post(client, "/api/admin/cameras/probe",
            new { onvifUrl = cam.DeviceUrl, username = FakeCamera.User, password = FakeCamera.Password }));
        foreach (var (index, role) in new[] { (0, "PLATE"), (1, "CARGO") })
        {
            var p = probe.GetProperty("profiles")[index];
            await Json(await Post(client, "/api/admin/cameras", new
            {
                label = p.GetProperty("name").GetString(), gateId, role, captureOnScan = true, frames = 2,
                frameIntervalMs = 50, source = "ONVIF", onvifUrl = cam.DeviceUrl,
                profileToken = p.GetProperty("token").GetString(), snapshotUrl = p.GetProperty("snapshotUri").GetString(),
                username = FakeCamera.User, password = FakeCamera.Password,
            }));
        }
        await Post(client, "/api/auth/logout", new { });

        // gatekeeper scans the paper ticket: verified, burned, pictures taken
        await Json(await Post(client, "/api/auth/login", new { username = "salim", secret = "5937" }));
        var now = await db.ReadAsync(c => Dapper.SqlMapper.ExecuteScalarAsync<long>(c, "SELECT extract(epoch FROM now())::bigint"));
        var qr = phone.Ticket(1, 5, driver, now - 900);
        var scan = await Json(await Post(client, "/api/gate/scan", new { gateId, qrText = qr }));

        Assert.Equal("ACCEPT", scan.GetProperty("scan").GetProperty("outcome").GetString());
        Assert.Equal("05", scan.GetProperty("names").GetProperty("fleetNumber").GetString());
        Assert.Equal("01234 216 16", scan.GetProperty("names").GetProperty("plate").GetString());
        Assert.Equal("أحمد ب.", scan.GetProperty("names").GetProperty("driverName").GetString());
        Assert.Equal(4, scan.GetProperty("snapshots").GetArrayLength());

        var snapId = scan.GetProperty("snapshots")[0].GetProperty("id").GetInt64();
        var picture = await client.GetAsync($"/api/snapshots/{snapId}");
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType?.MediaType);

        // the same paper scanned again is refused, and pictured too
        var replay = await Json(await Post(client, "/api/gate/scan", new { gateId, qrText = qr }));
        Assert.Equal("REJECT", replay.GetProperty("scan").GetProperty("outcome").GetString());
        Assert.Equal(4, replay.GetProperty("snapshots").GetArrayLength());

        var scanId = scan.GetProperty("scan").GetProperty("scanId").GetInt64();
        var decision = await Json(await Post(client, $"/api/gate/scans/{scanId}/decision", new { decision = "FULL" }));
        Assert.Equal("ENTERED", decision.GetProperty("state").GetString());

        // receipt for the driver, scanned in the same box at exit (pictures again)
        var tripId = scan.GetProperty("scan").GetProperty("tripId").GetInt64();
        var receipt = await Json(await Post(client, $"/api/gate/trips/{tripId}/receipt", new { }));
        Assert.StartsWith("TR1:", receipt.GetProperty("qrText").GetString());
        Assert.Equal("05", receipt.GetProperty("fleetNumber").GetString());
        var exit = await Json(await Post(client, "/api/gate/scan", new { gateId, qrText = receipt.GetProperty("qrText").GetString() }));
        Assert.Equal("EXIT", exit.GetProperty("kind").GetString());
        Assert.Equal("PENDING", exit.GetProperty("exit").GetProperty("outcome").GetString()); // seconds after entry
        Assert.Equal("EXIT_TOO_SOON", exit.GetProperty("exit").GetProperty("reasons")[0].GetString());
        Assert.Equal(4, exit.GetProperty("snapshots").GetArrayLength());
        var exitScanId = exit.GetProperty("exit").GetProperty("scanId").GetInt64();
        var exitDecision = await Json(await Post(client, $"/api/gate/exit/scans/{exitScanId}/decision", new { decision = "EMPTY" }));
        Assert.Equal("PENDING_REVIEW", exitDecision.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/review/queue")).StatusCode);
        await Post(client, "/api/auth/logout", new { });

        // the supervisor (here the admin) sees the trip, its pictures, and accepts it
        await Json(await Post(client, "/api/auth/login", new { username = "boss", secret = "a-long-password" }));
        var queue = await Json(await client.GetAsync("/api/review/queue"));
        Assert.Contains(queue.EnumerateArray(), q => q.GetProperty("tripId").GetInt64() == tripId);
        var detail = await Json(await client.GetAsync($"/api/trips/{tripId}"));
        Assert.Equal(8, detail.GetProperty("snapshots").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("scans").GetArrayLength());
        var firstPicture = detail.GetProperty("snapshots")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent,
            (await Post(client, $"/api/review/snapshots/{firstPicture}/labels", new { type = "LOAD", value = "FULL" })).StatusCode);
        var reviewed = await Json(await Post(client, $"/api/review/trips/{tripId}/decision", new { decision = "ACCEPT" }));
        Assert.Equal("CLOSED", reviewed.GetProperty("state").GetString());
        var dashboard = await Json(await client.GetAsync("/api/reports/dashboard"));
        Assert.Equal(2, dashboard.GetProperty("entries").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("exits").GetInt32());

        // everything above is in the audit chain, attributed to the signed-in users
        var actor = await db.ReadAsync(c => Dapper.SqlMapper.ExecuteScalarAsync<string>(c,
            "SELECT actor FROM tg.audit_log WHERE entity = 'gate_decisions' ORDER BY id LIMIT 1"));
        Assert.Equal($"user:{keeper}", actor);
        Assert.True((await new AuditService(db).VerifyAsync()).Ok);
    }
}
