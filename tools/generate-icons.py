#!/usr/bin/env python3
"""Builds every app icon from the master /icon.png.

Outputs (src/Octopus.App/Assets/Icon/):
  octopus.ico   Windows: the exe icon (explorer, taskbar, task manager), PNG frames 16..256
  octopus.icns  macOS: for an .app bundle (an unbundled run gets its Dock icon from dock.png)
  octopus.png   512 px window icon (Avalonia Window.Icon: Windows title bar/taskbar, Linux)
  dock.png      512 px macOS Dock icon, with the transparent margin the Dock expects (set at run time by MacAppIcon)
  logo.png      128 px in-app logo (sidebar head)

Usage: python3 tools/generate-icons.py   (needs Pillow)
"""
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "icon.png"
OUT = ROOT / "src" / "Octopus.App" / "Assets" / "Icon"

# The master is a glow on black: a rounded square keeps that black as the icon's own tile, and its soft edge
# fades into the corners, so it reads the same on light and dark taskbars.
CORNER = 0.2237  # the macOS app-icon corner radius, as a share of the tile


def rounded(master: Image.Image, size: int, inset: float = 0.0) -> Image.Image:
    """The master as a size x size RGBA tile; `inset` is the transparent margin (a share of size) around it."""
    tile = round(size * (1 - 2 * inset))
    art = master.resize((tile, tile), Image.LANCZOS).convert("RGBA")
    # Antialiased mask: drawn 8x, then reduced.
    scale = 8
    mask = Image.new("L", (tile * scale, tile * scale), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, tile * scale - 1, tile * scale - 1), radius=tile * scale * CORNER, fill=255)
    art.putalpha(mask.resize((tile, tile), Image.LANCZOS))
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    offset = (size - tile) // 2
    canvas.paste(art, (offset, offset), art)
    return canvas


def png_bytes(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, "PNG", optimize=True)
    return buffer.getvalue()


def write_ico(path: Path, frames: dict[int, bytes]) -> None:
    sizes = sorted(frames)
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries, blobs = b"", b""
    for size in sizes:
        data = frames[size]
        # Width/height 0 means 256. A PNG frame is stored as-is (Vista and later).
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    path.write_bytes(header + entries + blobs)


def write_icns(path: Path, frames: dict[str, bytes]) -> None:
    body = b"".join(kind.encode("ascii") + struct.pack(">I", 8 + len(data)) + data for kind, data in frames.items())
    path.write_bytes(b"icns" + struct.pack(">I", 8 + len(body)) + body)


def main() -> None:
    master = Image.open(SOURCE).convert("RGB")
    OUT.mkdir(parents=True, exist_ok=True)

    ico = {size: png_bytes(rounded(master, size)) for size in (16, 24, 32, 48, 64, 128, 256)}
    write_ico(OUT / "octopus.ico", ico)

    # macOS icons keep a transparent margin (824 of 1024) so the dock does not show them larger than their neighbours.
    icns_types = {
        "icp4": 16, "icp5": 32, "icp6": 64, "ic07": 128, "ic08": 256, "ic09": 512, "ic10": 1024,
        "ic11": 32, "ic12": 64, "ic13": 256, "ic14": 512,
    }
    write_icns(OUT / "octopus.icns", {kind: png_bytes(rounded(master, size, inset=100 / 1024)) for kind, size in icns_types.items()})

    (OUT / "octopus.png").write_bytes(png_bytes(rounded(master, 512)))
    (OUT / "dock.png").write_bytes(png_bytes(rounded(master, 512, inset=100 / 1024)))
    (OUT / "logo.png").write_bytes(png_bytes(rounded(master, 128)))

    for file in sorted(OUT.iterdir()):
        print(f"{file.relative_to(ROOT)}  {file.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
