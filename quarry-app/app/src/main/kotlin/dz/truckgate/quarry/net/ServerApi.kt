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

    /** [sign] signs with the phone's auth key and returns raw r||s. */
    fun fetchConfig(deviceId: Int, unixSeconds: Long, sign: (ByteArray) -> ByteArray): SignedConfig {
        val path = "/api/devices/$deviceId/config"
        val message = DeviceAuth.message(deviceId, unixSeconds, "GET", path, ByteArray(0))
        val headers = mapOf(
            "X-TG-Device" to deviceId.toString(),
            "X-TG-Time" to unixSeconds.toString(),
            "X-TG-Signature" to Base64.getEncoder().encodeToString(sign(message)),
        )
        val json = request("GET", path, null, headers)
        return SignedConfig(json.getString("payload"), json.getString("signature"))
    }

    private fun request(method: String, path: String, body: ByteArray?, headers: Map<String, String>): JSONObject {
        val conn = URL(baseUrl + path).openConnection() as HttpURLConnection
        try {
            conn.requestMethod = method
            conn.connectTimeout = 8_000
            conn.readTimeout = 15_000
            conn.setRequestProperty("Accept", "application/json")
            headers.forEach { (k, v) -> conn.setRequestProperty(k, v) }
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                conn.outputStream.use { it.write(body) }
            }
            val status = conn.responseCode
            val stream = if (status in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
            if (status !in 200..299) {
                val err = runCatching { JSONObject(text) }.getOrNull()
                throw ServerException(err?.optString("error") ?: "HTTP_$status", err?.optString("message") ?: "HTTP $status")
            }
            return JSONObject(text)
        } finally {
            conn.disconnect()
        }
    }
}
