package dz.truckgate.quarry.sync

import android.content.Context
import dz.truckgate.quarry.core.SignedConfig
import dz.truckgate.quarry.data.DeviceConfig
import dz.truckgate.quarry.data.Keys.K_CONFIG
import dz.truckgate.quarry.data.Keys.K_CONFIG_SIG
import dz.truckgate.quarry.data.Keys.K_DEVICE_ID
import dz.truckgate.quarry.data.Keys.K_LAST_SYNC
import dz.truckgate.quarry.data.Keys.K_LAST_SYNC_ERROR
import dz.truckgate.quarry.data.Keys.K_REVOKED
import dz.truckgate.quarry.data.Keys.K_SERVER_KEY
import dz.truckgate.quarry.data.Keys.K_SERVER_URL
import dz.truckgate.quarry.data.Store
import dz.truckgate.quarry.net.ServerApi
import dz.truckgate.quarry.net.ServerException
import dz.truckgate.quarry.security.DeviceKeys
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.Base64

/**
 * One synchronisation with the factory server, used by the background worker and the "sync now"
 * button: signed configuration down, tickets / print counts / voids up, then (on Wi-Fi) the photos
 * of loaded trucks. Blocking: call from a background thread.
 */
class SyncEngine(context: Context) {
    private val store = Store.get(context)

    data class Result(val config: DeviceConfig?, val sent: Int, val rejected: Int, val photos: Int, val photosRefused: Int = 0)

    fun run(includePhotos: Boolean): Result {
        val id = store.get(K_DEVICE_ID)?.toIntOrNull() ?: error("not enrolled")
        val api = ServerApi(store.get(K_SERVER_URL) ?: error("no server"))
        try {
            // Signed requests are time-stamped with the server's clock, so a wrong phone clock cannot block sync.
            val offset = api.serverTime() - nowPhone()
            val now = { nowPhone() + offset }
            val config = fetchConfig(api, id, now())

            var sent = 0
            var rejected = 0
            while (true) {
                val batch = store.pendingSync(BATCH)
                if (batch.isEmpty()) break
                val body = JSONObject().put("tickets", JSONArray().apply {
                    batch.forEach { t ->
                        put(JSONObject()
                            .put("raw", b64(t.bytes))
                            .put("printCount", t.printCount)
                            .put("driverChangeReason", t.driverChangeReason ?: JSONObject.NULL)
                            .put("void", if (t.voidedAt != null && t.voidSignature != null) JSONObject()
                                .put("at", t.voidedAt).put("reason", t.voidReason).put("signature", b64(t.voidSignature))
                                else JSONObject.NULL))
                    }
                }).toString().toByteArray(Charsets.UTF_8)
                val response = api.sync(id, now(), body) { DeviceKeys.signWithAuthKey(it) }
                val serverTime = response.getLong("serverTime")
                val results = response.getJSONArray("results")
                var progress = 0
                for (i in 0 until results.length()) {
                    val r = results.getJSONObject(i)
                    val row = batch[i]
                    if (r.getString("status") == "OK") {
                        store.markSynced(row.seq, row.changeCount, serverTime, r.optBoolean("photoWanted", true))
                        sent++; progress++
                    } else {
                        store.markSyncError(row.seq, r.optString("error"))
                        rejected++
                    }
                }
                if (progress == 0) break // only rejected tickets left: they stay flagged for the supervisor
            }
            // The server was reached and has everything that is valid; refused tickets raise alerts there.
            store.put(K_LAST_SYNC, now().toString())
            store.put(K_LAST_SYNC_ERROR, "")

            var photos = 0
            var photosRefused = 0
            if (includePhotos) {
                for ((seq, path) in store.photosToUpload(PHOTO_BATCH)) {
                    val file = File(path)
                    if (!file.exists()) { store.markPhotoDone(seq); continue }
                    try {
                        api.uploadPhoto(id, seq, now(), file.readBytes()) { DeviceKeys.signWithAuthKey(it) }
                        store.markPhotoDone(seq)
                        photos++
                    } catch (e: ServerException) {
                        // a refused photo is recorded on the server (alert); do not retry it for ever
                        // (BODY_TOO_LARGE: a photo taken by version 1.0.1 at full camera size; the server
                        // refuses it and it cannot be shrunk because its hash is inside the signed ticket)
                        if (e.code == "PHOTO_MISMATCH" || e.code == "PHOTO_INVALID" || e.code == "BODY_TOO_LARGE") {
                            store.markPhotoDone(seq)
                            photosRefused++
                        } else throw e
                    }
                }
            }
            return Result(config, sent, rejected, photos, photosRefused)
        } catch (e: ServerException) {
            if (e.code == "DEVICE_NOT_ALLOWED") store.put(K_REVOKED, "1")
            store.put(K_LAST_SYNC_ERROR, e.code)
            throw e
        } catch (e: Exception) {
            store.put(K_LAST_SYNC_ERROR, e.message ?: e.javaClass.simpleName)
            throw e
        }
    }

    /** Downloads, verifies (pinned server key, device id, no rollback) and stores the configuration. */
    fun fetchConfig(api: ServerApi, id: Int, unixSeconds: Long): DeviceConfig {
        val serverKey = Base64.getDecoder().decode(store.get(K_SERVER_KEY) ?: error("no server key"))
        val signed = api.fetchConfig(id, unixSeconds) { DeviceKeys.signWithAuthKey(it) }
        check(SignedConfig.verify(signed.payload, signed.signature, serverKey)) { "configuration signature is invalid" }
        val cfg = DeviceConfig.parse(signed.payload)
        check(cfg.deviceId == id) { "configuration is for another device" }
        val previous = store.get(K_CONFIG)?.let { runCatching { DeviceConfig.parse(it) }.getOrNull() }
        check(previous == null || cfg.generatedAt >= previous.generatedAt) { "older configuration refused" }
        store.putAll(mapOf(K_CONFIG to signed.payload, K_CONFIG_SIG to signed.signature))
        if (store.unsyncedCount() == 0) store.put(K_LAST_SYNC, cfg.generatedAt.toString())
        return cfg
    }

    private fun nowPhone() = System.currentTimeMillis() / 1000
    private fun b64(b: ByteArray) = Base64.getEncoder().encodeToString(b)

    private companion object {
        const val BATCH = 100
        const val PHOTO_BATCH = 20
    }
}
