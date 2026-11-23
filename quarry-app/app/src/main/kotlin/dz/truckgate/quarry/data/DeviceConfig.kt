package dz.truckgate.quarry.data

import dz.truckgate.quarry.core.GeoPoint
import dz.truckgate.quarry.core.Geofence
import dz.truckgate.quarry.core.IssuanceParams
import org.json.JSONArray
import org.json.JSONObject

/**
 * [status]: APPROVED or PENDING_APPROVAL (registered from a quarry phone, waiting for the supervisor).
 * [local]: registered on this phone and not confirmed by the server yet.
 */
data class Truck(
    val id: Int, val fleetNumber: String, val plate: String?, val defaultDriverId: Int?, val active: Boolean,
    val status: String = "APPROVED", val local: Boolean = false,
) {
    val pending: Boolean get() = local || status == "PENDING_APPROVAL"
}
data class Driver(val id: Int, val name: String)
data class Operator(val id: Int, val name: String, val pinHash: String)

/** Parsed `TGCFG1` payload (signature already verified by the caller). */
data class DeviceConfig(
    val version: Int,
    val deviceId: Int,
    val generatedAt: Long,
    val siteName: String?,
    val geofence: Geofence?,
    val trucks: List<Truck>,
    val drivers: List<Driver>,
    val operators: List<Operator>,
    val params: IssuanceParams,
    val factoryName: String = "",
    /** Truck numbers this phone may give to a truck it registers offline. */
    val truckIdBlocks: List<IntRange> = emptyList(),
    val photoRequired: Boolean = true,
) {
    fun truck(id: Int) = trucks.firstOrNull { it.id == id }
    fun driver(id: Int) = drivers.firstOrNull { it.id == id }
    fun operator(id: Int) = operators.firstOrNull { it.id == id }

    companion object {
        fun parse(payload: String): DeviceConfig {
            val root = JSONObject(payload)
            require(root.getString("format") == "TGCFG1") { "unknown configuration format" }
            val content = root.getJSONObject("content")
            val site = content.optJSONObject("site")
            val fence = site?.optJSONArray("geofence")?.let { arr ->
                Geofence((0 until arr.length()).map { i ->
                    val p = arr.getJSONArray(i)
                    GeoPoint(p.getInt(0), p.getInt(1))
                })
            }
            val p = content.getJSONObject("params")
            return DeviceConfig(
                version = root.getInt("version"),
                deviceId = root.getInt("deviceId"),
                generatedAt = root.getLong("generatedAt"),
                siteName = site?.optString("name"),
                geofence = fence,
                trucks = content.getJSONArray("trucks").objects().map {
                    Truck(it.getInt("id"), it.getString("fleetNumber"), it.optStringOrNull("plate"),
                        if (it.isNull("defaultDriverId")) null else it.getInt("defaultDriverId"), it.getBoolean("active"),
                        status = it.optString("status", "APPROVED"))
                },
                drivers = content.getJSONArray("drivers").objects().map { Driver(it.getInt("id"), it.getString("name")) },
                operators = content.getJSONArray("operators").objects().map {
                    Operator(it.getInt("id"), it.getString("name"), it.getString("pinHash"))
                },
                params = IssuanceParams(
                    truckCooldownS = p.getInt("truckCooldownS"),
                    clockMismatchS = p.getInt("clockMismatchS"),
                    syncMaxAgeS = p.getInt("syncMaxAgeS"),
                    gpsMaxAgeS = p.getInt("gpsMaxAgeS"),
                    gpsMaxAccuracyM = p.getInt("gpsMaxAccuracyM").toFloat(),
                    maxUnsyncedTickets = p.getInt("maxUnsyncedTickets"),
                    syncOverdueBlocks = p.optString("syncOverduePolicy", "BLOCK") != "WARN",
                ),
                factoryName = content.optString("factoryName", ""),
                truckIdBlocks = content.optJSONArray("truckIdBlocks")?.let { arr ->
                    (0 until arr.length()).map { i -> arr.getJSONArray(i).let { b -> b.getInt(0)..b.getInt(1) } }
                } ?: emptyList(),
                photoRequired = p.optBoolean("photoRequired", true),
            )
        }

        private fun JSONArray.objects(): List<JSONObject> = (0 until length()).map { getJSONObject(it) }
        private fun JSONObject.optStringOrNull(key: String): String? = if (isNull(key)) null else getString(key)
    }
}
