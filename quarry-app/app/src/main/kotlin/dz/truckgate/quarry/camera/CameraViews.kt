package dz.truckgate.quarry.camera

import android.content.Context
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageCapture
import androidx.camera.core.ImageCaptureException
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.NotFoundException
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.io.File
import java.util.UUID
import java.util.concurrent.Executors

/** Camera preview that reports the first QR code it reads. */
@Composable
fun QrScanner(modifier: Modifier = Modifier, onText: (String) -> Unit) {
    val context = LocalContext.current
    val owner = LocalLifecycleOwner.current
    val executor = remember { Executors.newSingleThreadExecutor() }
    val reader = remember {
        MultiFormatReader().apply {
            setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE)))
        }
    }
    val done = remember { java.util.concurrent.atomic.AtomicBoolean(false) }
    DisposableEffect(Unit) { onDispose { executor.shutdown() } }
    AndroidView(modifier = modifier, factory = { ctx ->
        PreviewView(ctx).also { view ->
            bind(ctx, owner, view) { provider ->
                val analysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()
                analysis.setAnalyzer(executor) { image ->
                    try {
                        if (!done.get()) {
                            val plane = image.planes[0]
                            val buffer = plane.buffer
                            val data = ByteArray(buffer.remaining()).also { buffer.get(it) }
                            val source = PlanarYUVLuminanceSource(data, plane.rowStride, image.height, 0, 0,
                                image.width, image.height, false)
                            val text = reader.decodeWithState(BinaryBitmap(HybridBinarizer(source))).text
                            done.set(true)
                            ContextCompat.getMainExecutor(ctx).execute { onText(text) }
                        }
                    } catch (e: NotFoundException) {
                        // no QR in this frame
                    } finally {
                        reader.reset()
                        image.close()
                    }
                }
                listOf(analysis)
            }
        }
    })
}

/** Preview + capture into the app's private storage (never the gallery). */
class PhotoCapture {
    internal var imageCapture: ImageCapture? = null

    fun take(context: Context, onSaved: (File) -> Unit, onError: (String) -> Unit) {
        val capture = imageCapture ?: return onError("camera not ready")
        val dir = File(context.filesDir, "photos").apply { mkdirs() }
        val file = File(dir, UUID.randomUUID().toString() + ".jpg")
        capture.takePicture(ImageCapture.OutputFileOptions.Builder(file).build(), ContextCompat.getMainExecutor(context),
            object : ImageCapture.OnImageSavedCallback {
                override fun onImageSaved(output: ImageCapture.OutputFileResults) = onSaved(file)
                override fun onError(exception: ImageCaptureException) = onError(exception.message ?: "capture failed")
            })
    }
}

@Composable
fun PhotoPreview(capture: PhotoCapture, modifier: Modifier = Modifier) {
    val owner = LocalLifecycleOwner.current
    Box(modifier) {
        AndroidView(modifier = Modifier.fillMaxSize(), factory = { ctx ->
            PreviewView(ctx).also { view ->
                bind(ctx, owner, view) {
                    val ic = ImageCapture.Builder().setCaptureMode(ImageCapture.CAPTURE_MODE_MINIMIZE_LATENCY).build()
                    capture.imageCapture = ic
                    listOf(ic)
                }
            }
        })
    }
}

private fun bind(
    context: Context,
    owner: androidx.lifecycle.LifecycleOwner,
    view: PreviewView,
    useCases: (ProcessCameraProvider) -> List<androidx.camera.core.UseCase>,
) {
    val future = ProcessCameraProvider.getInstance(context)
    future.addListener({
        val provider = future.get()
        val preview = Preview.Builder().build().also { it.setSurfaceProvider(view.surfaceProvider) }
        provider.unbindAll()
        provider.bindToLifecycle(owner, CameraSelector.DEFAULT_BACK_CAMERA, preview, *useCases(provider).toTypedArray())
    }, ContextCompat.getMainExecutor(context))
}
