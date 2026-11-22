package dz.truckgate.ticket

/**
 * Abstraction over the device signing key. The Android app implements this with an
 * Android Keystore key (hardware-backed, non-exportable, user-authentication required).
 */
interface TicketSigner {
    /** SubjectPublicKeyInfo DER of the signing key, registered on the server at enrollment. */
    val publicKeySpki: ByteArray

    /** Returns a DER-encoded ECDSA P-256 / SHA-256 signature over [data] (Keystore's native format). */
    fun signDer(data: ByteArray): ByteArray
}

/** Result of issuing a ticket: the bytes to store, the text to print as QR and the human codes. */
class IssuedTicket(val fields: TicketFields, val bytes: ByteArray) {
    val qrText: String get() = TicketCodec.toQrText(bytes)
    val shortCode: String get() = TicketCodec.shortCode(bytes)
    val ticketId: String get() = fields.ticketId
}

object TicketIssuer {
    /**
     * Encodes, signs and self-verifies a ticket. Self-verification catches a broken key or
     * conversion bug on the phone before anything is printed.
     */
    fun issue(fields: TicketFields, signer: TicketSigner): IssuedTicket {
        val signed = TicketCodec.encodeSignedPart(fields)
        val raw = EcdsaP256.derToRaw(signer.signDer(signed))
        val bytes = signed + raw
        check(EcdsaP256.verify(signer.publicKeySpki, signed, raw)) { "self-verification failed" }
        return IssuedTicket(fields, bytes)
    }
}
