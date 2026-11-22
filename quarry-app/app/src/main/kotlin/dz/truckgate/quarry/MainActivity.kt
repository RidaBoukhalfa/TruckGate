package dz.truckgate.quarry

import android.Manifest
import android.app.ActivityManager
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.pm.PackageManager
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

    private val needed = arrayOf(
        Manifest.permission.CAMERA,
        Manifest.permission.ACCESS_FINE_LOCATION,
        Manifest.permission.BLUETOOTH_CONNECT,
    )

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
