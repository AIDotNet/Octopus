#!/usr/bin/env bash
# Builds Octopus.app + a .dmg for one macOS runtime id from a self-contained publish, and a .zip of the same app for
# automatic updates (design/10-自动更新.md §5.3).
#
#   tools/package-macos.sh <rid: osx-arm64|osx-x64> <version> <out-dir>
#
# Environment:
#   SIGN_IDENTITY   codesign identity (a Developer ID Application hash/name). "-" or unset = ad-hoc signature.
#   NOTARIZE=1      notarize + staple the app and the dmg (needs a real SIGN_IDENTITY and the three variables below).
#   APPLE_ID, APPLE_TEAM_ID, APPLE_APP_SPECIFIC_PASSWORD
#   BUNDLE_ID       default com.aidotnet.octopus
set -euo pipefail

RID="${1:?usage: package-macos.sh <rid> <version> <out-dir>}"
VERSION="${2:?usage: package-macos.sh <rid> <version> <out-dir>}"
OUT="${3:?usage: package-macos.sh <rid> <version> <out-dir>}"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
BUNDLE_ID="${BUNDLE_ID:-com.aidotnet.octopus}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

# CFBundleVersion must be dotted numbers: drop any -prerelease / +build suffix.
NUMERIC_VERSION="${VERSION%%[-+]*}"

echo "==> publish ($RID)"
# Single-file keeps the ~250 managed .dll files inside the executable. Loose .dlls in Contents/MacOS would each count
# as unsigned nested code and fail verification / notarization; the native libraries stay beside it as real Mach-O files.
dotnet publish "$ROOT/src/Octopus.App" -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:Version="$VERSION" -p:DebugType=None -p:DebugSymbols=false -o "$WORK/publish"

echo "==> assemble Octopus.app"
APP="$WORK/Octopus.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$WORK/publish/." "$APP/Contents/MacOS/"
# Static archives are link-time leftovers: not needed at run time, and not signable.
find "$APP" -name '*.a' -delete
cp "$ROOT/src/Octopus.App/Assets/Icon/octopus.icns" "$APP/Contents/Resources/octopus.icns"
sed -e "s|@BUNDLE_ID@|$BUNDLE_ID|g" -e "s|@VERSION@|$NUMERIC_VERSION|g" \
  "$ROOT/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/Octopus"

echo "==> codesign ($([ "$SIGN_IDENTITY" = "-" ] && echo ad-hoc || echo "$SIGN_IDENTITY"))"
if [ "$SIGN_IDENTITY" = "-" ]; then TIMESTAMP=(--timestamp=none); else TIMESTAMP=(--timestamp); fi
# Inner code first (dylibs, createdump, ...), then the bundle itself, which also signs the main executable.
while IFS= read -r -d '' f; do
  if file -b "$f" | grep -q 'Mach-O'; then
    codesign --force --options runtime "${TIMESTAMP[@]}" --sign "$SIGN_IDENTITY" "$f"
  fi
done < <(find "$APP/Contents" -type f ! -path "$APP/Contents/MacOS/Octopus" -print0)
codesign --force --options runtime "${TIMESTAMP[@]}" \
  --entitlements "$ROOT/packaging/macos/entitlements.plist" --sign "$SIGN_IDENTITY" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

notarize() { # <file> : submit, wait, fail loudly with Apple's log when not accepted
  local result status id
  result="$(xcrun notarytool submit "$1" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
    --password "$APPLE_APP_SPECIFIC_PASSWORD" --wait --output-format json)"
  status="$(plutil -extract status raw -o - - <<<"$result")"
  id="$(plutil -extract id raw -o - - <<<"$result")"
  echo "notarization $id: $status"
  if [ "$status" != "Accepted" ]; then
    xcrun notarytool log "$id" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
      --password "$APPLE_APP_SPECIFIC_PASSWORD" || true
    exit 1
  fi
}

if [ "${NOTARIZE:-0}" = "1" ]; then
  [ "$SIGN_IDENTITY" != "-" ] || { echo "NOTARIZE=1 needs a real SIGN_IDENTITY" >&2; exit 1; }
  echo "==> notarize app"
  ditto -c -k --keepParent "$APP" "$WORK/Octopus.zip"
  notarize "$WORK/Octopus.zip"
  xcrun stapler staple "$APP"
fi

# The update package: the final app (notarized and stapled when NOTARIZE=1), zipped so ditto -x restores it exactly.
echo "==> update zip"
ZIP="$OUT/Octopus-$VERSION-$RID.zip"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

echo "==> dmg"
DMG="$OUT/Octopus-$VERSION-$RID.dmg"
STAGE="$WORK/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -volname "Octopus" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG"
if [ "$SIGN_IDENTITY" != "-" ]; then
  codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
fi
if [ "${NOTARIZE:-0}" = "1" ]; then
  echo "==> notarize dmg"
  notarize "$DMG"
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$DMG"
fi

echo "==> $DMG"
echo "==> $ZIP"
