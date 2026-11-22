package dz.truckgate.ticket

/** Base45 (RFC 9285). Strict decoder: rejects unknown chars, bad length and overflowing chunks. */
object Base45 {
    private const val ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:"

    fun encode(data: ByteArray): String {
        val sb = StringBuilder(data.size / 2 * 3 + 2)
        var i = 0
        while (i + 1 < data.size) {
            var n = (data[i].toInt() and 0xFF) * 256 + (data[i + 1].toInt() and 0xFF)
            val c = n % 45; n /= 45
            val d = n % 45; val e = n / 45
            sb.append(ALPHABET[c]).append(ALPHABET[d]).append(ALPHABET[e])
            i += 2
        }
        if (data.size % 2 == 1) {
            val n = data.last().toInt() and 0xFF
            sb.append(ALPHABET[n % 45]).append(ALPHABET[n / 45])
        }
        return sb.toString()
    }

    fun decode(text: String): ByteArray {
        val vals = IntArray(text.length) { idx ->
            val v = ALPHABET.indexOf(text[idx])
            if (v < 0) throw TicketFormatException("invalid base45 character")
            v
        }
        if (vals.size % 3 == 1) throw TicketFormatException("invalid base45 length")
        val out = java.io.ByteArrayOutputStream(vals.size / 3 * 2 + 1)
        var i = 0
        while (i + 2 < vals.size) {
            val n = vals[i] + vals[i + 1] * 45 + vals[i + 2] * 45 * 45
            if (n > 0xFFFF) throw TicketFormatException("base45 triplet overflow")
            out.write(n shr 8); out.write(n and 0xFF)
            i += 3
        }
        if (vals.size % 3 == 2) {
            val n = vals[vals.size - 2] + vals[vals.size - 1] * 45
            if (n > 0xFF) throw TicketFormatException("base45 pair overflow")
            out.write(n)
        }
        return out.toByteArray()
    }
}
