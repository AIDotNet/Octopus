#!/usr/bin/env python3
"""Builds the built-in pixel icons of the project squares (src/Octopus.App/Assets/PixelIcons/pixel-icons.json).

Sources (both MIT, see Assets/PixelIcons/README.md):
  pixelarticons 2.4.2             npm `pixelarticons`, folder `svg/`
  HackerNoon pixel-icon-library   npm `@hackernoon/pixel-icon-library` 1.1.0, folder `icons/SVG/` (regular, brands, purcats)

Every icon is drawn on a 24 x 24 pixel grid. It is rasterized here into that grid (a pixel is set when its centre lies
inside a filled shape, non-zero rule, shape by shape), so the app stores bits, not paths, and draws the pixels at a whole
multiple of their size: crisp at any scale. Icons that are controls rather than pictures (arrows, alignment, check boxes,
text formatting...) are left out; the "-sharp" duplicates of pixelarticons too.

Usage:
  python3 tools/generate-pixel-icons.py <pixelarticons/svg> <pixel-icon-library/icons/SVG> [contact-sheet.png]
"""
import base64
import json
import re
import struct
import sys
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

GRID = 24
ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "Octopus.App" / "Assets" / "PixelIcons" / "pixel-icons.json"

# ---- what is left out -------------------------------------------------------------------------------------------

# An entry ending in "-" is a prefix; any other entry matches that name and the names that continue it with "-"
# ("arrow" matches "arrow-down"). A "-solid" / "-glyph" variant follows its plain icon.
SKIP = (
    "a-arrow-", "align-", "angle", "arrow", "arrows", "aspect-ratio", "at", "ad", "avatar-circle-", "avatar-square-",
    "badge-", "bars", "bold", "brightness-", "bullet-list", "bulletlist", "calendar-2", "calendar-alt",
    "calendar-range", "calendar-text", "calendar-weeks", "cancel", "card-text", "cc", "cellular-signal-",
    "chart-column-decreasing", "check", "checkbox", "chevron", "chevrons", "circle", "close", "collapse", "comment-text",
    "contact", "copy", "corner-", "crop", "cross", "cursor", "cut", "date-time", "delete", "directions", "divider",
    "dock", "door-open", "download", "drag-and-drop", "edit", "ellipses-", "eraser", "exclaimation", "exclamation",
    "expand", "external-link", "eye-cross", "eye-off", "file-import", "filter", "flatten", "flip-", "float-",
    "folder-plus", "forward", "frame", "gallery-thumbnails", "grid", "h1", "h2", "h3", "hash", "hashtag", "heading",
    "highlight", "icon-category", "icons", "image-2-plus", "image-new", "indent", "info", "invert", "italics", "label",
    "lasso", "layout", "letter-", "line-height", "link", "list-box", "loader", "loading", "lock-open", "login",
    "logout", "magic-edit", "mail-right", "menu", "message-reply", "minus", "more-", "move", "numbered-list",
    "octagon-", "open", "outdent", "page-break", "paragraph", "pause", "pen", "percent", "phone-outgoing",
    "phone-ringing", "pi-circle", "picture-in-picture", "play", "playlist", "plus", "pointer", "print", "pro",
    "proportions", "question", "quote-", "radius", "ratio", "redo", "refresh", "reload", "repeat", "rss-", "scroll-",
    "search", "section", "share", "shuffle", "side-nav-", "sliders", "sort", "sound-", "speed-medium", "speed-slow",
    "spinner", "spline-cursor", "spray-image", "spray-wave", "square", "sticky-note-text", "stop", "strike-through",
    "subscriptions", "switch", "t-arrow-", "tab", "table", "tangent", "text-", "themes", "times", "toke", "trash",
    "underline", "undo", "unlink", "unlock", "upload", "user-check", "user-minus", "user-plus", "user-x", "volume",
    "vote-yeah", "waves-arrow-", "window-", "x", "zoom-",
    "bell-off", "bell-exclaimation", "bell-mute", "mic-off", "zap-off", "siren-off", "parking-off", "lightbulb-off",
    "power-off", "cigarette-off",
)
# Kept although an entry above matches them.
KEEP = {"pen-nib", "circle-power"}
LETTERS = [f"letter-{c}" for c in "abcdefghijklmnopqrstuvwxyz"]

# ---- categories (by name; the first match wins; the rest is "other") ---------------------------------------------

