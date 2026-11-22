using System.Text.Json;

namespace TruckGate.Crypto.Tests;

/// <summary>Runs the shared conformance vectors from ticket-spec/test-vectors.</summary>
public class VectorTests
{
    private static readonly string VectorsDir = FindVectorsDir();

    private static string FindVectorsDir()
    {
        var env = Environment.GetEnvironmentVariable("TRUCKGATE_VECTORS");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ticket-spec", "test-vectors");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("ticket-spec/test-vectors not found; set TRUCKGATE_VECTORS");
    }

    private static JsonElement Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorsDir, file))).RootElement;

    private static IEnumerable<JsonElement> Cases(string file) => Load(file).GetProperty("cases").EnumerateArray();

    private static byte[] Hex(JsonElement e) => Convert.FromHexString(e.GetString()!);

    private static void AssertNoFailures(List<string> failures, int minChecks, int checks)
    {
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(checks >= minChecks, $"only {checks} checks ran");
    }

    [Fact]
    public void Codec_matches_reference()
    {
        var root = Load("codec.json");
        var keys = root.GetProperty("public_keys_spki_b64");
        var failures = new List<string>();
        int checks = 0;
        void Check(string name, bool ok) { checks++; if (!ok) failures.Add(name); }

        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var name = "codec/" + c.GetProperty("name").GetString();
            var f = c.GetProperty("fields");
            var fields = new TicketFields(
                Kind: f.GetProperty("kind").GetByte(),
                DeviceId: f.GetProperty("device_id").GetUInt16(),
                Seq: f.GetProperty("seq").GetUInt32(),
                IssuedAt: f.GetProperty("issued_at").GetUInt32(),
                ClockDelta: f.GetProperty("clock_delta").GetInt16(),
                TruckId: f.GetProperty("truck_id").GetUInt16(),
                DriverId: f.GetProperty("driver_id").GetUInt16(),
                OperatorId: f.GetProperty("operator_id").GetUInt16(),
                LatE6: f.GetProperty("lat_e6").GetInt32(),
                LonE6: f.GetProperty("lon_e6").GetInt32(),
                Flags: f.GetProperty("flags").GetByte(),
                ConfigVersion: f.GetProperty("config_version").GetUInt16(),
                PhotoSha256: Hex(f.GetProperty("photo_sha256")));

            var signed = TicketCodec.EncodeSignedPart(fields);
            Check(name + "/encode", Convert.ToHexStringLower(signed) == c.GetProperty("signed_hex").GetString());

            var ticket = Hex(c.GetProperty("ticket_hex"));
            var qr = c.GetProperty("qr_text").GetString()!;
            Check(name + "/qr", TicketCodec.ToQrText(ticket) == qr);
            Check(name + "/qr-decode", TicketCodec.FromQrText(qr).AsSpan().SequenceEqual(ticket));
            Check(name + "/short", TicketCodec.ShortCode(ticket) == c.GetProperty("short_code").GetString());

            var d = TicketCodec.Decode(ticket);
            Check(name + "/decode", d.Fields.Equals(fields));
            Check(name + "/id", d.Fields.TicketId == c.GetProperty("ticket_id").GetString());

            var spki = Convert.FromBase64String(keys.GetProperty(c.GetProperty("signer").GetString()!).GetString()!);
            Check(name + "/verify", EcdsaP256.Verify(spki, d.SignedPart, d.Signature));
        }
        AssertNoFailures(failures, 28, checks);
    }

    [Fact]
    public void Signatures_match_reference()
    {
        var failures = new List<string>();
        int checks = 0;
        foreach (var c in Cases("signature.json"))
        {
            checks++;
            var ticket = Hex(c.GetProperty("ticket_hex"));
            var spki = Convert.FromBase64String(c.GetProperty("public_key_spki_b64").GetString()!);
            bool ok = EcdsaP256.Verify(spki, ticket[..63], ticket[63..]);
            if (ok != c.GetProperty("expected_valid").GetBoolean())
                failures.Add($"signature/{c.GetProperty("name").GetString()} got {ok}");
        }
        AssertNoFailures(failures, 7, checks);
    }

    [Fact]
    public void Malformed_tickets_are_rejected_at_decode()
    {
        var failures = new List<string>();
        int checks = 0;
        foreach (var c in Cases("malformed.json"))
        {
            checks++;
            try
            {
                TicketCodec.Decode(TicketCodec.FromQrText(c.GetProperty("qr_text").GetString()!));
                failures.Add("malformed/" + c.GetProperty("name").GetString() + " decoded");
            }
            catch (TicketFormatException)
            {
                // expected
            }
        }
        AssertNoFailures(failures, 12, checks);
    }

    [Fact]
    public void Gate_policy_matches_reference()
    {
        var failures = new List<string>();
        int checks = 0;
        foreach (var c in Cases("policy.json"))
        {
            checks++;
            var name = c.GetProperty("name").GetString();
            var ctx = c.GetProperty("context");
            var lookup = InMemoryLookup.From(ctx);
            var pp = ctx.GetProperty("policy");
            var parameters = new GatePolicyParameters(
                pp.GetProperty("min_travel_s").GetInt32(),
                pp.GetProperty("max_validity_s").GetInt32(),
                pp.GetProperty("min_truck_gap_s").GetInt32(),
                pp.GetProperty("future_tolerance_s").GetInt32());

            var r = GatePolicy.Evaluate(c.GetProperty("qr_text").GetString()!, ctx.GetProperty("now").GetInt64(),
                lookup, parameters);

            var e = c.GetProperty("expected");
            var expectedOutcome = e.GetProperty("outcome").GetString() switch
            {
                "ACCEPT" => GateOutcome.Accept,
                "PENDING" => GateOutcome.Pending,
                "REJECT" => GateOutcome.Reject,
                var x => throw new InvalidDataException(x),
            };
            var expectedReasons = e.GetProperty("reasons").EnumerateArray().Select(x => x.GetString()!).ToList();
            var expectedNotes = e.GetProperty("notes").EnumerateArray().Select(x => x.GetString()!).ToList();
            var expectedId = e.GetProperty("ticket_id").ValueKind == JsonValueKind.Null
                ? null : e.GetProperty("ticket_id").GetString();

            if (r.Outcome != expectedOutcome || !r.Reasons.SequenceEqual(expectedReasons) ||
                !r.Notes.SequenceEqual(expectedNotes) || r.TicketId != expectedId)
            {
                failures.Add($"policy/{name}: got {r.Outcome} [{string.Join(",", r.Reasons)}] " +
                             $"notes [{string.Join(",", r.Notes)}] id {r.TicketId}");
            }
        }
        AssertNoFailures(failures, 29, checks);
    }

    private sealed class InMemoryLookup : IGateLookup
    {
        private readonly Dictionary<ushort, RegisteredDevice> _devices = new();
        private readonly Dictionary<ushort, TruckState> _trucks = new();
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly HashSet<string> _voided = new(StringComparer.Ordinal);

        public static InMemoryLookup From(JsonElement ctx)
        {
            var l = new InMemoryLookup();
            foreach (var d in ctx.GetProperty("devices").EnumerateArray())
            {
                var id = d.GetProperty("device_id").GetUInt16();
                var kind = d.GetProperty("kind").GetString() switch
                {
                    "QUARRY_PHONE" => DeviceKind.QuarryPhone,
                    "GATE_STATION" => DeviceKind.GateStation,
                    var x => throw new InvalidDataException(x),
                };
                var revoked = d.GetProperty("revoked_at");
                l._devices[id] = new RegisteredDevice(id, kind,
                    Convert.FromBase64String(d.GetProperty("public_key_spki_b64").GetString()!),
                    revoked.ValueKind == JsonValueKind.Null ? null : revoked.GetInt64());
            }
            foreach (var t in ctx.GetProperty("trucks").EnumerateArray())
            {
                var id = t.GetProperty("truck_id").GetUInt16();
                var last = t.GetProperty("last_entry_at");
                l._trucks[id] = new TruckState(id, t.GetProperty("active").GetBoolean(),
                    last.ValueKind == JsonValueKind.Null ? null : last.GetInt64());
            }
            foreach (var u in ctx.GetProperty("used_ticket_ids").EnumerateArray()) l._used.Add(u.GetString()!);
            foreach (var v in ctx.GetProperty("voided_ticket_ids").EnumerateArray()) l._voided.Add(v.GetString()!);
            return l;
        }

        public RegisteredDevice? FindDevice(ushort deviceId) => _devices.GetValueOrDefault(deviceId);
        public TruckState? FindTruck(ushort truckId) => _trucks.GetValueOrDefault(truckId);
        public bool IsUsed(string ticketId) => _used.Contains(ticketId);
        public bool IsVoided(string ticketId) => _voided.Contains(ticketId);
    }
}
