package dz.truckgate.quarry.print

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.text.Layout
import android.text.StaticLayout
import android.text.TextPaint
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel
import dz.truckgate.quarry.core.Dither
import dz.truckgate.quarry.core.EscPosEncoder
import dz.truckgate.quarry.core.MonoBitmap
import dz.truckgate.quarry.core.PrinterProfile

/** One line of the printed ticket. */
data class PrintLine(val text: String, val sizePx: Float, val bold: Boolean = false)

/**
 * Draws the ticket as an image the width of the paper (Arabic shaping and right-to-left handled
 * by Android's text engine), adds the QR code drawn here, and converts everything to ESC/POS raster.
 */
object TicketRenderer {

    fun render(header: List<PrintLine>, qrText: String?, footer: List<PrintLine>, profile: PrinterProfile): List<ByteArray> {
        val width = profile.paper.dots
        val margin = 8
        val blocks = mutableListOf<Bitmap>()
        header.forEach { blocks += textBlock(it, width - 2 * margin) }
        if (qrText != null) blocks += qrBlock(qrText, width - 2 * margin)
        footer.forEach { blocks += textBlock(it, width - 2 * margin) }

        val gap = 6
        val height = blocks.sumOf { it.height + gap } + 2 * margin
        val page = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(page)
        canvas.drawColor(Color.WHITE)
        var y = margin
        blocks.forEach { b ->
            canvas.drawBitmap(b, ((width - b.width) / 2).toFloat(), y.toFloat(), null)
            y += b.height + gap
            b.recycle()
        }
        val mono = toMono(page)
        page.recycle()
        return EscPosEncoder.encode(mono, profile)
    }

    private fun textBlock(line: PrintLine, width: Int): Bitmap {
        val paint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.BLACK
            textSize = line.sizePx
            typeface = if (line.bold) Typeface.DEFAULT_BOLD else Typeface.DEFAULT
        }
        val layout = StaticLayout.Builder.obtain(line.text, 0, line.text.length, paint, width)
            .setAlignment(Layout.Alignment.ALIGN_CENTER)
            .build()
        val bmp = Bitmap.createBitmap(width, maxOf(1, layout.height), Bitmap.Config.ARGB_8888)
        val c = Canvas(bmp)
        c.drawColor(Color.WHITE)
        layout.draw(c)
        return bmp
    }

    private fun qrBlock(text: String, maxWidth: Int): Bitmap {
        val hints = mapOf(EncodeHintType.MARGIN to 4, EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M)
        val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints)
        val module = EscPosEncoder.qrModuleSize(matrix.width, maxWidth)
        val size = matrix.width * module
        val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val c = Canvas(bmp)
        c.drawColor(Color.WHITE)
        val black = Paint().apply { color = Color.BLACK }
        for (y in 0 until matrix.height) for (x in 0 until matrix.width) {
            if (matrix[x, y]) {
                c.drawRect((x * module).toFloat(), (y * module).toFloat(),
                    ((x + 1) * module).toFloat(), ((y + 1) * module).toFloat(), black)
            }
        }
        return bmp
    }

    private fun toMono(bmp: Bitmap): MonoBitmap {
        val w = bmp.width
        val h = bmp.height
        val pixels = IntArray(w * h)
        bmp.getPixels(pixels, 0, w, 0, 0, w, h)
        val luma = IntArray(w * h) { i ->
            val p = pixels[i]
            (Color.red(p) * 299 + Color.green(p) * 587 + Color.blue(p) * 114) / 1000
        }
        // Text and QR are pure black and white: a threshold keeps them sharp.
        return Dither.threshold(luma, w, h, 160)
    }
}
