package dz.truckgate.ticket

import java.math.BigInteger
import java.security.KeyFactory
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.X509EncodedKeySpec

/**
 * ECDSA P-256 / SHA-256 helpers. Tickets carry raw r||s (64 bytes); Android Keystore and the
 * JDK produce/consume DER, so we convert at the boundary.
 */
object EcdsaP256 {
    private const val P256_ORDER_BITS = 256

    /** DER SEQUENCE{INTEGER r, INTEGER s} -> 64-byte r||s. */
    fun derToRaw(der: ByteArray): ByteArray {
        var i = 0
        fun readLen(): Int {
            val first = der[i++].toInt() and 0xFF
            if (first < 0x80) return first
            val n = first and 0x7F
            if (n !in 1..2) throw TicketFormatException("bad DER length")
            var len = 0
            repeat(n) { len = (len shl 8) or (der[i++].toInt() and 0xFF) }
            return len
        }
        fun readInt(): BigInteger {
            if (der[i++].toInt() != 0x02) throw TicketFormatException("expected DER INTEGER")
            val len = readLen()
            val v = BigInteger(1, der.copyOfRange(i, i + len)); i += len
            return v
        }
        try {
            if (der[i++].toInt() != 0x30) throw TicketFormatException("expected DER SEQUENCE")
            readLen()
            val r = readInt(); val s = readInt()
            if (i != der.size) throw TicketFormatException("trailing DER bytes")
            return toFixed32(r) + toFixed32(s)
        } catch (e: IndexOutOfBoundsException) {
            throw TicketFormatException("truncated DER")
        }
    }

    /** 64-byte r||s -> DER. */
    fun rawToDer(raw: ByteArray): ByteArray {
        if (raw.size != 64) throw TicketFormatException("signature must be 64 bytes")
        fun derInt(part: ByteArray): ByteArray {
            val v = BigInteger(1, part).toByteArray() // minimal two's complement, positive
            return byteArrayOf(0x02, v.size.toByte()) + v
        }
        val body = derInt(raw.copyOfRange(0, 32)) + derInt(raw.copyOfRange(32, 64))
        return byteArrayOf(0x30, body.size.toByte()) + body
    }

    fun publicKeyFromSpki(spkiDer: ByteArray): ECPublicKey {
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spkiDer)) as ECPublicKey
        if (key.params.order.bitLength() != P256_ORDER_BITS) throw TicketFormatException("not a P-256 key")
        return key
    }

    /** Verifies raw r||s over [data]. Never throws for bad signatures; returns false. */
    fun verify(spkiDer: ByteArray, data: ByteArray, rawSignature: ByteArray): Boolean = try {
        val key = publicKeyFromSpki(spkiDer)
        val r = BigInteger(1, rawSignature.copyOfRange(0, 32))
        val s = BigInteger(1, rawSignature.copyOfRange(32, 64))
        val n = key.params.order
        if (rawSignature.size != 64 || r.signum() == 0 || s.signum() == 0 || r >= n || s >= n) {
            false
        } else {
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(key); update(data); verify(rawToDer(rawSignature))
            }
        }
    } catch (e: Exception) {
        false
    }

    private fun toFixed32(v: BigInteger): ByteArray {
        val b = v.toByteArray()
        return when {
            b.size == 32 -> b
            b.size == 33 && b[0].toInt() == 0 -> b.copyOfRange(1, 33)
            b.size < 32 -> ByteArray(32 - b.size) + b
            else -> throw TicketFormatException("integer too large for P-256")
        }
    }
}
