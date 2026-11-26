#!/bin/sh
# Removes this Octopus install: the launcher entry, the PATH link, and this directory.
set -eu
DIR="$(cd "$(dirname "$0")" && pwd)"
MANIFEST="$DIR/.octopus-install"
[ -f "$MANIFEST" ] && [ -x "$DIR/Octopus" ] || { echo "$DIR does not look like an Octopus install; nothing removed." >&2; exit 1; }

# Clean up hooks/bridges written to external agent configs and generated signal-attach cache before deleting the binary.
if [ "$(id -u)" -eq 0 ] && [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ] && command -v sudo >/dev/null 2>&1; then
  sudo -H -u "$SUDO_USER" "$DIR/Octopus" --cleanup-hooks >/dev/null 2>&1 || true
else
  "$DIR/Octopus" --cleanup-hooks >/dev/null 2>&1 || true
fi

# Remove per-user XDG autostart entry created by the app, if present.
if [ "$(id -u)" -eq 0 ] && [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
  sudo_home="$(eval printf '%s' "~$SUDO_USER" 2>/dev/null || true)"
  [ -z "$sudo_home" ] || rm -f "$sudo_home/.config/autostart/octopus.desktop"
else
  rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/octopus.desktop"
fi

link="$(sed -n 's/^link=//p' "$MANIFEST")"
entry="$(sed -n 's/^entry=//p' "$MANIFEST")"
# Only remove the link if it still points at this install.
if [ -n "$link" ] && [ -L "$link" ] && [ "$(readlink "$link")" = "$DIR/Octopus" ]; then rm -f "$link"; fi
[ -z "$entry" ] || rm -f "$entry"
rm -rf "$DIR"
echo "Octopus removed from $DIR"
