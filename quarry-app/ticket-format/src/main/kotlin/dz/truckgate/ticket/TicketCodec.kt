package dz.truckgate.ticket

import dz.truckgate.ticket.TicketFields.Companion.FLAGS_RESERVED_MASK
import dz.truckgate.ticket.TicketFields.Companion.FORMAT_VERSION
import dz.truckgate.ticket.TicketFields.Companion.KIND_DECAPAGE
import dz.truckgate.ticket.TicketFields.Companion.KIND_TRANSPORT
import dz.truckgate.ticket.TicketFields.Companion.SIGNED_LEN
import dz.truckgate.ticket.TicketFields.Companion.TICKET_LEN
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest

/** Byte layout, QR text and short code (SPEC.md §2, §4, §5). */
object TicketCodec {
    const val QR_PREFIX = "TG1:"
    private const val CROCKFORD32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    class Decoded(val fields: TicketFields, val signedPart: ByteArray, val signature: ByteArray)

    fun encodeSignedPart(f: TicketFields): ByteArray {
        if (f.kind != KIND_TRANSPORT && f.kind != KIND_DECAPAGE) throw TicketFormatException("unknown kind")
        if (f.flags and FLAGS_RESERVED_MASK != 0 || f.flags !in 0..0xFF) throw TicketFormatException("invalid flags")
        if (f.photoSha256.size != 32) throw TicketFormatException("photoSha256 must be 32 bytes")
        requireRange("deviceId", f.deviceId.toLong(), 0, 0xFFFF)
        requireRange("seq", f.seq, 0, 0xFFFF_FFFFL)
        requireRange("issuedAt", f.issuedAt, 0, 0xFFFF_FFFFL)
        requireRange("clockDelta", f.clockDelta.toLong(), Short.MIN_VALUE.toLong(), Short.MAX_VALUE.toLong())
        requireRange("truckId", f.truckId.toLong(), 0, 0xFFFF)
        requireRange("driverId", f.driverId.toLong(), 0, 0xFFFF)
        requireRange("operatorId", f.operatorId.toLong(), 0, 0xFFFF)
        requireRange("configVersion", f.configVersion.toLong(), 0, 0xFFFF)

        return ByteBuffer.allocate(SIGNED_LEN).order(ByteOrder.BIG_ENDIAN)
            .put(FORMAT_VERSION.toByte())
            .put(f.kind.toByte())
            .putShort(f.deviceId.toShort())
            .putInt(f.seq.toInt())
            .putInt(f.issuedAt.toInt())
            .putShort(f.clockDelta.toShort())
            .putShort(f.truckId.toShort())
            .putShort(f.driverId.toShort())
            .putShort(f.operatorId.toShort())
            .putInt(f.latE6)
            .putInt(f.lonE6)
            .put(f.flags.toByte())
            .putShort(f.configVersion.toShort())
            .put(f.photoSha256)
            .array()
    }

    fun decode(ticket: ByteArray): Decoded {
        if (ticket.size != TICKET_LEN) throw TicketFormatException("length ${ticket.size} != $TICKET_LEN")
        val b = ByteBuffer.wrap(ticket, 0, SIGNED_LEN).order(ByteOrder.BIG_ENDIAN)
        val version = b.get().toInt() and 0xFF
        if (version != FORMAT_VERSION) throw TicketFormatException("unsupported version $version")
        val kind = b.get().toInt() and 0xFF
        if (kind != KIND_TRANSPORT && kind != KIND_DECAPAGE) throw TicketFormatException("unknown kind $kind")
        val deviceId = b.short.toInt() and 0xFFFF
        val seq = b.int.toLong() and 0xFFFF_FFFFL
        val issuedAt = b.int.toLong() and 0xFFFF_FFFFL
        val clockDelta = b.short.toInt()
        val truckId = b.short.toInt() and 0xFFFF
        val driverId = b.short.toInt() and 0xFFFF
        val operatorId = b.short.toInt() and 0xFFFF
        val lat = b.int
        val lon = b.int
        val flags = b.get().toInt() and 0xFF
        if (flags and FLAGS_RESERVED_MASK != 0) throw TicketFormatException("reserved flag bits set")
        val cfg = b.short.toInt() and 0xFFFF
        val photo = ByteArray(32).also { b.get(it) }
        val fields = TicketFields(kind, deviceId, seq, issuedAt, clockDelta, truckId, driverId, operatorId,
            lat, lon, flags, cfg, photo)
        return Decoded(fields, ticket.copyOfRange(0, SIGNED_LEN), ticket.copyOfRange(SIGNED_LEN, TICKET_LEN))
    }

    fun toQrText(ticket: ByteArray): String = QR_PREFIX + Base45.encode(ticket)

    fun fromQrText(text: String): ByteArray {
        if (!text.startsWith(QR_PREFIX)) throw TicketFormatException("missing $QR_PREFIX prefix")
        return Base45.decode(text.substring(QR_PREFIX.length))
    }

    /** First 30 bits of SHA-256(ticket), 6 Crockford base32 chars as "XXX-XXX". */
    fun shortCode(ticket: ByteArray): String {
        val h = MessageDigest.getInstance("SHA-256").digest(ticket)
        val v = (ByteBuffer.wrap(h, 0, 4).int.toLong() and 0xFFFF_FFFFL) ushr 2
        val chars = CharArray(6) { i -> CROCKFORD32[((v ushr (25 - 5 * i)) and 0x1F).toInt()] }
        return String(chars, 0, 3) + "-" + String(chars, 3, 3)
    }

    private fun requireRange(name: String, v: Long, min: Long, max: Long) {
        if (v < min || v > max) throw TicketFormatException("$name out of range: $v")
    }
}
