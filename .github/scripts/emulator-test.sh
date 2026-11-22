#!/usr/bin/env bash
# Installs and launches the APK on an Android emulator; checks the permission screen, then the
# enrollment screen once the runtime permissions this Android version asks for are granted.
# Usage: emulator-test.sh APK API OUTDIR
set -uo pipefail
APK="$1"; API="$2"; OUT="$3"; PKG=dz.truckgate.quarry
mkdir -p "$OUT"
R="$OUT/emulator-api$API.txt"
log() { echo "$*" | tee -a "$R"; }
fail=0
check_ui() { # file text label
  if grep -q "$2" "$1"; then log "PASS  $3"; else log "FAIL  $3 (text '$2' not on screen)"; fail=1; fi
}
dump_ui() {
  adb shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1 || true
  adb pull /sdcard/ui.xml "$1" >/dev/null 2>&1 || echo "<none/>" > "$1"
}
adb wait-for-device
log "== Android API $API: $(adb shell getprop ro.build.version.release | tr -d '\r') ($(adb shell getprop ro.product.cpu.abi | tr -d '\r'))"
if adb install -r "$APK" >>"$R" 2>&1; then log "PASS  install"; else log "FAIL  install"; exit 1; fi
adb shell dumpsys package $PKG | grep -E "versionName|versionCode" | sed 's/^ */  /' | tee -a "$R"

adb logcat -c
adb shell am start -W -n $PKG/.MainActivity >>"$R" 2>&1
sleep 8
adb exec-out screencap -p > "$OUT/api$API-1-permissions.png"
dump_ui "$OUT/api$API-1-ui.xml"
check_ui "$OUT/api$API-1-ui.xml" "يحتاج التطبيق إلى الكاميرا" "first launch asks for permissions"

perms="android.permission.CAMERA android.permission.ACCESS_FINE_LOCATION android.permission.ACCESS_COARSE_LOCATION"
if [ "$API" -ge 31 ]; then perms="$perms android.permission.BLUETOOTH_CONNECT"; fi
for p in $perms; do adb shell pm grant $PKG $p >>"$R" 2>&1 && log "  granted $p"; done
adb shell am force-stop $PKG
adb shell am start -W -n $PKG/.MainActivity >>"$R" 2>&1
sleep 8
adb exec-out screencap -p > "$OUT/api$API-2-enroll.png"
dump_ui "$OUT/api$API-2-ui.xml"
check_ui "$OUT/api$API-2-ui.xml" "تسجيل الهاتف" "with the permissions of Android $API granted, the enrollment screen opens"

if adb shell pidof $PKG >/dev/null; then log "PASS  app process alive"; else log "FAIL  app process not running"; fail=1; fi
adb logcat -d > "$OUT/api$API-logcat.txt"
if grep -E "FATAL EXCEPTION|AndroidRuntime: Process: $PKG" "$OUT/api$API-logcat.txt" >/dev/null; then
  log "FAIL  crash in logcat"; grep -A20 "FATAL EXCEPTION" "$OUT/api$API-logcat.txt" | head -40 | tee -a "$R"; fail=1
else
  log "PASS  no crash in logcat"
fi
adb uninstall $PKG >>"$R" 2>&1 && log "PASS  uninstall"
exit $fail