CATEGORIES = [
    ("letters", set(LETTERS)),
    ("brands", {
        "android", "apple", "bluesky", "deno", "discord", "docker", "facebook", "figma", "github", "gitlab", "gmail",
        "google", "google-chrome", "instagram", "linkedin", "linkedin-2", "linux", "mastodon", "netlify", "npm",
        "pixelarticons", "pnpm", "react", "slack", "telegram", "tiktok", "twitter-bird", "vercel", "whatsapp", "youtube",
    }),
    ("fun", {
        "alien", "balloon", "bomb", "cake", "castle", "chess", "crown", "diamond-gem", "drum", "gamepad", "gift",
        "joystick", "party-popper", "potion", "skull", "sword", "t-rex", "trophy", "wand", "sparkle", "sparkles", "star",
        "heart", "smile", "laugh", "angry", "annoyed", "frown", "meh", "sunglasses", "robot", "robot-face",
        "robot-face-happy", "robot-face-sad", "tournament", "target", "goal", "sticker", "hockey-mask", "eye-snake",
        "face-angry", "face-grin", "face-heart-eyes", "face-laugh-squint", "face-sad", "face-thinking", "star-crescent",
        "cassette-tape", "retro-camera", "retro-pc", "spray-can", "spray", "shapes", "magic", "ghost", "gaming",
        "futurism", "hackernoon-purcat", "toke-square", "box-heart", "book-heart", "infinity", "siren", "anchor",
        "keyboard-music", "music", "drum", "balloon", "disc", "album", "clapperboard", "megaphone", "bullhorn",
    }),
    ("nature", {
        "dog", "fish", "snail", "snake", "bug", "leaf", "tree", "tree-pine", "earth", "globe", "globe-americas", "moon",
        "sun", "cloud", "cloud-moon", "cloud-sun", "cloud-fog", "cloud-rain", "snowflake", "wind", "waves", "fire", "zap",
        "bolt", "seedlings", "thermometer", "drop", "mountain", "feather", "paw",
    }),
    ("life", {
        "apple", "coffee", "mug", "tea", "bottle-wine", "home", "sofa", "bed", "hotel-bed", "shirt", "handbag",
        "backpack", "briefcases", "suitcase", "shopping-bag", "shopping-cart", "cart-add", "cart-minus", "store", "shop",
        "tent", "car", "bus", "truck", "ship", "helicopter", "plane", "plane-departure", "headphone", "headphones",
        "camera", "video-camera", "video", "tv", "radio", "book", "book-open", "book-bookmark", "library", "teach",
        "graduation-cap", "glasses", "medical-house", "hand", "handshake", "human", "human-arms-down", "human-arms-up",
        "estate", "building", "building-community", "factory", "hotel", "university", "warehouse", "map", "map-pin",
        "map-pin-home", "map-solid", "road-sign", "compass", "gps", "gps-2", "smart-home", "alarm-clock", "clock",
        "hourglass", "watch", "recycle", "broom", "people-carry", "seedlings", "utility-pole", "wall", "window-frame",
        "door-closed", "parking", "cigarette-off", "user", "users", "users-crown", "user-headset", "avatar-circle",
        "avatar-square", "life-hacking", "society", "remote", "bell", "bell-ring", "phone", "phone-call", "smartphone",
        "mic", "image", "images", "gallery", "eye", "location-pin", "medal", "flag", "flag-checkered", "goal",
    }),
    ("work", {
        "calendar", "mail", "mail-open", "envelope", "inbox", "mailbox", "message", "message-text", "message-dots",
        "comment", "comment-dots", "comment-quote", "comments", "chart", "chart-line", "chart-bar-big", "analytics",
        "trending", "trending-up", "file", "file-text", "files", "folder", "folder-open", "clipboard", "clipboard-note",
        "notebook", "note", "notes", "sticky-note", "invoice", "receipt", "wallet", "money", "dollar", "euro", "pound",
        "rubel", "coins", "coin", "banknote", "credit-card", "card", "membercard", "briefcase", "presentation", "printer",
        "projector", "newspaper", "archive", "bookmark", "tag", "thumbtack", "bank", "box-usd", "scale", "languages",
        "translate", "paint-brush", "pen-nib", "pencil", "pencil-ruler", "brush", "colors-swatch", "pipette",
        "business", "finance", "management", "product-management", "startups", "writing", "media", "web3",
        "thumbs-up", "thumbs-down", "thumbsup", "thumbsdown", "badge-check", "lightbulb", "calender", "article",
        "pin", "rss", "podcasts", "playlist", "news", "store", "shopping-bag",
    }),
    ("dev", {
        "code", "code-block", "terminal", "debug", "cpu", "gpu", "server", "database", "cloud-server", "git-branch",
        "git-commit", "git-merge", "git-pull-request", "branch", "fork", "merge", "pull-request", "braces",
        "braces-content", "brackets", "brackets-content", "brackets-angle", "bracket-curly", "bracket-round",
        "bracket-square", "binary", "algorithm", "circuit-board", "computer", "laptop", "laptop-code", "keyboard",
        "monitor", "mouse", "usb", "plug", "modem", "memory-stick", "pc", "pc-case", "ai-app-mac", "ai-file", "ai-scan",
        "ai-settings-2", "ai-settings-3", "ai-user-circle", "ai-view", "ai-voice", "blocks", "package", "box", "tools",
        "tool-case", "gear", "settings-2", "settings-cog", "settings-cog-2", "cog", "tablet", "webcam", "wifi",
        "qr-code", "scan-barcode", "barcode", "lock", "lock-alt", "key", "shield", "command", "sitemap", "chart-network",
        "script", "test-tube", "power", "circle-power", "app-mac", "app-windows", "bolt", "cloud-download-alt",
        "cloud-upload", "programming", "technology", "tech-companies", "tech-stories", "data-science",
        "machine-learning", "cybersecurity", "science", "cloud", "rss", "npm", "signal", "speed-fast", "send",
        "airplay", "audio-waveform", "vibrate", "battery", "battery-full", "satellite", "text-cursor",
    }),
]


