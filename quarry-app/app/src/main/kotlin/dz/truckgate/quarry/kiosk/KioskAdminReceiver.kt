package dz.truckgate.quarry.kiosk

import android.app.admin.DeviceAdminReceiver

/**
 * Makes the app eligible as device owner, so the phone can be locked to it (kiosk).
 * Provisioning: `adb shell dpm set-device-owner dz.truckgate.quarry/.kiosk.KioskAdminReceiver`
 * on a freshly reset phone with no accounts (see quarry-app/README.md).
 */
class KioskAdminReceiver : DeviceAdminReceiver()
