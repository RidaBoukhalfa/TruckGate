using System.Text.Json;

namespace TruckGate.Crypto.Tests;

/// <summary>Gate receipt (receipt.json) and void record (void.json) conformance.</summary>
public class ReceiptVectorTests
{
    private static JsonElement Load(string file)
    {
        var env = Environment.GetEnvironmentVariable("TRUCKGATE_VECTORS");
        string? dirPath = string.IsNullOrEmpty(env) ? null : env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dirPath is null && dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "ticket-spec", "test-vectors");
            if (Directory.Exists(candidate)) dirPath = candidate;
        }
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dirPath!, file))).RootElement;
    }

    [Fact]
    public void Receipts_match_reference()
    {
        var root = Load("receipt.json");
        var serverKey = Convert.FromBase64String(root.GetProperty("server_key_spki_b64").GetString()!);
        int checks = 0;
        foreach (var c in root.GetProperty("valid").EnumerateArray())
        {
            var f = c.GetProperty("fields");
            var fields = new ReceiptFields(
                f.GetProperty("trip_id").GetUInt32(), f.GetProperty("gate_id").GetUInt16(),
                f.GetProperty("entered_at").GetUInt32(), f.GetProperty("truck_id").GetUInt16(),
                f.GetProperty("ticket_device_id").GetUInt16(), f.GetProperty("ticket_seq").GetUInt32(),
                f.GetProperty("load").GetByte());
            var bytes = Convert.FromHexString(c.GetProperty("receipt_hex").GetString()!);
            var qr = c.GetProperty("qr_text").GetString()!;

            Assert.Equal(bytes[..GateReceipt.SignedLength], GateReceipt.EncodeSignedPart(fields));
            Assert.Equal(qr, GateReceipt.ToQrText(bytes));
            Assert.Equal(bytes, GateReceipt.FromQrText(qr));
            var decoded = GateReceipt.TryVerify(qr, serverKey, out var failure);
            Assert.Null(failure);
            Assert.Equal(fields, decoded!.Fields);
            Assert.Equal(c.GetProperty("trip_number").GetString(), decoded.Fields.TripNumber);
            checks++;
        }

        Assert.Null(GateReceipt.TryVerify(root.GetProperty("forged").GetProperty("qr_text").GetString()!, serverKey, out var forged));
        Assert.Equal(GateReasons.BadSignature, forged);

        foreach (var c in root.GetProperty("malformed").EnumerateArray())
        {
            Assert.Null(GateReceipt.TryVerify(c.GetProperty("qr_text").GetString()!, serverKey, out var why));
            Assert.Equal(GateReasons.Malformed, why);
            checks++;
        }
        Assert.True(checks >= 10, $"only {checks} checks");
    }

    [Fact]
    public void Receipt_round_trip_with_a_fresh_key()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var fields = new ReceiptFields(9, 1, 1_800_000_000, 5, 1, 482, ReceiptFields.LoadPartial);
        var receipt = GateReceipt.Build(fields, data => key.SignData(data,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var decoded = GateReceipt.TryVerify(GateReceipt.ToQrText(receipt), spki, out var failure);
        Assert.Null(failure);
        Assert.Equal(fields, decoded!.Fields);

        receipt[5] ^= 1; // another trip id: signature no longer matches
        Assert.Null(GateReceipt.TryVerify(GateReceipt.ToQrText(receipt), spki, out failure));
        Assert.Equal(GateReasons.BadSignature, failure);
    }

    [Fact]
    public void Void_records_match_reference()
    {
        var root = Load("void.json");
        var keys = root.GetProperty("public_keys_spki_b64");
        int checks = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("ticket_id").GetString()!;
            var at = c.GetProperty("at").GetInt64();
            var reason = c.GetProperty("reason").GetString()!;
            var signature = Convert.FromHexString(c.GetProperty("signature_hex").GetString()!);
            Assert.Equal(Convert.FromHexString(c.GetProperty("message_hex").GetString()!), VoidRecord.Message(id, at, reason));
            foreach (var v in c.GetProperty("valid_for").EnumerateObject())
            {
                var spki = Convert.FromBase64String(keys.GetProperty(v.Name).GetString()!);
                Assert.Equal(v.Value.GetBoolean(), VoidRecord.Verify(spki, id, at, reason, signature));
                checks++;
            }
            Assert.False(VoidRecord.Verify(Convert.FromBase64String(keys.GetProperty("quarry-1").GetString()!), id, at + 1, reason, signature));
        }
        Assert.True(checks >= 6);
    }
}
