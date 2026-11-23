package dz.truckgate.quarry

import android.Manifest
import android.app.ActivityManager
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import dz.truckgate.quarry.kiosk.KioskAdminReceiver
import dz.truckgate.quarry.ui.QuarryApp

/** FragmentActivity because BiometricPrompt needs one. */
class MainActivity : FragmentActivity() {
    private val vm: QuarryViewModel by viewModels()

    // BLUETOOTH_CONNECT is a runtime permission only from Android 12 (API 31); on Android 11 the
    // manifest's BLUETOOTH permission is enough. Coarse location is asked together with fine, as
    // Android 12+ requires, but precise (fine) location is what the quarry boundary check needs.
    private val needed: Array<String> = buildList {
        add(Manifest.permission.CAMERA)
        add(Manifest.permission.ACCESS_FINE_LOCATION)
        add(Manifest.permission.ACCESS_COARSE_LOCATION)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) add(Manifest.permission.BLUETOOTH_CONNECT)
    }.toTypedArray()

    private var granted by mutableStateOf(false)

    private val request = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        checkPermissions()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        checkPermissions()
        setContent {
            QuarryApp(vm, this, granted) { request.launch(needed) }
        }
    }

    override fun onResume() {
        super.onResume()
        checkPermissions()
        enterKioskIfAllowed()
    }

    private fun checkPermissions() {
        granted = needed.all { ContextCompat.checkSelfPermission(this, it) == PackageManager.PERMISSION_GRANTED }
        if (granted) vm.onPermissionsGranted()
    }

    /**
     * When the phone is provisioned as a dedicated device (this app is device owner), lock the
     * screen to this app: no home button, no other apps, no settings.
     */
    private fun enterKioskIfAllowed() {
        val dpm = getSystemService(DevicePolicyManager::class.java) ?: return
        if (dpm.isDeviceOwnerApp(packageName)) {
            dpm.setLockTaskPackages(ComponentName(this, KioskAdminReceiver::class.java), arrayOf(packageName))
        }
        val am = getSystemService(ActivityManager::class.java) ?: return
        if (dpm.isLockTaskPermitted(packageName) && am.lockTaskModeState == ActivityManager.LOCK_TASK_MODE_NONE) {
            startLockTask()
        }
    }
}
