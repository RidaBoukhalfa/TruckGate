namespace TruckGate.Crypto;

public enum DeviceKind { QuarryPhone, GateStation }

public sealed record RegisteredDevice(ushort DeviceId, DeviceKind Kind, byte[] PublicKeySpki, long? RevokedAt);

public sealed record TruckState(ushort TruckId, bool Active, long? LastEntryAt);

public sealed record GatePolicyParameters(
    int MinTravelSeconds = 600,
    int MaxValiditySeconds = 5400,
    int MinTruckGapSeconds = 1200,
    int FutureToleranceSeconds = 120);

/// <summary>Data the policy needs from the server (database in production, memory in tests).</summary>
public interface IGateLookup
{
    RegisteredDevice? FindDevice(ushort deviceId);
    TruckState? FindTruck(ushort truckId);
    bool IsUsed(string ticketId);
    bool IsVoided(string ticketId);
}

public enum GateOutcome { Accept, Pending, Reject }

public sealed record GateResult(
    GateOutcome Outcome,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Notes,
    string? TicketId,
    TicketFields? Fields);

/// <summary>Reason codes. Values are part of the spec and must match the test vectors.</summary>
public static class GateReasons
{
    public const string Malformed = "MALFORMED";
    public const string UnknownDevice = "UNKNOWN_DEVICE";
    public const string WrongDeviceKind = "WRONG_DEVICE_KIND";
    public const string BadSignature = "BAD_SIGNATURE";
    public const string WrongKind = "WRONG_KIND";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string IssuedBeforeRevocation = "ISSUED_BEFORE_REVOCATION";
    public const string AlreadyUsed = "ALREADY_USED";
    public const string Voided = "VOIDED";
    public const string UnknownTruck = "UNKNOWN_TRUCK";
    public const string TruckGap = "TRUCK_GAP";
    public const string IssuedInFuture = "ISSUED_IN_FUTURE";
    public const string TooEarly = "TOO_EARLY";
    public const string Expired = "EXPIRED";
    public const string NoGps = "NO_GPS";
    public const string OutsideGeofence = "OUTSIDE_GEOFENCE";
    public const string ClockMismatch = "CLOCK_MISMATCH";
    public const string DriverChanged = "DRIVER_CHANGED";
}

/// <summary>Gate verification policy (ticket-spec SPEC.md §6). The order of checks is normative.</summary>
public static class GatePolicy
{
    public static GateResult Evaluate(string qrText, long now, IGateLookup lookup, GatePolicyParameters p)
    {
        // ---- Stage A: stop at first failure
        DecodedTicket decoded;
        try
        {
            decoded = TicketCodec.Decode(TicketCodec.FromQrText(qrText));
        }
        catch (TicketFormatException)
        {
            return Reject(GateReasons.Malformed, null, null);
        }

        var f = decoded.Fields;
        var id = f.TicketId;
        var device = lookup.FindDevice(f.DeviceId);
        if (device is null) return Reject(GateReasons.UnknownDevice, id, f);
        if (device.Kind != DeviceKind.QuarryPhone) return Reject(GateReasons.WrongDeviceKind, id, f);
        if (!EcdsaP256.Verify(device.PublicKeySpki, decoded.SignedPart, decoded.Signature))
            return Reject(GateReasons.BadSignature, id, f);
        if (f.Kind != TicketFields.KindTransport) return Reject(GateReasons.WrongKind, id, f);

        // ---- Stage B: collect everything
        var reject = new List<string>();
        var pending = new List<string>();
        var notes = new List<string>();

        if (device.RevokedAt is { } revokedAt)
        {
            if (f.IssuedAt >= revokedAt) reject.Add(GateReasons.DeviceRevoked);
            else pending.Add(GateReasons.IssuedBeforeRevocation);
        }

        // Single use is keyed by ticket ID, never by bytes (ECDSA signatures are malleable).
        if (lookup.IsUsed(id)) reject.Add(GateReasons.AlreadyUsed);
        if (lookup.IsVoided(id)) reject.Add(GateReasons.Voided);

        var truck = lookup.FindTruck(f.TruckId);
        if (truck is null || !truck.Active)
            pending.Add(GateReasons.UnknownTruck);
        else if (truck.LastEntryAt is { } last && now - last < p.MinTruckGapSeconds)
            reject.Add(GateReasons.TruckGap);

        long age = now - f.IssuedAt;
        if (f.IssuedAt > now + p.FutureToleranceSeconds) pending.Add(GateReasons.IssuedInFuture);
        else if (age < p.MinTravelSeconds) pending.Add(GateReasons.TooEarly);
        else if (age > p.MaxValiditySeconds) pending.Add(GateReasons.Expired);

        if (f.HasFlag(TicketFields.FlagNoGps)) pending.Add(GateReasons.NoGps);
        if (f.HasFlag(TicketFields.FlagOutsideGeofence)) pending.Add(GateReasons.OutsideGeofence);
        if (f.HasFlag(TicketFields.FlagClockMismatch)) pending.Add(GateReasons.ClockMismatch);
        if (f.HasFlag(TicketFields.FlagDriverChanged)) notes.Add(GateReasons.DriverChanged);

        if (reject.Count > 0) return new GateResult(GateOutcome.Reject, reject, notes, id, f);
        if (pending.Count > 0) return new GateResult(GateOutcome.Pending, pending, notes, id, f);
        return new GateResult(GateOutcome.Accept, [], notes, id, f);
    }

    private static GateResult Reject(string reason, string? id, TicketFields? f) =>
        new(GateOutcome.Reject, [reason], [], id, f);
}
