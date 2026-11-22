package dz.truckgate.ticket

/** Field values of a TruckGate ticket (SPEC.md §2). Pure data, no Android dependency. */
data class TicketFields(
    val kind: Int,
    val deviceId: Int,
    val seq: Long,
    val issuedAt: Long,
    val clockDelta: Int,
    val truckId: Int,
    val driverId: Int,
    val operatorId: Int,
    val latE6: Int,
    val lonE6: Int,
    val flags: Int,
    val configVersion: Int,
    val photoSha256: ByteArray,
) {
    val ticketId: String get() = "Q$deviceId-" + seq.toString().padStart(6, '0')

    fun hasFlag(flag: Int): Boolean = flags and flag != 0

    override fun equals(other: Any?): Boolean =
        other is TicketFields &&
            kind == other.kind && deviceId == other.deviceId && seq == other.seq &&
            issuedAt == other.issuedAt && clockDelta == other.clockDelta &&
            truckId == other.truckId && driverId == other.driverId && operatorId == other.operatorId &&
            latE6 == other.latE6 && lonE6 == other.lonE6 && flags == other.flags &&
            configVersion == other.configVersion && photoSha256.contentEquals(other.photoSha256)

    override fun hashCode(): Int = ticketId.hashCode()

    companion object {
        const val FORMAT_VERSION = 1
        const val KIND_TRANSPORT = 0x01
        const val KIND_DECAPAGE = 0x02

        const val FLAG_NO_GPS = 0x01
        const val FLAG_OUTSIDE_GEOFENCE = 0x02
        const val FLAG_DRIVER_CHANGED = 0x04
        const val FLAG_CLOCK_MISMATCH = 0x08
        const val FLAGS_RESERVED_MASK = 0xF0

        const val SIGNED_LEN = 63
        const val SIGNATURE_LEN = 64
        const val TICKET_LEN = SIGNED_LEN + SIGNATURE_LEN
    }
}

class TicketFormatException(message: String) : IllegalArgumentException(message)
