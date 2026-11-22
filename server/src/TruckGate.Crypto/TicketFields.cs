namespace TruckGate.Crypto;

/// <summary>Field values of a TruckGate ticket (ticket-spec SPEC.md §2).</summary>
public sealed record TicketFields(
    byte Kind,
    ushort DeviceId,
    uint Seq,
    uint IssuedAt,
    short ClockDelta,
    ushort TruckId,
    ushort DriverId,
    ushort OperatorId,
    int LatE6,
    int LonE6,
    byte Flags,
    ushort ConfigVersion,
    byte[] PhotoSha256)
{
    public const byte FormatVersion = 1;
    public const byte KindTransport = 0x01;
    public const byte KindDecapage = 0x02;

    public const byte FlagNoGps = 0x01;
    public const byte FlagOutsideGeofence = 0x02;
    public const byte FlagDriverChanged = 0x04;
    public const byte FlagClockMismatch = 0x08;
    public const byte FlagsReservedMask = 0xF0;

    public const int SignedLength = 63;
    public const int SignatureLength = 64;
    public const int TicketLength = SignedLength + SignatureLength;

    /// <summary>Human and database key, e.g. <c>Q1-000482</c>. Unique for the life of the system.</summary>
    public string TicketId => $"Q{DeviceId}-{Seq:D6}";

    public bool HasFlag(byte flag) => (Flags & flag) != 0;

    public bool Equals(TicketFields? other) =>
        other is not null &&
        Kind == other.Kind && DeviceId == other.DeviceId && Seq == other.Seq &&
        IssuedAt == other.IssuedAt && ClockDelta == other.ClockDelta &&
        TruckId == other.TruckId && DriverId == other.DriverId && OperatorId == other.OperatorId &&
        LatE6 == other.LatE6 && LonE6 == other.LonE6 && Flags == other.Flags &&
        ConfigVersion == other.ConfigVersion && PhotoSha256.AsSpan().SequenceEqual(other.PhotoSha256);

    public override int GetHashCode() => HashCode.Combine(DeviceId, Seq);
}

public sealed class TicketFormatException(string message) : FormatException(message);
