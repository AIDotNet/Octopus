#!/usr/bin/env bash
# Builds the self-extracting installer Octopus-<version>-linux-x64.run (asks for the install directory).
#
#   tools/package-linux.sh <version> <publish-dir> <out-dir>
#
# <publish-dir> is the self-contained linux-x64 publish output of src/Octopus.App.
set -euo pipefail

VERSION="${1:?usage: package-linux.sh <version> <publish-dir> <out-dir>}"
PUBLISH="${2:?usage: package-linux.sh <version> <publish-dir> <out-dir>}"
OUT="${3:?usage: package-linux.sh <version> <publish-dir> <out-dir>}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

mkdir -p "$WORK/payload"
cp -R "$PUBLISH/." "$WORK/payload/"
rm -f "$WORK/payload/"*.dbg "$WORK/payload/"*.pdb "$WORK/payload/"*.xml
cp "$ROOT/src/Octopus.App/Assets/Icon/octopus.png" "$WORK/payload/octopus.png"
cp "$ROOT/packaging/linux/uninstall.sh" "$WORK/payload/uninstall.sh"
chmod +x "$WORK/payload/Octopus" "$WORK/payload/uninstall.sh"
XZ_OPT="-6 -T0" tar -C "$WORK/payload" -cJf "$WORK/payload.tar.xz" .

RUN="$OUT/Octopus-$VERSION-linux-x64.run"
sed "s|@VERSION@|$VERSION|g" "$ROOT/packaging/linux/install-header.sh" > "$RUN"
cat "$WORK/payload.tar.xz" >> "$RUN"
chmod +x "$RUN"
echo "==> $RUN"
