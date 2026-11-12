#!/bin/sh
# Octopus @VERSION@ installer (linux-x64). The program archive is appended after the __ARCHIVE_BELOW__ line.
#
#   ./Octopus-@VERSION@-linux-x64.run                 ask where to install (Enter = default)
#   ./Octopus-@VERSION@-linux-x64.run --prefix DIR    install into DIR without asking
#   options: --yes  accept the default without asking     --no-desktop  skip the launcher entry and the PATH link
#
# Default directory: /opt/octopus when run as root, otherwise ~/.local/share/octopus.
# Uninstall: run uninstall.sh in the install directory.
set -eu

VERSION="@VERSION@"
PREFIX=""
ASSUME_YES=0
DESKTOP=1

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) [ $# -ge 2 ] || { echo "--prefix needs a directory" >&2; exit 2; }; PREFIX="$2"; shift 2 ;;
    --prefix=*) PREFIX="${1#--prefix=}"; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --no-desktop) DESKTOP=0; shift ;;
    -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1 (try --help)" >&2; exit 2 ;;
  esac
done

if [ "$(id -u)" -eq 0 ]; then
  DEFAULT_DIR="/opt/octopus"; BIN_DIR="/usr/local/bin"; APPS_DIR="/usr/share/applications"
else
  DEFAULT_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/octopus"; BIN_DIR="$HOME/.local/bin"
  APPS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
fi

if [ -z "$PREFIX" ]; then
  if [ "$ASSUME_YES" -eq 0 ] && [ -t 0 ]; then
    printf 'Install Octopus %s to [%s]: ' "$VERSION" "$DEFAULT_DIR"
    read -r PREFIX || PREFIX=""
  fi
  [ -n "$PREFIX" ] || PREFIX="$DEFAULT_DIR"
fi
# An unexpanded ~ (typed at the prompt, where the shell did not expand it).
# shellcheck disable=SC2088
case "$PREFIX" in
  "~") PREFIX="$HOME" ;;
  "~/"*) PREFIX="$HOME/${PREFIX#"~/"}" ;;
esac
case "$PREFIX" in /*) ;; *) PREFIX="$(pwd)/$PREFIX" ;; esac

# Refuse to unpack into a directory that already holds something else (an upgrade over an earlier install is fine).
if [ -d "$PREFIX" ] && [ -n "$(ls -A "$PREFIX" 2>/dev/null)" ] && [ ! -f "$PREFIX/.octopus-install" ]; then
  echo "error: $PREFIX is not empty and is not an Octopus install. Pick an empty or new directory." >&2
  exit 1
fi

mkdir -p "$PREFIX"
ARCHIVE_LINE="$(awk '/^__ARCHIVE_BELOW__$/ { print NR + 1; exit }' "$0")"
echo "Installing to $PREFIX ..."
tail -n +"$ARCHIVE_LINE" "$0" | tar -xzf - -C "$PREFIX"
chmod +x "$PREFIX/Octopus" "$PREFIX/uninstall.sh"

LINK=""; ENTRY=""
if [ "$DESKTOP" -eq 1 ]; then
  mkdir -p "$BIN_DIR" "$APPS_DIR"
  LINK="$BIN_DIR/octopus"
  ln -sf "$PREFIX/Octopus" "$LINK"
  ENTRY="$APPS_DIR/octopus.desktop"
  cat > "$ENTRY" <<DESKTOP_ENTRY
[Desktop Entry]
Type=Application
Name=Octopus
Comment=Terminal and project manager
Exec="$PREFIX/Octopus"
Icon=$PREFIX/octopus.png
Terminal=false
Categories=Development;
StartupWMClass=Octopus
DESKTOP_ENTRY
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
fi

# What uninstall.sh removes besides the directory itself.
printf 'link=%s\nentry=%s\nversion=%s\n' "$LINK" "$ENTRY" "$VERSION" > "$PREFIX/.octopus-install"

echo "Octopus $VERSION installed."
echo "  run:       $PREFIX/Octopus"
[ -z "$LINK" ] || echo "  command:   $LINK  (make sure $BIN_DIR is on PATH)"
echo "  uninstall: $PREFIX/uninstall.sh"
exit 0
# shellcheck disable=SC2317
__ARCHIVE_BELOW__
