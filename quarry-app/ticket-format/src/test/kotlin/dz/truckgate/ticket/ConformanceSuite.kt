package dz.truckgate.ticket

import java.io.File
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64

/**
 * Runs the shared vectors in ticket-spec/test-vectors against this implementation.
 * Used by the JUnit test (CI) and by LocalRunner (no test framework needed).
 */
class ConformanceSuite(private val vectorsDir: File) {
    val failures = mutableListOf<String>()
    var passed = 0
        private set

    private fun check(name: String, ok: Boolean, detail: () -> String = { "" }) {
        if (ok) passed++ else failures += "$name ${detail()}"
    }

    @Suppress("UNCHECKED_CAST")
    private fun load(file: String): Map<String, Any?> =
        MiniJson.parse(File(vectorsDir, file).readText()) as Map<String, Any?>

    @Suppress("UNCHECKED_CAST")
    private fun cases(file: String) = load(file)["cases"] as List<Map<String, Any?>>

    fun runAll(): ConformanceSuite {
        codec(); signatures(); malformed(); voids(); issuanceRoundTrip(); derConversion()
        return this
    }

    private fun codec() {
        for (c in cases("codec.json")) {
            val name = "codec/${c["name"]}"
            @Suppress("UNCHECKED_CAST") val f = c["fields"] as Map<String, Any?>
            val fields = TicketFields(
                kind = (f["kind"] as Long).toInt(), deviceId = (f["device_id"] as Long).toInt(),
                seq = f["seq"] as Long, issuedAt = f["issued_at"] as Long,
                clockDelta = (f["clock_delta"] as Long).toInt(), truckId = (f["truck_id"] as Long).toInt(),
                driverId = (f["driver_id"] as Long).toInt(), operatorId = (f["operator_id"] as Long).toInt(),
                latE6 = (f["lat_e6"] as Long).toInt(), lonE6 = (f["lon_e6"] as Long).toInt(),
                flags = (f["flags"] as Long).toInt(), configVersion = (f["config_version"] as Long).toInt(),
                photoSha256 = hex(f["photo_sha256"] as String),
            )
            val signed = TicketCodec.encodeSignedPart(fields)
            check("$name/encode", signed.toHex() == c["signed_hex"]) { signed.toHex() }
            val ticket = hex(c["ticket_hex"] as String)
            check("$name/qr", TicketCodec.toQrText(ticket) == c["qr_text"])
            check("$name/qr-decode", TicketCodec.fromQrText(c["qr_text"] as String).contentEquals(ticket))
            check("$name/short", TicketCodec.shortCode(ticket) == c["short_code"]) { TicketCodec.shortCode(ticket) }
            val d = TicketCodec.decode(ticket)
            check("$name/decode", d.fields == fields)
            check("$name/id", d.fields.ticketId == c["ticket_id"])
            @Suppress("UNCHECKED_CAST")
            val keys = load("codec.json")["public_keys_spki_b64"] as Map<String, String>
            val spki = Base64.getDecoder().decode(keys[c["signer"] as String])
            check("$name/verify", EcdsaP256.verify(spki, d.signedPart, d.signature))
        }
    }

    private fun signatures() {
        for (c in cases("signature.json")) {
            val ticket = hex(c["ticket_hex"] as String)
            val spki = Base64.getDecoder().decode(c["public_key_spki_b64"] as String)
            val ok = EcdsaP256.verify(spki, ticket.copyOfRange(0, 63), ticket.copyOfRange(63, 127))
            check("signature/${c["name"]}", ok == c["expected_valid"]) { "got $ok" }
        }
    }

    private fun malformed() {
        for (c in cases("malformed.json")) {
            val rejected = try {
                TicketCodec.decode(TicketCodec.fromQrText(c["qr_text"] as String)); false
            } catch (e: TicketFormatException) { true }
            check("malformed/${c["name"]}", rejected)
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun voids() {
        val keys = load("void.json")["public_keys_spki_b64"] as Map<String, String>
        for (c in cases("void.json")) {
            val name = "void/${c["name"]}"
            val msg = VoidRecord.message(c["ticket_id"] as String, c["at"] as Long, c["reason"] as String)
            check("$name/message", msg.toHex() == c["message_hex"]) { msg.toHex() }
            val sig = hex(c["signature_hex"] as String)
            for ((label, expected) in c["valid_for"] as Map<String, Any?>) {
                val ok = EcdsaP256.verify(Base64.getDecoder().decode(keys[label]), msg, sig)
                check("$name/$label", ok == expected) { "got $ok" }
            }
        }
    }

    /** Issue with a fresh software key (stands in for Keystore) and verify end to end. */
    private fun issuanceRoundTrip() {
        val kp = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val signer = object : TicketSigner {
            override val publicKeySpki: ByteArray = kp.public.encoded
            override fun signDer(data: ByteArray): ByteArray =
                Signature.getInstance("SHA256withECDSA").run { initSign(kp.private); update(data); sign() }
        }
        repeat(50) { n ->
            val fields = TicketFields(TicketFields.KIND_TRANSPORT, 1, n.toLong(), 1_795_000_000L + n, 0, 5, 12, 2,
                35_000_000, -1_000_000, 0, 1, ByteArray(32) { it.toByte() })
            val issued = TicketIssuer.issue(fields, signer)
            val back = TicketCodec.decode(TicketCodec.fromQrText(issued.qrText))
            check("issue/$n/roundtrip", back.fields == fields)
            check("issue/$n/verify", EcdsaP256.verify(signer.publicKeySpki, back.signedPart, back.signature))
            check("issue/$n/qr-length", issued.qrText.length == 195)
        }
    }

    private fun derConversion() {
        // Small r/s values exercise padding and the leading-zero rule.
        val raw = ByteArray(64).also { it[31] = 1; it[32] = 0x80.toByte() }
        check("der/roundtrip", EcdsaP256.derToRaw(EcdsaP256.rawToDer(raw)).contentEquals(raw))
    }

    companion object {
        fun hex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }

        fun defaultVectorsDir(): File =
            File(System.getProperty("truckgate.vectors") ?: "../../ticket-spec/test-vectors")
    }
}

/** Entry point for running the suite without a test framework. */
object LocalRunner {
    @JvmStatic
    fun main(args: Array<String>) {
        val dir = if (args.isNotEmpty()) File(args[0]) else ConformanceSuite.defaultVectorsDir()
        val suite = ConformanceSuite(dir).runAll()
        suite.failures.forEach { println("FAIL $it") }
        println("passed=${suite.passed} failed=${suite.failures.size}")
        if (suite.failures.isNotEmpty()) kotlin.system.exitProcess(1)
    }
}
