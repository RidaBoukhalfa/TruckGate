#!/usr/bin/env bash
# Inspects a built APK with the Android SDK build tools; prints a report, exits non-zero if a check fails.
set -euo pipefail
APK="$1"
BT="$ANDROID_HOME/build-tools/$(ls "$ANDROID_HOME/build-tools" | sort -V | tail -1)"
fail=0
echo "== APK: $(basename "$APK")"
echo "size_bytes=$(stat -c %s "$APK")"
echo "sha256=$(sha256sum "$APK" | cut -d' ' -f1)"
echo "build_tools=$(basename "$BT")"
echo
echo "== aapt2 dump badging"
"$BT/aapt2" dump badging "$APK" | grep -E "^(package|sdkVersion|targetSdkVersion|application-label|launchable-activity|uses-permission|native-code|uses-feature)" || true
badging=$("$BT/aapt2" dump badging "$APK")
grep -q "package: name='dz.truckgate.quarry'" <<<"$badging" || { echo "FAIL package name"; fail=1; }
grep -q "versionName='1.0.0'" <<<"$badging" || { echo "FAIL versionName"; fail=1; }
grep -Eq "(sdkVersion|minSdkVersion):'30'" <<<"$badging" || { echo "FAIL minSdk"; fail=1; }
echo
echo "== apksigner verify"
"$BT/apksigner" verify --verbose --print-certs "$APK" | grep -vE "^WARNING: META-INF" || { echo "FAIL signature"; fail=1; }
echo
echo "== debuggable flag"
"$BT/aapt2" dump xmltree "$APK" --file AndroidManifest.xml | grep -i "debuggable" || echo "(not debuggable)"
echo
echo "== contents"
unzip -l "$APK" | awk 'NR>3 && $4 != "" {print $4}' | sed -E 's#^(lib/[^/]+)/.*#\1/*#; s#^(res)/.*#\1/*#; s#^(assets)/.*#\1/*#; s#^(META-INF)/.*#\1/*#; s#^(kotlin)/.*#\1/*#' | sort | uniq -c
echo
echo "== secrets / development addresses in code and resources (expect none)"
tmp=$(mktemp -d)
unzip -q "$APK" -d "$tmp"
hits=$(find "$tmp" -type f \( -name '*.dex' -o -name 'resources.arsc' -o -name '*.xml' -o -name '*.json' -o -name '*.properties' \) -print0 |
  xargs -0 strings -a | grep -E "10\.0\.2\.2|CHANGE_ME|BEGIN (EC |RSA )?PRIVATE KEY|truckgate_app|Password=" | sort -u || true)
if [ -n "$hits" ]; then echo "$hits"; echo "FAIL suspicious strings"; fail=1; else echo "none found"; fi
rm -rf "$tmp"
exit $fail
