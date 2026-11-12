#!/bin/sh
# Removes this Octopus install: the launcher entry, the PATH link, and this directory.
set -eu
DIR="$(cd "$(dirname "$0")" && pwd)"
MANIFEST="$DIR/.octopus-install"
[ -f "$MANIFEST" ] && [ -x "$DIR/Octopus" ] || { echo "$DIR does not look like an Octopus install; nothing removed." >&2; exit 1; }

link="$(sed -n 's/^link=//p' "$MANIFEST")"
entry="$(sed -n 's/^entry=//p' "$MANIFEST")"
# Only remove the link if it still points at this install.
if [ -n "$link" ] && [ -L "$link" ] && [ "$(readlink "$link")" = "$DIR/Octopus" ]; then rm -f "$link"; fi
[ -z "$entry" ] || rm -f "$entry"
rm -rf "$DIR"
echo "Octopus removed from $DIR"
