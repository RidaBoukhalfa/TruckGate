package dz.truckgate.quarry.print

import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.io.IOException
import java.util.UUID

data class PairedPrinter(val name: String, val address: String)

/**
 * Bluetooth Classic serial (SPP) — what almost every portable ESC/POS printer uses. Data is sent
 * in small chunks with a short pause so that printers with a small buffer keep up.
 */
@SuppressLint("MissingPermission") // BLUETOOTH_CONNECT is requested by the UI before any call
class BluetoothPrinter(private val context: Context) {
    private val adapter get() = context.getSystemService(BluetoothManager::class.java)?.adapter

    fun paired(): List<PairedPrinter> =
        adapter?.bondedDevices.orEmpty().map { PairedPrinter(it.name ?: it.address, it.address) }.sortedBy { it.name }

    suspend fun print(address: String, chunks: List<ByteArray>, pauseMs: Long = 25) = withContext(Dispatchers.IO) {
        val bt = adapter ?: throw IOException("Bluetooth not available")
        val device = bt.getRemoteDevice(address)
        val socket = connect { device.createRfcommSocketToServiceRecord(SPP) }
            ?: connect { device.createInsecureRfcommSocketToServiceRecord(SPP) }
            ?: throw IOException("cannot connect to printer")
        try {
            val out = socket.outputStream
            for (chunk in chunks) {
                out.write(chunk)
                out.flush()
                delay(pauseMs)
            }
            delay(400) // let the printer drain its buffer before the link closes
        } finally {
            runCatching { socket.close() }
        }
    }

    private fun connect(open: () -> BluetoothSocket): BluetoothSocket? = try {
        open().also { it.connect() }
    } catch (e: IOException) {
        null
    }

    private companion object {
        val SPP: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }
}
