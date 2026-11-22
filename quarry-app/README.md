# TruckGate — quarry app

Android app used by the quarry operator to issue signed transport tickets and print them.

| Module | What | Runs on |
|---|---|---|
| `ticket-format` | Ticket bytes, Base45 QR text, signatures (shared with the server via `ticket-spec`) | any JVM |
| `quarry-core` | Issuance rules (20-minute truck rule, GPS time, geofence, sync deadline), printer-agnostic ESC/POS encoder, enrollment QR, signed config, PIN check | any JVM |
| `app` | Android UI, Keystore keys, fingerprint, GPS, camera, Bluetooth printing, local storage | Android 11+ |

## Phone requirements

- Android 11 or newer, fingerprint sensor (strong biometric), GPS, camera, Bluetooth.
- Prefer a phone whose Keystore has StrongBox (hardware security chip); the app uses it automatically.

## Printer

Any **ESC/POS** thermal printer over **Bluetooth Classic (SPP)**, 58 mm or 80 mm. Everything is
printed as an image (`GS v 0`), so Arabic text and the QR code do not depend on the printer's fonts
or QR support. Pair the printer in Android Bluetooth settings, then choose it in the app's settings
and run a test print. Keep a second, already-paired printer at the quarry.

## Provisioning a phone (dedicated device)

1. Factory-reset the phone. Do **not** add a Google account.
2. Enable developer options and USB debugging, install the APK, then make the app device owner:
   `adb shell dpm set-device-owner dz.truckgate.quarry/.kiosk.KioskAdminReceiver`
   The app then locks the screen to itself (no home button, no other apps).
3. Enroll the operator fingerprint(s) **before** enrolling the app. Adding a fingerprint later
   destroys the ticket key on purpose; the phone must then be enrolled again.
4. On the factory admin screen create an enrollment code; scan it with the app on the factory Wi-Fi.
   The app creates its keys, registers them and downloads its signed configuration.

## Security notes

- Ticket key: ECDSA P-256 in Android Keystore, non-exportable, **one fingerprint per signature**.
- Auth key: separate Keystore key for background requests (configuration, later sync).
- The configuration (trucks, drivers, operators, geofence, rules) is signed by the server; the
  server key is pinned from the enrollment QR. Older configurations are refused.
- Operator PINs are checked offline against PBKDF2 hashes from the signed configuration;
  5 wrong PINs lock the login for 60 seconds.
- Ticket numbers are saved before printing and never reused; tickets are never deleted.
- Until ticket upload (step 6) exists, the 24-hour rule is measured from the last configuration refresh.
