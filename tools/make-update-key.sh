#!/usr/bin/env bash
# Makes a new key pair for signing update manifests (design/10-自动更新.md §5.2).
#
#   tools/make-update-key.sh <out-dir>
#
# Writes <out-dir>/update-signing-key.pem (the private key: keep it secret, never commit it) and prints the public key.
# Then:
#   1. Put the whole .pem file into the repository secret UPDATE_SIGNING_KEY.
#   2. Add the printed public key to UpdateEndpoints.PublicKeys (src/Octopus.Updates/UpdateEndpoints.cs). To replace a
#      key, ship a release that trusts both keys first, and sign with the new key only after that.
set -euo pipefail

OUT="${1:?usage: make-update-key.sh <out-dir>}"
mkdir -p "$OUT"
KEY="$OUT/update-signing-key.pem"
[ ! -e "$KEY" ] || { echo "$KEY already exists" >&2; exit 1; }

( umask 077; openssl ecparam -name prime256v1 -genkey -noout -out "$KEY" )
echo "private key: $KEY"
echo "public key (SubjectPublicKeyInfo, base64):"
openssl ec -in "$KEY" -pubout -outform DER 2>/dev/null | base64 | tr -d '\n'
echo
