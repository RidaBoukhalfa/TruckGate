package dz.truckgate.quarry.core

import dz.truckgate.ticket.TicketFields
import kotlin.math.abs

/** Latest GPS fix. [gpsTimeMs] is the satellite time of the fix, [ageMs] how long ago it was taken (monotonic clock). */
data class GpsFix(val gpsTimeMs: Long, val latE6: Int, val lonE6: Int, val accuracyM: Float, val ageMs: Long)

/** Issuance parameters, delivered in the server-signed device configuration. */
data class IssuanceParams(
    val truckCooldownS: Int = 1200,
    val clockMismatchS: Int = 300,
    val syncMaxAgeS: Int = 86_400,
    val gpsMaxAgeS: Int = 120,
    val gpsMaxAccuracyM: Float = 150f,
    val maxUnsyncedTickets: Int = 500,
    /** Supervisor policy for a phone not synced for [syncMaxAgeS]: block issuance (default) or only warn. */
    val syncOverdueBlocks: Boolean = true,
)

data class IssuanceContext(
    val phoneNowMs: Long,
    val fix: GpsFix?,
    val geofence: Geofence?,
    val deviceRevoked: Boolean,
    val truckActive: Boolean,
    /** issued_at (s) of the last ticket for this truck on this phone, if any. */
    val truckLastIssuedAt: Long?,
    /** issued_at (s) of the newest ticket on this phone, any truck. Detects a clock moved backwards. */
    val lastIssuedAt: Long?,
    val defaultDriverId: Int?,
    val driverId: Int,
    /** Seconds (same time base as issued_at) of the last successful sync, or of enrollment if never synced. */
    val lastSyncAt: Long,
    val unsyncedCount: Int,
)

sealed interface BlockReason {
    data object DeviceRevoked : BlockReason
    data object TruckInactive : BlockReason
    data class TruckCooldown(val remainingS: Long) : BlockReason
    data object OutsideGeofence : BlockReason
    data class SyncOverdue(val hoursSinceSync: Long) : BlockReason
    data class TooManyUnsynced(val count: Int) : BlockReason
}

sealed interface IssuanceDecision {
    data class Allowed(val issuedAt: Long, val clockDelta: Int, val latE6: Int, val lonE6: Int, val flags: Int) :
        IssuanceDecision

    data class Blocked(val reasons: List<BlockReason>) : IssuanceDecision
}

/**
 * Decides whether a ticket may be issued now and with which time, position and flags
 * (docs/DECISIONS.md: 20-minute truck rule, geofence, GPS time, 24-hour sync rule).
 */
object IssuanceRules {
    /** Hours since the last sync when it is older than the limit (shown as a warning when it does not block). */
    fun syncOverdueHours(nowS: Long, lastSyncAt: Long, p: IssuanceParams): Long? =
        if (p.syncMaxAgeS > 0 && nowS - lastSyncAt > p.syncMaxAgeS) (nowS - lastSyncAt) / 3600 else null

    fun decide(ctx: IssuanceContext, p: IssuanceParams): IssuanceDecision {
        val blocked = mutableListOf<BlockReason>()
        var flags = 0

        val fix = ctx.fix?.takeIf { it.ageMs in 0..p.gpsMaxAgeS * 1000L && it.accuracyM <= p.gpsMaxAccuracyM }
        val issuedAt: Long
        val clockDelta: Int
        val lat: Int
        val lon: Int
        if (fix != null) {
            val gpsNowMs = fix.gpsTimeMs + fix.ageMs
            issuedAt = gpsNowMs / 1000
            clockDelta = ((ctx.phoneNowMs - gpsNowMs) / 1000).coerceIn(Short.MIN_VALUE.toLong(), Short.MAX_VALUE.toLong()).toInt()
            if (abs(clockDelta) > p.clockMismatchS) flags = flags or TicketFields.FLAG_CLOCK_MISMATCH
            lat = fix.latE6
            lon = fix.lonE6
            if (ctx.geofence != null && !ctx.geofence.contains(GeoPoint(lat, lon))) blocked += BlockReason.OutsideGeofence
        } else {
            flags = flags or TicketFields.FLAG_NO_GPS
            issuedAt = ctx.phoneNowMs / 1000
            clockDelta = 0
            lat = 0
            lon = 0
        }

        // A clock that went backwards is suspicious even without GPS.
        if (ctx.lastIssuedAt != null && issuedAt < ctx.lastIssuedAt - 60) flags = flags or TicketFields.FLAG_CLOCK_MISMATCH

        if (ctx.deviceRevoked) blocked += BlockReason.DeviceRevoked
        if (!ctx.truckActive) blocked += BlockReason.TruckInactive

        if (ctx.truckLastIssuedAt != null) {
            val since = issuedAt - ctx.truckLastIssuedAt
            // A negative value (clock moved back) also blocks, for the full cooldown.
            if (since < p.truckCooldownS) {
                val remaining = if (since < 0) p.truckCooldownS.toLong() else p.truckCooldownS - since
                blocked += BlockReason.TruckCooldown(remaining)
            }
        }

        if (p.syncOverdueBlocks && p.syncMaxAgeS > 0 && issuedAt - ctx.lastSyncAt > p.syncMaxAgeS)
            blocked += BlockReason.SyncOverdue((issuedAt - ctx.lastSyncAt) / 3600)
        if (ctx.unsyncedCount >= p.maxUnsyncedTickets) blocked += BlockReason.TooManyUnsynced(ctx.unsyncedCount)

        if (ctx.defaultDriverId != null && ctx.defaultDriverId != ctx.driverId)
            flags = flags or TicketFields.FLAG_DRIVER_CHANGED

        return if (blocked.isEmpty()) IssuanceDecision.Allowed(issuedAt, clockDelta, lat, lon, flags)
        else IssuanceDecision.Blocked(blocked)
    }
}
