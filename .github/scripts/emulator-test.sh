#!/usr/bin/env bash
# Installs and launches the APK on an Android emulator; checks the permission screen, then the
# enrollment screen once the runtime permissions this Android version asks for are granted.
# Then runs the Keystore instrumentation tests (DeviceKeysTest) with a screen-lock PIN set.
# Usage: emulator-test.sh APK TEST_APK API OUTDIR
set -uo pipefail
APK="$1"; TEST_APK="$2"; API="$3"; OUT="$4"; PKG=dz.truckgate.quarry
mkdir -p "$OUT"
R="$OUT/emulator-api$API.txt"
log() { echo "$*" | tee -a "$R"; }
fail=0
check_ui() { # file text label — the accessibility dump can come back before Compose has filled it
  for attempt in 1 2 3; do
    if grep -q "$2" "$1"; then log "PASS  $3"; return; fi
    sleep 4; dump_ui "$1"
  done
  log "FAIL  $3 (text '$2' not on screen)"; fail=1
}
dump_ui() {
  timeout 60 adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || true
  adb pull /sdcard/ui.xml "$1" >/dev/null 2>&1 || echo "<none/>" > "$1"
}
adb wait-for-device
log "== Android API $API: $(adb shell getprop ro.build.version.release | tr -d '\r') ($(adb shell getprop ro.product.cpu.abi | tr -d '\r'))"
if adb install -r "$APK" >>"$R" 2>&1; then log "PASS  install"; else log "FAIL  install"; exit 1; fi
adb shell dumpsys package $PKG | grep -E "versionName|versionCode" | sed 's/^ */  /' | tee -a "$R"

adb logcat -c
timeout 90 adb shell am start -W -n $PKG/.MainActivity >>"$R" 2>&1 || log "  (launch did not report completion within 90 s)"
sleep 8
adb exec-out screencap -p > "$OUT/api$API-1-permissions.png"
dump_ui "$OUT/api$API-1-ui.xml"
check_ui "$OUT/api$API-1-ui.xml" "يحتاج التطبيق إلى الكاميرا" "first launch asks for permissions"

perms="android.permission.CAMERA android.permission.ACCESS_FINE_LOCATION android.permission.ACCESS_COARSE_LOCATION"
if [ "$API" -ge 31 ]; then perms="$perms android.permission.BLUETOOTH_CONNECT"; fi
for p in $perms; do adb shell pm grant $PKG $p >>"$R" 2>&1 && log "  granted $p"; done
adb shell am force-stop $PKG
timeout 90 adb shell am start -W -n $PKG/.MainActivity >>"$R" 2>&1 || log "  (launch did not report completion within 90 s)"
sleep 8
adb exec-out screencap -p > "$OUT/api$API-2-enroll.png"
dump_ui "$OUT/api$API-2-ui.xml"
check_ui "$OUT/api$API-2-ui.xml" "تسجيل الهاتف" "with the permissions of Android $API granted, the enrollment screen opens"

if adb shell pidof $PKG >/dev/null; then log "PASS  app process alive"; else log "FAIL  app process not running"; fail=1; fi
adb logcat -d > "$OUT/api$API-logcat.txt"
if grep -E "FATAL EXCEPTION|AndroidRuntime: Process: $PKG|ANR in $PKG" "$OUT/api$API-logcat.txt" >/dev/null; then
  log "FAIL  crash or ANR in logcat"; grep -E -A25 "FATAL EXCEPTION|ANR in" "$OUT/api$API-logcat.txt" | head -60 | tee -a "$R"; fail=1
else
  log "PASS  no crash in logcat"
fi

# ---- Android Keystore code on this Android version
if adb install -r "$TEST_APK" >>"$R" 2>&1; then log "PASS  install instrumentation tests"; else log "FAIL  install instrumentation tests"; fail=1; fi
if adb shell locksettings set-pin 1111 >>"$R" 2>&1; then log "  screen-lock PIN set (so the fingerprint rule itself is what refuses the ticket key)"; else log "  could not set a PIN on this version (lock-screen rule tested instead)"; fi
adb logcat -c
timeout 300 adb shell am instrument -w -r $PKG.test/androidx.test.runner.AndroidJUnitRunner > "$OUT/api$API-instrumentation.txt" 2>&1
adb logcat -d -s TruckGateTest:I > "$OUT/api$API-keystore.txt" 2>&1
sed 's/^/  /' "$OUT/api$API-keystore.txt" | grep -v "^  ---" | tee -a "$R"
if grep -q "INSTRUMENTATION_CODE: -1" "$OUT/api$API-instrumentation.txt" && ! grep -q "FAILURES!!!" "$OUT/api$API-instrumentation.txt"; then
  log "PASS  Keystore tests: $(grep -E '^OK \(' "$OUT/api$API-instrumentation.txt" | tr -d '\r')"
else
  log "FAIL  Keystore tests"; grep -E "INSTRUMENTATION_STATUS: stack|Error|Exception" "$OUT/api$API-instrumentation.txt" | head -20 | tee -a "$R"; fail=1
fi
adb shell locksettings clear --old 1111 >/dev/null 2>&1 || true
adb uninstall $PKG.test >>"$R" 2>&1
adb uninstall $PKG >>"$R" 2>&1 && log "PASS  uninstall"
exit $fail
