package dz.truckgate.quarry.core

import dz.truckgate.ticket.EcdsaP256
import java.security.MessageDigest
import java.util.Base64
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec

/**
 * Enrollment QR shown on the factory admin screen:
 *   `TGE1|<server base url>|<one-time token>|<server public key, SPKI base64>`
 * Scanning it pins the server key (used to verify the signed configuration) and lets the
 * phone register its own keys with a token that works once and expires.
 */
data class EnrollmentInvite(val serverUrl: String, val token: String, val serverKeySpki: ByteArray) {
    companion object {
        private const val PREFIX = "TGE1"

        fun parse(text: String): EnrollmentInvite {
            val parts = text.trim().split('|')
            if (parts.size != 4 || parts[0] != PREFIX) throw IllegalArgumentException("not a TruckGate enrollment code")
            val url = parts[1].trimEnd('/')
            if (!(url.startsWith("http://") || url.startsWith("https://"))) throw IllegalArgumentException("bad server url")
            val token = parts[2]
            if (token.length < 20 || !token.all { it.isLetterOrDigit() || it == '-' || it == '_' })
                throw IllegalArgumentException("bad token")
            val key = try {
                Base64.getDecoder().decode(parts[3])
            } catch (e: IllegalArgumentException) {
                throw IllegalArgumentException("bad server key")
            }
            EcdsaP256.publicKeyFromSpki(key) // throws if not P-256
            return EnrollmentInvite(url, token, key)
        }
    }
}

/** Server-signed configuration: the signature covers the exact UTF-8 bytes of [payload]. */
object SignedConfig {
    fun verify(payload: String, signatureBase64: String, serverKeySpki: ByteArray): Boolean = try {
        EcdsaP256.verify(serverKeySpki, payload.toByteArray(Charsets.UTF_8), Base64.getDecoder().decode(signatureBase64))
    } catch (e: IllegalArgumentException) {
        false
    }
}

/**
 * Request authentication for phone → server calls, signed with the phone's *auth* key
 * (the ticket key needs a fingerprint for every use, so it is not used for background calls).
 *   message = `TGAUTH1|<deviceId>|<unix seconds>|<METHOD>|<path>|<sha256 hex of body>`
 */
object DeviceAuth {
    fun message(deviceId: Int, unixSeconds: Long, method: String, path: String, body: ByteArray): ByteArray {
        val bodyHash = MessageDigest.getInstance("SHA-256").digest(body).joinToString("") { "%02x".format(it) }
        return "TGAUTH1|$deviceId|$unixSeconds|${method.uppercase()}|$path|$bodyHash".toByteArray(Charsets.UTF_8)
    }
}

/** Operator PIN check against the hash delivered in the signed config: `pbkdf2-sha256$iter$saltB64$hashB64`. */
object PinHash {
    fun verify(pin: String, encoded: String): Boolean {
        val parts = encoded.split('$')
        if (parts.size != 4 || parts[0] != "pbkdf2-sha256") return false
        val iterations = parts[1].toIntOrNull() ?: return false
        if (iterations !in 10_000..2_000_000) return false
        return try {
            val salt = Base64.getDecoder().decode(parts[2])
            val expected = Base64.getDecoder().decode(parts[3])
            val spec = PBEKeySpec(pin.toCharArray(), salt, iterations, expected.size * 8)
            val actual = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
            MessageDigest.isEqual(actual, expected)
        } catch (e: IllegalArgumentException) {
            false
        }
    }

    /** Used by tests and by the server-side reference; the server is the one that creates hashes. */
    fun hash(pin: String, salt: ByteArray, iterations: Int = 100_000): String {
        val spec = PBEKeySpec(pin.toCharArray(), salt, iterations, 256)
        val h = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).encoded
        val b64 = Base64.getEncoder()
        return "pbkdf2-sha256$$iterations$${b64.encodeToString(salt)}$${b64.encodeToString(h)}"
    }
}