def category_of(name: str) -> str:
    base = re.sub(r"-(solid|glyph)$", "", name)
    for key, names in CATEGORIES:
        if base in names or name in names:
            return key
    return "other"


def skipped(name: str) -> bool:
    base = re.sub(r"-(solid|glyph)$", "", name)
    if base in KEEP or base in LETTERS:
        return False
    return any(base.startswith(p) if p.endswith("-") else base == p or base.startswith(p + "-") for p in SKIP)


# ---- geometry -----------------------------------------------------------------------------------------------------

NUM = re.compile(r"-?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?")
TOKEN = re.compile(r"[A-Za-z]|-?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?")


class Unsupported(Exception):
    pass


def path_polygons(d: str):
    """The subpaths of a path made of straight lines, as lists of absolute points."""
    tokens = TOKEN.findall(d)
    polys, current, x, y, sx, sy, cmd, i = [], [], 0.0, 0.0, 0.0, 0.0, None, 0

    def num():
        nonlocal i
        value = float(tokens[i])
        i += 1
        return value

    while i < len(tokens):
        if re.fullmatch(r"[A-Za-z]", tokens[i]):
            cmd = tokens[i]
            i += 1
            if cmd in "Zz":
                if current:
                    polys.append(current)
                current = []
                x, y = sx, sy
                continue
        if cmd is None:
            raise Unsupported("no command")
        if cmd in "Mm":
            nx, ny = num(), num()
            if cmd == "m" and (current or polys):
                nx, ny = x + nx, y + ny
            if current:
                polys.append(current)
            x, y, sx, sy = nx, ny, nx, ny
            current = [(x, y)]
            cmd = "L" if cmd == "M" else "l"
        elif cmd in "Ll":
            nx, ny = num(), num()
            x, y = (x + nx, y + ny) if cmd == "l" else (nx, ny)
            current.append((x, y))
        elif cmd in "Hh":
            nx = num()
            x = x + nx if cmd == "h" else nx
            current.append((x, y))
        elif cmd in "Vv":
            ny = num()
            y = y + ny if cmd == "v" else ny
            current.append((x, y))
        else:
            raise Unsupported(f"command {cmd}")
    if current:
        polys.append(current)
    return [p for p in polys if len(p) >= 3]


def winding(poly, px, py):
    w = 0
    n = len(poly)
    for k in range(n):
        x1, y1 = poly[k]
        x2, y2 = poly[(k + 1) % n]
        if y1 <= py:
            if y2 > py and (x2 - x1) * (py - y1) - (px - x1) * (y2 - y1) > 0:
                w += 1
        elif y2 <= py and (x2 - x1) * (py - y1) - (px - x1) * (y2 - y1) < 0:
            w -= 1
    return w


def shapes_of(svg_text: str):
    """Filled shapes of the file: each a list of polygons (one element, non-zero rule). Fill is inherited."""
    root = ET.fromstring(svg_text)
    view = [float(v) for v in NUM.findall(root.get("viewBox", f"0 0 {GRID} {GRID}"))]
    if view != [0, 0, GRID, GRID]:
        raise Unsupported(f"viewBox {view}")
    shapes = []

    def walk(el, fill):
        tag = el.tag.split("}")[-1]
        fill = el.get("fill", fill)
        if el.get("transform"):
            raise Unsupported("transform")
        if float(el.get("opacity", "1")) < 1:
            return
        filled = fill not in ("none", "transparent")
        if tag == "path" and filled:
            shapes.append(path_polygons(el.get("d", "")))
        elif tag == "rect" and filled:
            x, y = float(el.get("x", 0)), float(el.get("y", 0))
            w, h = float(el.get("width")), float(el.get("height"))
            shapes.append([[(x, y), (x + w, y), (x + w, y + h), (x, y + h)]])
        elif tag == "polygon" and filled:
            v = [float(n) for n in NUM.findall(el.get("points", ""))]
            shapes.append([list(zip(v[0::2], v[1::2]))])
        elif tag in ("circle", "ellipse", "line", "polyline", "image", "text", "use"):
            raise Unsupported(tag)
        for child in el:
            walk(child, fill)

    walk(root, "black")
    return shapes


