package dz.truckgate.ticket

/** Void record (SPEC.md §8): the text the phone signs with its ticket key to void a ticket. */
object VoidRecord {
    fun message(ticketId: String, unixSeconds: Long, reason: String): ByteArray {
        val trimmed = reason.trim()
        if ('|' in ticketId || trimmed.isEmpty()) throw TicketFormatException("invalid void record")
        return "TGVOID1|$ticketId|$unixSeconds|$trimmed".toByteArray(Charsets.UTF_8)
    }
}
