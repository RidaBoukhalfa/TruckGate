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

/** A ticket as it is sent to the server. */
data class SyncRow(
    val seq: Long,
    val ticketId: String,
    val bytes: ByteArray,
    val printCount: Int,
    val driverChangeReason: String?,
    val voidedAt: Long?,
    val voidReason: String?,
    val voidSignature: ByteArray?,
    val changeCount: Int,
)

/** Keys of the meta table, shared by the screens and the background sync. */
object Keys {
    const val K_DEVICE_ID = "device_id"
    const val K_SERVER_URL = "server_url"
    const val K_SERVER_KEY = "server_key"
    const val K_CONFIG = "config_payload"
    const val K_CONFIG_SIG = "config_signature"
    const val K_LAST_SYNC = "last_sync_at"
    const val K_LAST_SYNC_ERROR = "last_sync_error"
    const val K_REVOKED = "revoked"
    const val K_PRINTER = "printer_address"
    const val K_PAPER = "paper_width"
}

/**
 * Local storage in the app's private directory. Tickets are never deleted: a ticket is kept
 * even if printing fails, and a void is recorded next to it with its own signature.
 * One instance per process (screens and background sync share it).
 */
class Store private constructor(context: Context) : SQLiteOpenHelper(context, "quarry.db", null, 2) {

    companion object {
        @Volatile private var instance: Store? = null

        fun get(context: Context): Store =
            instance ?: synchronized(this) { instance ?: Store(context.applicationContext).also { instance = it } }
    }

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
                created_at INTEGER NOT NULL,
                sync_dirty INTEGER NOT NULL DEFAULT 1,
                change_count INTEGER NOT NULL DEFAULT 0,
                photo_wanted INTEGER NOT NULL DEFAULT 1,
                sync_error TEXT
            )
            """.trimIndent()
        )
        db.execSQL("CREATE INDEX tickets_truck ON tickets (truck_id, issued_at)")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        if (oldVersion < 2) {
            db.execSQL("ALTER TABLE tickets ADD COLUMN sync_dirty INTEGER NOT NULL DEFAULT 1")
            db.execSQL("ALTER TABLE tickets ADD COLUMN change_count INTEGER NOT NULL DEFAULT 0")
            db.execSQL("ALTER TABLE tickets ADD COLUMN photo_wanted INTEGER NOT NULL DEFAULT 1")
            db.execSQL("ALTER TABLE tickets ADD COLUMN sync_error TEXT")
        }
    }

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

    fun unsyncedCount(): Int = (longQuery("SELECT COUNT(*) FROM tickets WHERE sync_dirty = 1") ?: 0).toInt()

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
        writableDatabase.execSQL(
            "UPDATE tickets SET print_count = print_count + 1, sync_dirty = 1, change_count = change_count + 1 WHERE seq = ?",
            arrayOf<Any?>(seq)
        )
    }

    fun markVoided(seq: Long, at: Long, reason: String, signature: ByteArray) {
        writableDatabase.execSQL(
            "UPDATE tickets SET voided_at = ?, void_reason = ?, void_signature = ?, sync_dirty = 1, " +
                "change_count = change_count + 1 WHERE seq = ? AND voided_at IS NULL",
            arrayOf<Any?>(at, reason, signature, seq)
        )
    }

    // ------------------------------------------------------------------ sync

    fun pendingSync(limit: Int): List<SyncRow> =
        readableDatabase.rawQuery(
            "SELECT seq, ticket_id, bytes, print_count, driver_change_reason, voided_at, void_reason, void_signature, " +
                "change_count FROM tickets WHERE sync_dirty = 1 ORDER BY seq LIMIT $limit", null
        ).use { c ->
            buildList {
                while (c.moveToNext()) add(
                    SyncRow(
                        seq = c.getLong(0), ticketId = c.getString(1), bytes = c.getBlob(2), printCount = c.getInt(3),
                        driverChangeReason = c.getString(4), voidedAt = if (c.isNull(5)) null else c.getLong(5),
                        voidReason = c.getString(6), voidSignature = if (c.isNull(7)) null else c.getBlob(7),
                        changeCount = c.getInt(8),
                    )
                )
            }
        }

    /** Clears the flag only if nothing changed on the ticket since it was read for sending. */
    fun markSynced(seq: Long, changeCount: Int, at: Long, photoWanted: Boolean) {
        writableDatabase.execSQL(
            "UPDATE tickets SET sync_dirty = CASE WHEN change_count = ? THEN 0 ELSE 1 END, synced_at = ?, " +
                "photo_wanted = ?, sync_error = NULL WHERE seq = ?",
            arrayOf<Any?>(changeCount, at, if (photoWanted) 1 else 0, seq)
        )
    }

    fun markSyncError(seq: Long, error: String) {
        writableDatabase.execSQL("UPDATE tickets SET sync_error = ? WHERE seq = ?", arrayOf<Any?>(error, seq))
    }

    /** Synced tickets whose photo the server still wants: (seq, photo path). */
    fun photosToUpload(limit: Int): List<Pair<Long, String>> =
        readableDatabase.rawQuery(
            "SELECT seq, photo_path FROM tickets WHERE synced_at IS NOT NULL AND photo_wanted = 1 ORDER BY seq LIMIT $limit", null
        ).use { c -> buildList { while (c.moveToNext()) add(c.getLong(0) to c.getString(1)) } }

    fun markPhotoDone(seq: Long) {
        writableDatabase.execSQL("UPDATE tickets SET photo_wanted = 0 WHERE seq = ?", arrayOf<Any?>(seq))
    }

    fun rejectedCount(): Int = (longQuery("SELECT COUNT(*) FROM tickets WHERE sync_error IS NOT NULL") ?: 0).toInt()

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
