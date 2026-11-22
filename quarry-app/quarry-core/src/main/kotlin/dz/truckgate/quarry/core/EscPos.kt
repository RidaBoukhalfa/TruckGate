package dz.truckgate.quarry.core

/** 1-bit image, `true` = black dot. */
class MonoBitmap(val width: Int, val height: Int) {
    private val bits = BooleanArray(width * height)

    operator fun get(x: Int, y: Int): Boolean = bits[y * width + x]
    operator fun set(x: Int, y: Int, black: Boolean) {
        bits[y * width + x] = black
    }

    fun blackCount(): Int = bits.count { it }
}

/** Turns 8-bit grey (0 = black, 255 = white) into 1-bit. */
object Dither {
    fun threshold(luma: IntArray, width: Int, height: Int, level: Int = 128): MonoBitmap {
        require(luma.size == width * height)
        val out = MonoBitmap(width, height)
        for (y in 0 until height) for (x in 0 until width) out[x, y] = luma[y * width + x] < level
        return out
    }

    /** For photos; text and QR codes should use [threshold] to stay sharp. */
    fun floydSteinberg(luma: IntArray, width: Int, height: Int): MonoBitmap {
        require(luma.size == width * height)
        val err = luma.map { it.toFloat() }.toFloatArray()
        val out = MonoBitmap(width, height)
        for (y in 0 until height) for (x in 0 until width) {
            val i = y * width + x
            val old = err[i]
            val black = old < 128f
            out[x, y] = black
            val e = old - (if (black) 0f else 255f)
            if (x + 1 < width) err[i + 1] += e * 7 / 16
            if (y + 1 < height) {
                if (x > 0) err[i + width - 1] += e * 3 / 16
                err[i + width] += e * 5 / 16
                if (x + 1 < width) err[i + width + 1] += e * 1 / 16
            }
        }
        return out
    }
}

enum class PaperWidth(val mm: Int, val dots: Int) {
    MM58(58, 384),
    MM80(80, 576),
}

/**
 * Printer settings chosen once in the app (with a test print). Defaults suit most cheap
 * Bluetooth printers; [rowsPerChunk] and the app's pause between chunks can be lowered for
 * printers with small buffers.
 */
data class PrinterProfile(
    val paper: PaperWidth = PaperWidth.MM58,
    val rowsPerChunk: Int = 24,
    val feedLinesAfter: Int = 4,
    val cut: Boolean = false,
) {
    init {
        require(rowsPerChunk in 1..255) { "rowsPerChunk must be 1..255" }
        require(feedLinesAfter in 0..255)
    }
}

/**
 * Printer-agnostic ESC/POS output. Everything (Arabic and French text, QR code) is sent as a
 * raster image with `GS v 0`, which virtually every ESC/POS printer supports. This avoids the
 * printer's own fonts (usually no Arabic shaping) and its QR command (`GS ( k`, missing on
 * many cheap models).
 */
object EscPosEncoder {
    private val INIT = byteArrayOf(0x1B, 0x40)

    /** Returns the chunks to send in order; the app pauses briefly between chunks. */
    fun encode(image: MonoBitmap, profile: PrinterProfile): List<ByteArray> {
        require(image.width <= profile.paper.dots) {
            "image is ${image.width} dots wide, paper allows ${profile.paper.dots}"
        }
        val bytesPerRow = (image.width + 7) / 8
        val chunks = mutableListOf(INIT)
        var y0 = 0
        while (y0 < image.height) {
            val rows = minOf(profile.rowsPerChunk, image.height - y0)
            val out = ByteArray(8 + bytesPerRow * rows)
            out[0] = 0x1D; out[1] = 0x76; out[2] = 0x30; out[3] = 0x00
            out[4] = (bytesPerRow and 0xFF).toByte(); out[5] = (bytesPerRow shr 8).toByte()
            out[6] = (rows and 0xFF).toByte(); out[7] = (rows shr 8).toByte()
            var k = 8
            for (y in y0 until y0 + rows) {
                for (bx in 0 until bytesPerRow) {
                    var b = 0
                    for (bit in 0 until 8) {
                        val x = bx * 8 + bit
                        if (x < image.width && image[x, y]) b = b or (0x80 ushr bit)
                    }
                    out[k++] = b.toByte()
                }
            }
            chunks += out
            y0 += rows
        }
        if (profile.feedLinesAfter > 0) chunks += byteArrayOf(0x1B, 0x64, profile.feedLinesAfter.toByte())
        if (profile.cut) chunks += byteArrayOf(0x1D, 0x56, 0x42, 0x00)
        return chunks
    }

    /** Largest whole number of dots per QR module that fits the width (quiet zone included). */
    fun qrModuleSize(modulesWithQuietZone: Int, maxDots: Int): Int = maxOf(1, maxDots / modulesWithQuietZone)
}
