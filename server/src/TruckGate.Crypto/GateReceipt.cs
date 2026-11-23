using System.Buffers.Binary;
using System.Text;

namespace TruckGate.Crypto;

/// <summary>Gate receipt fields (ticket-spec SPEC.md §9).</summary>
public sealed record ReceiptFields(
    uint TripId, ushort GateId, uint EnteredAt, ushort TruckId, ushort TicketDeviceId, uint TicketSeq, byte Load)
{
    public const byte LoadUnknown = 0, LoadFull = 1, LoadPartial = 2, LoadEmpty = 3;

    public string TripNumber => $"T-{TripId:D6}";
}

public sealed record DecodedReceipt(ReceiptFields Fields, byte[] SignedPart, byte[] Signature);

/// <summary>TR1 gate receipt: 85 bytes, signed by the server key, printed for the driver, scanned at exit.</summary>
public static class GateReceipt
{
    public const string QrPrefix = "TR1:";
    public const byte Kind = 0x10;
    public const int SignedLength = 21;
    public const int Length = SignedLength + 64;

    public static byte[] EncodeSignedPart(ReceiptFields r)
    {
        if (r.Load > ReceiptFields.LoadEmpty) throw new TicketFormatException("unknown load value");
        var b = new byte[SignedLength];
        var s = b.AsSpan();
        s[0] = TicketFields.FormatVersion;
        s[1] = Kind;
        BinaryPrimitives.WriteUInt32BigEndian(s[2..], r.TripId);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], r.GateId);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], r.EnteredAt);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], r.TruckId);
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], r.TicketDeviceId);
        BinaryPrimitives.WriteUInt32BigEndian(s[16..], r.TicketSeq);
        s[20] = r.Load;
        return b;
    }

    /// <summary>Builds a signed receipt. <paramref name="sign"/> returns a raw r||s P-256 signature.</summary>
    public static byte[] Build(ReceiptFields r, Func<byte[], byte[]> sign)
    {
        var signed = EncodeSignedPart(r);
        var signature = sign(signed);
        if (signature.Length != 64) throw new InvalidOperationException("signature must be raw r||s (64 bytes)");
        return [.. signed, .. signature];
    }

    public static DecodedReceipt Decode(byte[] receipt)
    {
        if (receipt.Length != Length) throw new TicketFormatException($"length {receipt.Length} != {Length}");
        ReadOnlySpan<byte> s = receipt;
        if (s[0] != TicketFields.FormatVersion) throw new TicketFormatException($"unsupported version {s[0]}");
        if (s[1] != Kind) throw new TicketFormatException($"not a receipt (kind {s[1]})");
        if (s[20] > ReceiptFields.LoadEmpty) throw new TicketFormatException("unknown load value");
        var fields = new ReceiptFields(
            TripId: BinaryPrimitives.ReadUInt32BigEndian(s[2..]),
            GateId: BinaryPrimitives.ReadUInt16BigEndian(s[6..]),
            EnteredAt: BinaryPrimitives.ReadUInt32BigEndian(s[8..]),
            TruckId: BinaryPrimitives.ReadUInt16BigEndian(s[12..]),
            TicketDeviceId: BinaryPrimitives.ReadUInt16BigEndian(s[14..]),
            TicketSeq: BinaryPrimitives.ReadUInt32BigEndian(s[16..]),
            Load: s[20]);
        return new DecodedReceipt(fields, s[..SignedLength].ToArray(), s[SignedLength..].ToArray());
    }

    public static string ToQrText(byte[] receipt) => QrPrefix + Base45.Encode(receipt);

    public static byte[] FromQrText(string text)
    {
        if (!text.StartsWith(QrPrefix, StringComparison.Ordinal)) throw new TicketFormatException("missing TR1: prefix");
        return Base45.Decode(text[QrPrefix.Length..]);
    }

    /// <summary>Decodes and checks the server signature. Null when malformed or not signed by that key.</summary>
    public static DecodedReceipt? TryVerify(string qrText, byte[] serverKeySpki, out string? failure)
    {
        DecodedReceipt decoded;
        try
        {
            decoded = Decode(FromQrText(qrText));
        }
        catch (TicketFormatException)
        {
            failure = GateReasons.Malformed;
            return null;
        }
        if (!EcdsaP256.Verify(serverKeySpki, decoded.SignedPart, decoded.Signature))
        {
            failure = GateReasons.BadSignature;
            return null;
        }
        failure = null;
        return decoded;
    }
}

/// <summary>Void record (SPEC.md §8): the text the phone signs with its ticket key.</summary>
public static class VoidRecord
{
    public static byte[] Message(string ticketId, long unixSeconds, string reason)
    {
        var trimmed = reason.Trim();
        if (ticketId.Contains('|') || trimmed.Length == 0) throw new TicketFormatException("invalid void record");
        return Encoding.UTF8.GetBytes($"TGVOID1|{ticketId}|{unixSeconds}|{trimmed}");
    }

    public static bool Verify(byte[] ticketKeySpki, string ticketId, long unixSeconds, string reason, byte[] signature)
    {
        try
        {
            return EcdsaP256.Verify(ticketKeySpki, Message(ticketId, unixSeconds, reason), signature);
        }
        catch (TicketFormatException)
        {
            return false;
        }
    }
}
