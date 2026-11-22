# TruckGate — Build report (server/installer 1.0.0, quarry app 1.0.1)

| | |
|---|---|
| Build date | 2026-11-22, finished 15:02 UTC |
| Source | commit `5a3125b0b58907d6277a6b96f32bf2cce8c76656` (branch `step-5-exit-review-alerts`, pushed also as `release/v1.0.0`) |
| Built by | GitHub Actions, workflow `.github/workflows/release.yml`, run 45034786648 (attempt 2, see §5) — all 8 jobs green |
| Published as | **draft** release `v1.0.0-build.10` (visible only to repository collaborators; not published) |
| Replaces | build 8 (Android app 1.0.0, Android 11+ only: would not install on the first field phone, "There was a problem parsing the package") |

## 1. Files

| File | Size | SHA-256 |
|---|---|---|
| `release/TruckGate-Quarry.apk` | 11,889,636 bytes | `82c75c752d3bf830fbe90455fd3435197534c28caff4661cf8b315d880b3e23f` |
| `release/TruckGate-Windows-Setup.exe` | 84,984,438 bytes | `920fa31c0e72c7bfceaae7285b767ee09081158ffcc7f9509b8e2994da288e83` |
| `release/INSTALLATION_GUIDE.md` | — | (also inside the installer, `docs\`) |
| `release/BUILD_REPORT.md` | — | this file |

The two binaries were downloaded from the draft release and their SHA-256 re-computed: identical to the values computed on the CI runners. They are not committed to git (`.gitignore`).

Check on Windows: `Get-FileHash .\TruckGate-Windows-Setup.exe -Algorithm SHA256`. On Linux/macOS: `sha256sum` / `shasum -a 256`.

## 2. What is inside

**Android APK** — package `dz.truckgate.quarry`, versionName **1.0.1**, versionCode 101, **minSdk 26 (Android 8.0)**, targetSdk/compileSdk 35, native libraries for arm64-v8a, armeabi-v7a, x86, x86_64. **Debug build** (see §6): signed with the CI runner's Android debug key, v2 signature scheme, `debuggable=true`. Certificate SHA-256 `1c2a74996f58cc4d823f3c668d7ec4e33336e3b66ee4615dcf6f1da9c84fd818`. It is smaller than the 1.0.0 file (11.9 MB instead of 28.5 MB) only because the Android build tools store the code compressed when the app supports Android versions before 9; the content is the same plus the changes below.

Ticket signing on Android 8–10: the ticket key still needs a fingerprint for **every** signature. Android 11+ uses `setUserAuthenticationParameters(0, BIOMETRIC_STRONG)`; Android 8–10 use "authentication for every use" (validity −1), which before Android 11 only a fingerprint through the biometric prompt can unlock — the screen PIN cannot. Differences on older phones: StrongBox (a separate security chip) only from Android 9 and only on some phones; a phone that cannot produce a key attestation certificate still creates the key in Android Keystore, and the server stores the attestation as missing.

**Windows installer** — unchanged in function since build 8 (only the embedded installation guide now says Android 8.0+); Inno Setup 6.7.1; the setup program is a 32-bit (x86) loader that refuses non-x64 Windows and installs 64-bit files (2,145 files for the server type). Not code-signed. Contents:

| Component | Version / source |
|---|---|
| TruckGate server `TruckGate.Api.exe` | 1.0.0, .NET 10, `win-x64`, self-contained (no .NET installation needed), web UI in `wwwroot` |
| PostgreSQL | 16.16 Windows x64 binaries from EnterpriseDB (`bin`, `lib`, `share` only — no pgAdmin, no StackBuilder). `postgres.exe` Authenticode signature valid, signer *EnterpriseDB Corporation*. Source zip SHA-256 `ADD1DE26…2170C073` (EDB publishes no checksum; recorded here) |
| Microsoft Visual C++ 2015-2022 runtime x64 | 14.44.35211.0, signature valid (*Microsoft Corporation*); installed only if missing or older than 14.40 |
| Setup scripts | `deploy/windows/scripts` (PowerShell 5.1), `THIRD-PARTY-NOTICES.txt`, installation guide |

No password, API secret or private key is in either file. Database passwords are generated on the target PC at install time; the server signing key is created by the server on first use. The development settings file (`appsettings.Development.json`, which holds development-only passwords) is excluded from the published server and the CI checks that no password appears in the program folder. The APK was scanned for development addresses, placeholder credentials and private keys: none found.

## 3. How it was built

Everything is in `.github/workflows/release.yml`; the main commands:

```bash
# Android (ubuntu-latest, JDK 17 Temurin, Gradle wrapper of the project, build-tools 37.0.0)
cd quarry-app
./gradlew :ticket-format:test :quarry-core:test --no-daemon
./gradlew :app:assembleDebug :app:assembleDebugAndroidTest :app:lintDebug --no-daemon --stacktrace
# release APK only when the repository has signing secrets (it has none):
./gradlew :app:assembleRelease          # env TRUCKGATE_KEYSTORE, ..._PASSWORD, TRUCKGATE_KEY_ALIAS, TRUCKGATE_KEY_PASSWORD
aapt2 dump badging app-debug.apk; apksigner verify --verbose --print-certs app-debug.apk
```

```powershell
# Windows (windows-latest = Windows Server 2025, Node 22, .NET SDK 11.0.100 building net10.0)
cd web; npm ci; npm run build; cd ..
dotnet publish server/src/TruckGate.Api/TruckGate.Api.csproj -c Release -r win-x64 --self-contained true -p:Version=1.0.0 -o out/server
# + PostgreSQL 16.16 zip and vc_redist.x64.exe downloaded, signatures and version checked
ISCC.exe /DAppVersion=1.0.0 /DSourceRoot=..\..\out deploy\windows\TruckGate.iss
```

## 4. Tests that were run, and their results

### Android
| Test | Where | Result |
|---|---|---|
| Ticket-format conformance (shared test vectors with the Python reference and the C# server: tickets, short codes, voids, gate receipts) | CI, JUnit | pass — 207 checks |
| Quarry rules (geofence, issuance rules, ESC/POS printing, enrollment code, PIN hashes, signed configuration, **new: server-address QR**) | CI, JUnit | pass — 54 checks |
| API-level check (Android lint `NewApi`/`InlinedApi` on the app and its modules: no call that is missing on Android 8.0) | CI | pass — "No issues found" |
| APK inspection: package, version, minSdk 26, signature verification, contents, no secrets | CI, aapt2/apksigner | pass |
| On each emulator: install → first launch shows the permission screen → the permissions of that Android version granted → enrollment screen → no crash/ANR → uninstall | emulators **Android 8.0 (API 26), 9 (28), 10 (29), 11 (30), 14 (34)**, x86_64 | pass on all five (screenshots in the draft release) |
| On each emulator: Android Keystore code of the app (instrumentation tests): the auth key is created in Keystore with an attestation chain (3 certificates) and its signatures verify the way the server checks them; with a screen-lock PIN set and no fingerprint enrolled, the platform **refuses** to create the ticket key ("At least one fingerprint/biometric must be enrolled to create keys requiring user authentication for every use") — proof that the ticket key cannot exist without a fingerprint rule on that version | same five emulators | pass — 2/2 on each |

### Windows installer — real installation on the CI Windows machine (35 checks, all pass)
Machine: Windows Server 2025 Datacenter 10.0.26100, English, Windows PowerShell 5.1, administrator.

1. **Fresh install** (silent, type *server*): exit code 0; `TruckGate.Api.exe` and `postgres.exe` are x64; service **TruckGateDB** running as NetworkService, automatic; service **TruckGate** running as `NT SERVICE\TruckGate`, automatic, depends on TruckGateDB; `/health` = ok, database ok, version 1.0.0; web UI and its script served; phone address detected = this PC's IPv4 and reachable on it; database listening on 127.0.0.1:5433 only; secrets, configuration, database and data folders not readable by Users / Everyone / Authenticated Users; no password in the program folder; firewall rule TCP 5080 from the local subnet; backup task registered.
2. **First administrator** via the console command; stored name verified in the database; login OK; wrong password → 401; phone enrollment code created (server key created by the service, address = detected one); server-address QR signed; dashboard answers; a state-changing call without the CSRF header → 403.
3. **Web UI in Chrome** (headless): sign in, gate page, phones page with the server-address QR, dashboard — no JavaScript errors (screenshots in the draft release). **Backup now**: zip with database dump and server keys. **Change server address**: `localhost` refused with a clear reason; an address not belonging to this PC refused; a valid LAN address saved, service restarted, healthy.
4. **Upgrade** (installer run again over the installation): exit 0, healthy, same administrator, same server key.
5. **Uninstall**: exit 0, services, firewall rule and program folder removed, **data kept**.
6. **Reinstall on the kept data**: healthy, same administrator and server key; uninstalled again.
7. **Gate-station-only install** with a server address: no services installed, address saved; an unreachable server reported clearly ("No answer from … within 6 s. Check the network cable/Wi-Fi and the IP address of the server."); uninstall OK.

### Server test suites
| Suite | Linux CI (PostgreSQL 16 container) | Windows CI (bundled PostgreSQL 16.16 binaries) |
|---|---|---|
| Crypto (ticket/receipt vectors) | 7/7 pass | 7/7 pass |
| Server integration (gate, exit, review, alerts incl. simulated ZTE router SMS and WhatsApp API, sync, pay, HTTP incl. signed phone sync and the new server-address QR and `/health`) | 95/95 pass | 95/95 pass |
| Web UI TypeScript check + build | pass | (built for the installer) |

## 5. Problems found during this build, and what was done

| Problem | Fix |
|---|---|
| Android 11: the app asked for `BLUETOOTH_CONNECT`, which does not exist as a runtime permission before Android 12, so the app would never leave the permission screen on Android 11 | Asked only on Android 12+, coarse location asked with fine. Verified on the API 30 emulator |
| The 1.0.0 APK required Android 11; the first field phone is older ("There was a problem parsing the package") | App 1.0.1 supports Android 8.0+ with the same fingerprint-per-ticket rule (§2) |
| Found by the new Android 8.0 emulator test: on Android 8–10 the app froze after the permissions were granted — the GPS listener did not implement three callbacks that are only optional from Android 11, so the GPS status callback failed | All callbacks implemented; Android 8.0 emulator passes |
| Android 9 emulator, first attempt of the final run: the screen dump came back empty although the screenshot showed the enrollment screen (test-harness timing) | Failed job re-run on the same build: pass. The test now retries the dump (committed after this build) |
| `appsettings.Development.json` (development passwords) would have been copied into the installer | Excluded from publishing; CI checks it |
| Installer: setting folder permissions in two steps left the data folder without any access in between; when the setup log could not be written there, server setup stopped (installer exit code 10) | Permissions set in one step; a log write can no longer stop the setup; a second setup log is written to the installing user's TEMP folder |
| Inno Setup script compile error (Pascal Script type mismatch) | Fixed |
| CI: Gradle failures could be hidden by `| tee`; Maven Central answered 429 once | `bash -o pipefail` for all steps; one retry |
| CI: test-harness issues (a test PostgreSQL holding the step's output pipe, Windows PowerShell cookie handling, `Start-Process -Wait` with the uninstaller) | Fixed in the test scripts. Login was also verified with a real browser (Chrome) |

Build warnings: 1 — `CA1416` (Windows-only `EventLogSettings` used inside an `IsWindows()` check through a lambda; harmless). Kotlin: none.

## 6. Limitations — read before testing

**Not tested with real hardware** (only simulated, or not at all):
- A physical Android phone: signing a ticket with a real fingerprint (emulators have no enrolled fingerprint — the tests prove only that the ticket key cannot be created without one), key attestation on real hardware, the fallback for phones that cannot attest, scanning a QR with the real camera, GPS fix and quarry boundary, the Bluetooth thermal ticket printer.
- Android versions: tested only on emulators (8.0, 9, 10, 11, 14). Manufacturer builds (MIUI, One UI…) were not tested; Android 7 and older are not supported.
- IP cameras / NVR (ONVIF discovery and snapshots were tested against a simulated camera only), the USB barcode scanner, the gate receipt printer.
- SMS through the ZTE G5C router (tested against a simulation of its web interface only), WhatsApp Cloud API and Telegram (tested against simulated APIs only).
- End-to-end with a phone: enrolling a real phone, issuing a ticket, printing, scanning it at the gate. The protocol (enrollment, signed sync, signed configuration) is covered by the server's HTTP tests with a simulated phone.

**Windows:**
- Tested on GitHub's Windows Server 2025 virtual machine (English, administrator, Visual C++ runtime already present). **Not** tested on a Windows 10/11 desktop, on French/Arabic Windows (accounts are referenced by SID to avoid language problems), with an antivirus, or with SmartScreen.
- Tested silently (`/VERYSILENT`). The wizard pages (address page with its connection test) and the graphical "Change server address" window were not clicked through; their logic was tested through the same scripts in non-interactive mode.
- The Visual C++ runtime installation step did not run (already installed on the test machine).
- The installer is **not code-signed**: SmartScreen will warn. A code-signing certificate is needed to remove the warning.
- Restoring a backup is not automated and was not tested (creating one was). Camera pictures are not in the backup.
- PostgreSQL major-version upgrades are not handled by the installer (16.x minor updates are).
- `innoextract` cannot read Inno Setup 6.7.1 installers, so the content was verified by installing it (2,145 files) rather than by listing.

**Android APK is a debug build:**
- Signed with the CI runner's temporary debug key, which changes from one CI build to the next: installing a later build over this one will fail — the app has to be uninstalled first, which **deletes unsynced tickets on the phone**. Sync first.
- `debuggable=true` (lets someone with USB debugging inspect the app's data; the phone's signing keys stay in the Android Keystore).
- For a release APK: create a keystore once (`keytool -genkeypair -v -keystore truckgate-release.jks -keyalg RSA -keysize 3072 -validity 10000 -alias truckgate`), keep it and its passwords safe and backed up (losing it means phones can never be updated), and add four repository secrets: `TRUCKGATE_KEYSTORE_B64` (the file in base64), `TRUCKGATE_KEYSTORE_PASSWORD`, `TRUCKGATE_KEY_ALIAS`, `TRUCKGATE_KEY_PASSWORD`. The workflow then builds a signed release APK automatically. No key was invented or committed for this build.

**Also:** the GitHub release is a **draft**; nothing was published. PR #6 (steps 5–6 and this build work) is still open and not merged.

## 7. Where things are

| What | Where |
|---|---|
| The two files, this report, the guide | `release/` in the repository working copy; the binaries also in the draft release `v1.0.0-build.10` |
| Test evidence (logs, screenshots, emulator UI dumps, Keystore test output, test results) | draft release: `evidence-android.zip` (incl. lint report), `evidence-emulator-api26/28/29/30/34.zip`, `evidence-windows.zip`, `ci-status.txt`, `SHA256SUMS.txt` |
| Installer sources | `deploy/windows/` (`TruckGate.iss`, `scripts/`) |
| Build and test automation | `.github/workflows/release.yml`, `.github/scripts/` |
| After installing on Windows | program `C:\Program Files\TruckGate`, data `C:\ProgramData\TruckGate` (logs in `logs\`, setup log `logs\install.log` and `%TEMP%\truckgate-install.log`) |
