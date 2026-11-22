package dz.truckgate.quarry.data

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

data class StoredTicket(
    val seq: Long,
    val ticketId: String,
    val truckId: Int,
    val driverId: Int,
    val operatorId: Int,
    val issuedAt: Long,
    val flags: Int,
    val bytes: ByteArray,
    val qrText: String,
    val shortCode: String,
    val printCount: Int,
    val voidedAt: Long?,
    val voidReason: String?,
)

/**
 * Local storage in the app's private directory. Tickets are never deleted: a ticket is kept
 * even if printing fails, and a void is recorded next to it with its own signature.
 */
class Store(context: Context) : SQLiteOpenHelper(context, "quarry.db", null, 1) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)")
        db.execSQL(
            """
            CREATE TABLE tickets (
                seq INTEGER PRIMARY KEY,
                ticket_id TEXT NOT NULL UNIQUE,
                truck_id INTEGER NOT NULL,
                driver_id INTEGER NOT NULL,
                operator_id INTEGER NOT NULL,
                issued_at INTEGER NOT NULL,
                flags INTEGER NOT NULL,
                bytes BLOB NOT NULL,
                qr_text TEXT NOT NULL,
                short_code TEXT NOT NULL,
                photo_path TEXT NOT NULL,
                photo_sha256 TEXT NOT NULL,
                driver_change_reason TEXT,
                print_count INTEGER NOT NULL DEFAULT 0,
                voided_at INTEGER,
                void_reason TEXT,
                void_signature BLOB,
                synced_at INTEGER,
                created_at INTEGER NOT NULL
            )
            """.trimIndent()
        )
        db.execSQL("CREATE INDEX tickets_truck ON tickets (truck_id, issued_at)")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = Unit

    // ------------------------------------------------------------------ key/value

    fun get(key: String): String? =
        readableDatabase.rawQuery("SELECT value FROM meta WHERE key = ?", arrayOf(key)).use {
            if (it.moveToFirst()) it.getString(0) else null
        }

    fun put(key: String, value: String) {
        writableDatabase.insertWithOnConflict("meta", null,
            ContentValues().apply { put("key", key); put("value", value) }, SQLiteDatabase.CONFLICT_REPLACE)
    }

    fun putAll(values: Map<String, String>) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            values.forEach { (k, v) -> put(k, v) }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    // ------------------------------------------------------------------ tickets

    fun nextSeq(): Long = longQuery("SELECT COALESCE(MAX(seq), 0) + 1 FROM tickets") ?: 1

    /** Last issue time for a truck, ignoring voided tickets (used for the 20-minute rule). */
    fun lastIssuedAtForTruck(truckId: Int): Long? =
        longQuery("SELECT MAX(issued_at) FROM tickets WHERE truck_id = ? AND voided_at IS NULL", truckId.toString())

    fun lastIssuedAt(): Long? = longQuery("SELECT MAX(issued_at) FROM tickets")

    fun countSince(epochSeconds: Long): Int =
        (longQuery("SELECT COUNT(*) FROM tickets WHERE issued_at >= ?", epochSeconds.toString()) ?: 0).toInt()

    fun unsyncedCount(): Int = (longQuery("SELECT COUNT(*) FROM tickets WHERE synced_at IS NULL") ?: 0).toInt()

    /** Fails (unique key) if the sequence number was already used: a number is never reused. */
    fun insertTicket(
        seq: Long, ticketId: String, truckId: Int, driverId: Int, operatorId: Int, issuedAt: Long, flags: Int,
        bytes: ByteArray, qrText: String, shortCode: String, photoPath: String, photoSha256: String,
        driverChangeReason: String?,
    ) {
        writableDatabase.insertOrThrow("tickets", null, ContentValues().apply {
            put("seq", seq); put("ticket_id", ticketId); put("truck_id", truckId); put("driver_id", driverId)
            put("operator_id", operatorId); put("issued_at", issuedAt); put("flags", flags); put("bytes", bytes)
            put("qr_text", qrText); put("short_code", shortCode); put("photo_path", photoPath)
            put("photo_sha256", photoSha256); put("driver_change_reason", driverChangeReason)
            put("created_at", System.currentTimeMillis() / 1000)
        })
    }

    fun markPrinted(seq: Long) {
        writableDatabase.execSQL("UPDATE tickets SET print_count = print_count + 1 WHERE seq = ?", arrayOf<Any?>(seq))
    }

    fun markVoided(seq: Long, at: Long, reason: String, signature: ByteArray) {
        writableDatabase.execSQL(
            "UPDATE tickets SET voided_at = ?, void_reason = ?, void_signature = ? WHERE seq = ? AND voided_at IS NULL",
            arrayOf<Any?>(at, reason, signature, seq)
        )
    }

    fun recent(limit: Int = 100): List<StoredTicket> =
        readableDatabase.rawQuery("$SELECT ORDER BY seq DESC LIMIT $limit", null).use { c ->
            buildList { while (c.moveToNext()) add(row(c)) }
        }

    fun ticket(seq: Long): StoredTicket? =
        readableDatabase.rawQuery("$SELECT WHERE seq = ?", arrayOf(seq.toString())).use { c ->
            if (c.moveToFirst()) row(c) else null
        }

    private fun row(c: android.database.Cursor) = StoredTicket(
        seq = c.getLong(0), ticketId = c.getString(1), truckId = c.getInt(2), driverId = c.getInt(3),
        operatorId = c.getInt(4), issuedAt = c.getLong(5), flags = c.getInt(6), bytes = c.getBlob(7),
        qrText = c.getString(8), shortCode = c.getString(9), printCount = c.getInt(10),
        voidedAt = if (c.isNull(11)) null else c.getLong(11), voidReason = c.getString(12),
    )

    private fun longQuery(sql: String, vararg args: String): Long? =
        readableDatabase.rawQuery(sql, args).use { if (it.moveToFirst() && !it.isNull(0)) it.getLong(0) else null }

    private companion object {
        const val SELECT = "SELECT seq, ticket_id, truck_id, driver_id, operator_id, issued_at, flags, bytes, " +
            "qr_text, short_code, print_count, voided_at, void_reason FROM tickets "
    }
}
