package dz.truckgate.quarry.camera

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.media.ExifInterface
import android.util.Size
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageCapture
import androidx.camera.core.ImageCaptureException
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
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

/**
 * Preview + capture into the app's private storage (never the gallery). The photo is kept small
 * (about 1600 px, a few hundred KB): its SHA-256 goes into the signed ticket, so it cannot be
 * shrunk later, and phones with 50-108 MP cameras would otherwise produce 10-30 MB files.
 */
class PhotoCapture {
    internal var imageCapture: ImageCapture? = null
    private val worker = Executors.newSingleThreadExecutor()

    fun take(context: Context, onSaved: (File) -> Unit, onError: (String) -> Unit) {
        val capture = imageCapture ?: return onError("camera not ready")
        val dir = File(context.filesDir, "photos").apply { mkdirs() }
        val file = File(dir, UUID.randomUUID().toString() + ".jpg")
        val main = ContextCompat.getMainExecutor(context)
        capture.takePicture(ImageCapture.OutputFileOptions.Builder(file).build(), worker,
            object : ImageCapture.OnImageSavedCallback {
                override fun onImageSaved(output: ImageCapture.OutputFileResults) {
                    val result = runCatching { PhotoShrink.ensureSmall(file) }
                    main.execute {
                        result.fold({ onSaved(file) }, { onError(it.message ?: "photo could not be saved") })
                    }
                }

                override fun onError(exception: ImageCaptureException) {
                    main.execute { onError(exception.message ?: "capture failed") }
                }
            })
    }
}

/** Last line of defence when the camera ignores the requested size. */
internal object PhotoShrink {
    const val MAX_BYTES = 1_500_000L
    private const val MAX_SIDE = 1600
    private const val QUALITY = 80

    fun ensureSmall(file: File) {
        if (file.length() <= MAX_BYTES) return
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        var sample = 1
        while (maxOf(bounds.outWidth, bounds.outHeight) / (sample * 2) >= MAX_SIDE) sample *= 2
        val decoded = BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample })
            ?: error("photo could not be read")
        val longSide = maxOf(decoded.width, decoded.height)
        val scaled = if (longSide > MAX_SIDE) {
            val f = MAX_SIDE.toFloat() / longSide
            Bitmap.createScaledBitmap(decoded, (decoded.width * f).toInt(), (decoded.height * f).toInt(), true)
        } else decoded
        val degrees = when (ExifInterface(file.path).getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)) {
            ExifInterface.ORIENTATION_ROTATE_90 -> 90f
            ExifInterface.ORIENTATION_ROTATE_180 -> 180f
            ExifInterface.ORIENTATION_ROTATE_270 -> 270f
            else -> 0f
        }
        val upright = if (degrees != 0f) Bitmap.createBitmap(scaled, 0, 0, scaled.width, scaled.height,
            Matrix().apply { postRotate(degrees) }, true) else scaled
        file.outputStream().use { upright.compress(Bitmap.CompressFormat.JPEG, QUALITY, it) }
    }
}

@Composable
fun PhotoPreview(capture: PhotoCapture, modifier: Modifier = Modifier) {
    val owner = LocalLifecycleOwner.current
    Box(modifier) {
        AndroidView(modifier = Modifier.fillMaxSize(), factory = { ctx ->
            PreviewView(ctx).also { view ->
                bind(ctx, owner, view) {
                    val ic = ImageCapture.Builder()
                        .setCaptureMode(ImageCapture.CAPTURE_MODE_MINIMIZE_LATENCY)
                        .setResolutionSelector(ResolutionSelector.Builder()
                            .setResolutionStrategy(ResolutionStrategy(Size(1600, 1200),
                                ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER))
                            .build())
                        .setJpegQuality(80)
                        .build()
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
