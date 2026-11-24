#!/usr/bin/env bash
# Makes the keystore that signs the Android APK of every release (design/10-自动更新.md §15.1).
#
#   tools/make-android-keystore.sh <out-dir>
#
# Android installs a new APK over the old one only when both are signed with the same key, so this key is made once and
# kept for as long as the app exists: losing it means every phone has to uninstall Octopus to get the next version.
# Writes <out-dir>/octopus-release.keystore (PKCS12, never commit it) and <out-dir>/secrets.txt with what goes into the
# repository secrets:
#   ANDROID_KEYSTORE           the keystore, base64
#   ANDROID_KEYSTORE_PASSWORD  the password of the keystore (and of the key: PKCS12 has one for both)
#   ANDROID_KEY_ALIAS          the key's alias: octopus
# Keep a copy of the keystore and its password outside GitHub too (a password manager): a secret cannot be read back.
set -euo pipefail

OUT="${1:?usage: make-android-keystore.sh <out-dir>}"
command -v keytool >/dev/null || { echo "keytool not found: install a JDK (17 or newer)" >&2; exit 1; }
mkdir -p "$OUT"
STORE="$OUT/octopus-release.keystore"
SECRETS="$OUT/secrets.txt"
[ ! -e "$STORE" ] || { echo "$STORE already exists" >&2; exit 1; }

ALIAS=octopus
PASSWORD="$(openssl rand -base64 32 | tr -d '/+=\n' | cut -c1-32)"

( umask 077
  # 30 years: the key outlives any plan to replace it (Android has no way to roll an APK key over for side-loaded apps).
  keytool -genkeypair -keystore "$STORE" -storetype PKCS12 -storepass "$PASSWORD" -keypass "$PASSWORD" \
          -alias "$ALIAS" -keyalg RSA -keysize 4096 -validity 10950 \
          -dname "CN=Octopus, O=AIDotNet" >/dev/null
  {
    echo "ANDROID_KEY_ALIAS=$ALIAS"
    echo "ANDROID_KEYSTORE_PASSWORD=$PASSWORD"
    printf 'ANDROID_KEYSTORE='
    base64 < "$STORE" | tr -d '\n'
    echo
  } > "$SECRETS" )

echo "keystore: $STORE"
echo "secrets:  $SECRETS (three lines: put each value into the repository secret of that name, then delete the file)"
echo "certificate SHA-256:"
keytool -list -keystore "$STORE" -storepass "$PASSWORD" -alias "$ALIAS" -v | grep -m1 'SHA256:' | sed 's/^[[:space:]]*//'
