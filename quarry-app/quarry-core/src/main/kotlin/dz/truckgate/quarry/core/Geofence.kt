package dz.truckgate.quarry.core

/** A point in micro-degrees, the same unit as the ticket's lat_e6 / lon_e6. */
data class GeoPoint(val latE6: Int, val lonE6: Int)

/**
 * Quarry boundary as a simple polygon (no holes). Tickets can only be issued inside it
 * when a GPS fix is available.
 */
class Geofence(val polygon: List<GeoPoint>) {
    init {
        require(polygon.size >= 3) { "a geofence needs at least 3 points" }
    }

    /** Ray casting; points exactly on an edge may fall either side, which is fine at metre scale. */
    fun contains(p: GeoPoint): Boolean {
        val x = p.lonE6.toDouble()
        val y = p.latE6.toDouble()
        var inside = false
        var j = polygon.size - 1
        for (i in polygon.indices) {
            val xi = polygon[i].lonE6.toDouble(); val yi = polygon[i].latE6.toDouble()
            val xj = polygon[j].lonE6.toDouble(); val yj = polygon[j].latE6.toDouble()
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside
            j = i
        }
        return inside
    }
}
