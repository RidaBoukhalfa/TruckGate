package dz.truckgate.quarry.net

import dz.truckgate.quarry.core.DeviceAuth
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.Base64

class ServerException(val code: String, message: String) : IOException(message)

/** Minimal HTTP client for the factory server (local network). Call from a background thread. */
class ServerApi(private val baseUrl: String) {

    data class Enrolled(val deviceId: Int, val serverTime: Long)
    data class SignedConfig(val payload: String, val signature: String)

    fun enroll(token: String, ticketKey: ByteArray, authKey: ByteArray, attestation: ByteArray, label: String): Enrolled {
        val b64 = Base64.getEncoder()
        val body = JSONObject()
            .put("token", token)
            .put("ticketKeySpki", b64.encodeToString(ticketKey))
            .put("authKeySpki", b64.encodeToString(authKey))
            .put("attestation", b64.encodeToString(attestation))
            .put("label", label)
        val json = request("POST", "/api/enroll", body.toString().toByteArray(Charsets.UTF_8), emptyMap())
        return Enrolled(json.getInt("deviceId"), json.getLong("serverTime"))
    }

    /** Server clock (unauthenticated): used only to time-stamp the signed requests that follow. */
    fun serverTime(): Long = request("GET", "/api/time", null, emptyMap()).getLong("serverTime")

    /** [sign] signs with the phone's auth key and returns raw r||s. */
    fun fetchConfig(deviceId: Int, unixSeconds: Long, sign: (ByteArray) -> ByteArray): SignedConfig {
        val path = "/api/devices/$deviceId/config"
        val json = request("GET", path, null, authHeaders(deviceId, unixSeconds, "GET", path, ByteArray(0), sign))
        return SignedConfig(json.getString("payload"), json.getString("signature"))
    }

    /** Uploads tickets, print counts and voids; the body is covered by the device signature. */
    fun sync(deviceId: Int, unixSeconds: Long, body: ByteArray, sign: (ByteArray) -> ByteArray): JSONObject {
        val path = "/api/devices/$deviceId/sync"
        return request("POST", path, body, authHeaders(deviceId, unixSeconds, "POST", path, body, sign))
    }

    fun uploadPhoto(deviceId: Int, seq: Long, unixSeconds: Long, jpeg: ByteArray, sign: (ByteArray) -> ByteArray) {
        val path = "/api/devices/$deviceId/photos/$seq"
        request("PUT", path, jpeg, authHeaders(deviceId, unixSeconds, "PUT", path, jpeg, sign), "image/jpeg")
    }

    private fun authHeaders(deviceId: Int, unixSeconds: Long, method: String, path: String, body: ByteArray,
                            sign: (ByteArray) -> ByteArray): Map<String, String> {
        val message = DeviceAuth.message(deviceId, unixSeconds, method, path, body)
        return mapOf(
            "X-TG-Device" to deviceId.toString(),
            "X-TG-Time" to unixSeconds.toString(),
            "X-TG-Signature" to Base64.getEncoder().encodeToString(sign(message)),
        )
    }

    private fun request(method: String, path: String, body: ByteArray?, headers: Map<String, String>,
                        contentType: String = "application/json; charset=utf-8"): JSONObject {
        val conn = URL(baseUrl + path).openConnection() as HttpURLConnection
        try {
            conn.requestMethod = method
            conn.connectTimeout = 8_000
            conn.readTimeout = 30_000
            conn.setRequestProperty("Accept", "application/json")
            headers.forEach { (k, v) -> conn.setRequestProperty(k, v) }
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", contentType)
                conn.outputStream.use { it.write(body) }
            }
            val status = conn.responseCode
            val stream = if (status in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
            if (status !in 200..299) {
                val err = runCatching { JSONObject(text) }.getOrNull()
                throw ServerException(err?.optString("error") ?: "HTTP_$status", err?.optString("message") ?: "HTTP $status")
            }
            return if (text.isBlank()) JSONObject() else JSONObject(text)
        } finally {
            conn.disconnect()
        }
    }
}
