package dz.truckgate.quarry.gps

import android.annotation.SuppressLint
import android.content.Context
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Looper
import android.os.SystemClock
import dz.truckgate.quarry.core.GpsFix

/**
 * Satellite fixes straight from the GPS chip (no Google services needed). The fix time comes from
 * the satellites, so it does not depend on the phone clock.
 */
class GpsTracker(context: Context) : LocationListener {
    private val manager = context.getSystemService(LocationManager::class.java)

    @Volatile
    private var last: Location? = null

    @SuppressLint("MissingPermission") // checked by the caller
    fun start() {
        if (manager.isProviderEnabled(LocationManager.GPS_PROVIDER))
            manager.requestLocationUpdates(LocationManager.GPS_PROVIDER, 2_000L, 0f, this, Looper.getMainLooper())
    }

    fun stop() = manager.removeUpdates(this)

    override fun onLocationChanged(location: Location) {
        last = location
    }

    /** The latest fix with its age measured on the monotonic clock. */
    fun latestFix(): GpsFix? {
        val l = last ?: return null
        val ageMs = (SystemClock.elapsedRealtimeNanos() - l.elapsedRealtimeNanos) / 1_000_000
        return GpsFix(
            gpsTimeMs = l.time,
            latE6 = (l.latitude * 1_000_000).toInt(),
            lonE6 = (l.longitude * 1_000_000).toInt(),
            accuracyM = if (l.hasAccuracy()) l.accuracy else Float.MAX_VALUE,
            ageMs = ageMs,
        )
    }

    /** Best estimate of the current UTC time in seconds: GPS when recent, phone clock otherwise. */
    fun bestUnixSeconds(maxFixAgeMs: Long = 600_000): Long {
        val fix = latestFix()
        return if (fix != null && fix.ageMs in 0..maxFixAgeMs) (fix.gpsTimeMs + fix.ageMs) / 1000
        else System.currentTimeMillis() / 1000
    }
}
