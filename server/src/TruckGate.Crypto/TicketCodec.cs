using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TruckGate.Crypto;

public sealed record DecodedTicket(TicketFields Fields, byte[] SignedPart, byte[] Signature);

/// <summary>Byte layout, QR text and short code (ticket-spec SPEC.md §2, §4, §5).</summary>
public static class TicketCodec
{
    public const string QrPrefix = "TG1:";
    private const string Crockford32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static byte[] EncodeSignedPart(TicketFields f)
    {
        if (f.Kind is not (TicketFields.KindTransport or TicketFields.KindDecapage))
            throw new TicketFormatException("unknown kind");
        if ((f.Flags & TicketFields.FlagsReservedMask) != 0)
            throw new TicketFormatException("reserved flag bits set");
        if (f.PhotoSha256 is not { Length: 32 })
            throw new TicketFormatException("photo hash must be 32 bytes");

        var b = new byte[TicketFields.SignedLength];
        var s = b.AsSpan();
        s[0] = TicketFields.FormatVersion;
        s[1] = f.Kind;
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], f.DeviceId);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], f.Seq);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], f.IssuedAt);
        BinaryPrimitives.WriteInt16BigEndian(s[12..], f.ClockDelta);
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], f.TruckId);
        BinaryPrimitives.WriteUInt16BigEndian(s[16..], f.DriverId);
        BinaryPrimitives.WriteUInt16BigEndian(s[18..], f.OperatorId);
        BinaryPrimitives.WriteInt32BigEndian(s[20..], f.LatE6);
        BinaryPrimitives.WriteInt32BigEndian(s[24..], f.LonE6);
        s[28] = f.Flags;
        BinaryPrimitives.WriteUInt16BigEndian(s[29..], f.ConfigVersion);
        f.PhotoSha256.CopyTo(s[31..]);
        return b;
    }

    public static DecodedTicket Decode(byte[] ticket)
    {
        if (ticket.Length != TicketFields.TicketLength)
            throw new TicketFormatException($"length {ticket.Length} != {TicketFields.TicketLength}");
        ReadOnlySpan<byte> s = ticket;
        if (s[0] != TicketFields.FormatVersion) throw new TicketFormatException($"unsupported version {s[0]}");
        byte kind = s[1];
        if (kind is not (TicketFields.KindTransport or TicketFields.KindDecapage))
            throw new TicketFormatException($"unknown kind {kind}");
        byte flags = s[28];
        if ((flags & TicketFields.FlagsReservedMask) != 0) throw new TicketFormatException("reserved flag bits set");

        var fields = new TicketFields(
            Kind: kind,
            DeviceId: BinaryPrimitives.ReadUInt16BigEndian(s[2..]),
            Seq: BinaryPrimitives.ReadUInt32BigEndian(s[4..]),
            IssuedAt: BinaryPrimitives.ReadUInt32BigEndian(s[8..]),
            ClockDelta: BinaryPrimitives.ReadInt16BigEndian(s[12..]),
            TruckId: BinaryPrimitives.ReadUInt16BigEndian(s[14..]),
            DriverId: BinaryPrimitives.ReadUInt16BigEndian(s[16..]),
            OperatorId: BinaryPrimitives.ReadUInt16BigEndian(s[18..]),
            LatE6: BinaryPrimitives.ReadInt32BigEndian(s[20..]),
            LonE6: BinaryPrimitives.ReadInt32BigEndian(s[24..]),
            Flags: flags,
            ConfigVersion: BinaryPrimitives.ReadUInt16BigEndian(s[29..]),
            PhotoSha256: s.Slice(31, 32).ToArray());
        return new DecodedTicket(
            fields,
            s[..TicketFields.SignedLength].ToArray(),
            s[TicketFields.SignedLength..].ToArray());
    }

    public static string ToQrText(byte[] ticket) => QrPrefix + Base45.Encode(ticket);

    public static byte[] FromQrText(string text)
    {
        if (!text.StartsWith(QrPrefix, StringComparison.Ordinal))
            throw new TicketFormatException("missing TG1: prefix");
        return Base45.Decode(text[QrPrefix.Length..]);
    }

    /// <summary>First 30 bits of SHA-256(ticket), 6 Crockford base32 chars as "XXX-XXX".</summary>
    public static string ShortCode(byte[] ticket)
    {
        var h = SHA256.HashData(ticket);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(h) >> 2;
        var c = new char[6];
        for (int i = 0; i < 6; i++) c[i] = Crockford32[(int)((v >> (25 - 5 * i)) & 0x1F)];
        return new string(c, 0, 3) + "-" + new string(c, 3, 3);
    }
}
