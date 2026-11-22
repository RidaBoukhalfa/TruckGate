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
    fun describe(context: Context, url: String, e: Throwable): String = when (e) {
        is ServerException -> context.getString(R.string.net_server_error, e.message ?: e.code)
        is UnknownHostException -> context.getString(R.string.net_unknown_host, url)
        is SocketTimeoutException -> context.getString(R.string.net_timeout, url)
        is NoRouteToHostException -> context.getString(R.string.net_no_route, url)
        is ConnectException -> context.getString(R.string.net_refused, url)
        is SSLException -> context.getString(R.string.net_tls, url)
        else -> context.getString(R.string.error, e.message ?: e.javaClass.simpleName)
    }
}
