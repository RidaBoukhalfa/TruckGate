package dz.truckgate.quarry.core

/**
 * Registration numbers ("matricule") typed by the quarry worker. Same rules as the server
 * (PlateRules.cs): Arabic-Indic and Persian digits become ASCII, separators are ignored for
 * comparison, 6 to 12 digits, written "serial category+year wilaya" when 10 or 11 digits long.
 */
object PlateRules {
    private fun digit(c: Char): Char? = when (c) {
        in '0'..'9' -> c
        in '٠'..'٩' -> '0' + (c - '٠')
        in '۰'..'۹' -> '0' + (c - '۰')
        else -> null
    }

    /** Digits only: two ways of typing the same plate give the same key. */
    fun key(plate: String?): String = plate.orEmpty().mapNotNull(::digit).joinToString("")

    /** Null when the text cannot be a registration number. */
    fun normalize(plate: String?): String? {
        if (plate.isNullOrBlank()) return null
        if (plate.any { digit(it) == null && it !in " -./_" }) return null
        val d = key(plate)
        if (d.length !in 6..12) return null
        if (d.length == 10 || d.length == 11)
            return "${d.substring(0, d.length - 5)} ${d.substring(d.length - 5, d.length - 2)} ${d.substring(d.length - 2)}"
        return plate.map { digit(it) ?: it }.joinToString("")
            .split(' ', '-', '_', '.', '/').filter { it.isNotEmpty() }.joinToString(" ")
    }
}

/**
 * Truck numbers reserved for this phone by the server (blocks in the signed configuration): the
 * smallest one not used yet, or null when the phone must sync before registering another truck.
 */
object ProvisionalIds {
    fun next(blocks: List<IntRange>, used: Set<Int>): Int? =
        blocks.sortedBy { it.first }.asSequence().flatMap { it.asSequence() }.firstOrNull { it !in used }
}