def snap(shapes):
    """Snaps to the half-pixel grid; artwork drawn on the half-pixel grid moves half a pixel onto whole pixels."""
    shapes = [[[(round(x * 2) / 2, round(y * 2) / 2) for x, y in poly] for poly in shape] for shape in shapes]
    values = [v for shape in shapes for poly in shape for p in poly for v in p]
    halves = sum(1 for v in values if v % 1)
    if values and halves > len(values) / 2:
        dx = 0.5 if max(values) <= GRID - 0.5 else -0.5
        shapes = [[[(x + dx, y + dx) for x, y in poly] for poly in shape] for shape in shapes]
    return shapes


def rasterize(shapes):
    rows = []
    for gy in range(GRID):
        bits = 0
        for gx in range(GRID):
            cx, cy = gx + 0.5, gy + 0.5
            if any(sum(winding(p, cx, cy) for p in shape) != 0 for shape in shapes):
                bits |= 1 << (GRID - 1 - gx)
        rows.append(bits)
    return rows


def encode(rows):
    return base64.b64encode(b"".join(r.to_bytes(3, "big") for r in rows)).decode("ascii")


# ---- sources ------------------------------------------------------------------------------------------------------

def collect(pixelarticons: Path, hackernoon: Path):
    icons, problems = [], []

    def add(icon_id, name, source, svg_path):
        try:
            rows = rasterize(snap(shapes_of(svg_path.read_text(encoding="utf-8"))))
        except (Unsupported, ET.ParseError, ValueError, TypeError) as e:
            problems.append(f"{svg_path.name}: {e}")
            return
        if not any(rows):
            problems.append(f"{svg_path.name}: empty")
            return
        icons.append({"id": icon_id, "name": name, "source": source, "category": category_of(name), "bits": encode(rows)})

    for svg in sorted(pixelarticons.glob("*.svg")):
        name = svg.stem
        if name.endswith("-sharp") or skipped(name):
            continue
        add(name, name, "pixelarticons", svg)
    seen = {i["id"] for i in icons}
    for folder in ("brands", "regular", "purcats"):
        for svg in sorted((hackernoon / folder).glob("*.svg")):
            name = svg.stem
            if folder != "brands" and skipped(name):
                continue
            icon_id = "hn-" + name
            if icon_id in seen:
                continue
            seen.add(icon_id)
            category = "brands" if folder == "brands" else None
            before = len(icons)
            add(icon_id, name, "hackernoon", svg)
            if category and len(icons) > before:
                icons[-1]["category"] = category
    return icons, problems


# ---- contact sheet (to look at the result) ------------------------------------------------------------------------

def write_png(path: Path, width, height, pixels):
    raw = b"".join(b"\x00" + bytes(pixels[y * width:(y + 1) * width]) for y in range(height))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def contact_sheet(icons, path: Path, columns=32, scale=1, gap=4):
    cell = GRID * scale + gap
    rows_n = (len(icons) + columns - 1) // columns
    width, height = columns * cell + gap, rows_n * cell + gap
    pixels = bytearray([255]) * (width * height)
    for n, icon in enumerate(icons):
        data = base64.b64decode(icon["bits"])
        ox, oy = gap + (n % columns) * cell, gap + (n // columns) * cell
        for gy in range(GRID):
            row = int.from_bytes(data[gy * 3:gy * 3 + 3], "big")
            for gx in range(GRID):
                if row >> (GRID - 1 - gx) & 1:
                    for sy in range(scale):
                        start = (oy + gy * scale + sy) * width + ox + gx * scale
                        pixels[start:start + scale] = bytes(scale)
    write_png(path, width, height, pixels)


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    icons, problems = collect(Path(sys.argv[1]), Path(sys.argv[2]))
    order = ["fun", "nature", "life", "work", "dev", "brands", "letters", "other"]
    icons.sort(key=lambda i: (order.index(i["category"]), i["source"] != "pixelarticons", i["name"]))
    OUT.parent.mkdir(parents=True, exist_ok=True)
    document = {"size": GRID, "icons": icons}
    OUT.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    counts = {c: sum(1 for i in icons if i["category"] == c) for c in order}
    print(f"{len(icons)} icons -> {OUT.relative_to(ROOT)} ({OUT.stat().st_size // 1024} KB)", counts)
    for p in problems:
        print("  skipped", p)
    if len(sys.argv) > 3:
        contact_sheet(icons, Path(sys.argv[3]))


if __name__ == "__main__":
    main()
