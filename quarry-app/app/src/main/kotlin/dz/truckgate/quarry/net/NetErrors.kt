package dz.truckgate.quarry.net

import android.content.Context
import dz.truckgate.quarry.R
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import javax.net.ssl.SSLException

/** Turns a failed call to the factory server into a message an operator can act on. */
object NetErrors {
    private val serverCodes = mapOf(
        "ENROLLMENT_CODE_UNKNOWN" to R.string.srv_enroll_unknown,
        "ENROLLMENT_CODE_USED" to R.string.srv_enroll_used,
        "ENROLLMENT_CODE_EXPIRED" to R.string.srv_enroll_expired,
        "KEY_ALREADY_ENROLLED" to R.string.srv_key_enrolled,
        "DEVICE_NOT_ALLOWED" to R.string.srv_device_not_allowed,
        "REQUEST_TIME" to R.string.srv_request_time,
        "SIGNATURE_INVALID" to R.string.srv_signature_invalid,
        "BODY_TOO_LARGE" to R.string.srv_too_large,
    )

    fun describe(context: Context, url: String, e: Throwable): String = when (e) {
        is ServerException -> serverCodes[e.code]?.let { context.getString(it) }
            ?: context.getString(R.string.net_server_error, e.message ?: e.code)
        is UnknownHostException -> context.getString(R.string.net_unknown_host, url)
        is SocketTimeoutException -> context.getString(R.string.net_timeout, url)
        is NoRouteToHostException -> context.getString(R.string.net_no_route, url)
        is ConnectException -> context.getString(R.string.net_refused, url)
        is SSLException -> context.getString(R.string.net_tls, url)
        else -> context.getString(R.string.error, e.message ?: e.javaClass.simpleName)
    }
}
