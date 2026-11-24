#!/usr/bin/env python3
"""Builds the built-in VS Code style colored file icons (src/Octopus.App/Assets/FileIcons/file-icons.json).

Source (MIT, see Assets/FileIcons/README.md):
  material-icon-theme (npm `material-icon-theme`, `dist/material-icons.json` and `icons/*.svg`)

Normalizes each selected SVG into a compact viewBox + path-list JSON so Octopus can render crisp
multi-color icons directly through Avalonia's DrawingContext with zero reflection (NativeAOT-safe)
and zero visual child overhead in virtualized tree rows.

Usage:
  python3 tools/generate-file-icons.py <path/to/material-icon-theme/package>
"""
from __future__ import annotations

import json
import math
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "src" / "Octopus.App" / "Assets" / "FileIcons"
OUT_JSON = OUT_DIR / "file-icons.json"

# Curated file icons (iconDefinition ids from material-icon-theme)
FILE_ICONS = [
    # Defaults & generic
    "file", "document", "log", "pdf", "table", "image", "svg", "audio", "video", "font", "zip",
    "lock", "key", "certificate", "database", "settings", "tune", "console", "powershell",
    "diff", "readme", "changelog", "credits", "contributing", "todo", "i18n",
    # Languages
    "csharp", "fsharp", "visualstudio", "xaml", "nuget",
    "typescript", "typescript-def", "javascript", "javascript-map", "react", "react_ts",
    "vue", "svelte", "astro", "angular", "html", "css", "sass", "less",
    "python", "python-misc", "go", "go-mod", "rust", "java", "kotlin", "scala", "swift",
    "dart", "ruby", "gemfile", "php", "c", "h", "cpp", "hpp", "lua", "zig", "elixir",
    "erlang", "haskell", "clojure", "r", "perl", "julia", "nim", "assembly", "webassembly",
    "groovy", "gradle", "cmake", "makefile", "nix", "terraform", "proto", "graphql", "prisma",
    "markdown", "mdx", "asciidoc", "tex", "mermaid", "http",
    # Data & configs
    "json", "yaml", "toml", "xml",
    "git", "github-actions-workflow", "gitlab", "docker", "kubernetes", "helm",
    "nodejs", "npm", "pnpm", "yarn", "bun", "deno",
    "vite", "webpack", "rollup", "babel", "eslint", "prettier", "biome",
    "tailwindcss", "postcss", "tsconfig", "next", "nuxt", "storybook", "jest", "vitest", "playwright",
    "editorconfig", "vercel", "netlify", "firebase", "supabase", "sentry", "dependabot",
    "test-ts", "test-jsx", "test-js",
    "android", "exe", "dll", "lib",
]

# Curated semantic folders (base icon id; both `<id>` and `<id>-open` are exported)
FOLDER_ICONS = [
    "folder",
    "folder-src",
    "folder-test",
    "folder-docs",
    "folder-images",
    "folder-components",
    "folder-views",
    "folder-css",
    "folder-scripts",
    "folder-config",
    "folder-utils",
    "folder-lib",
    "folder-api",
    "folder-class",
    "folder-interface",
    "folder-hook",
    "folder-store",
    "folder-public",
    "folder-dist",
    "folder-node",
    "folder-github",
    "folder-git",
    "folder-vscode",
    "folder-android",
    "folder-ios",
    "folder-macos",
    "folder-packages",
    "folder-plugin",
    "folder-i18n",
    "folder-ui",
    "folder-database",
    "folder-docker",
    "folder-temp",
    "folder-log",
    "folder-client",
    "folder-server",
    "folder-core",
    "folder-shared",
    "folder-layout",
    "folder-routes",
    "folder-middleware",
    "folder-controller",
    "folder-resource",
    "folder-tools",
    "folder-app",
    "folder-update",
    "folder-theme",
    "folder-debug",
    "folder-include",
    "folder-ci",
]

# Extra custom mappings to improve .NET / Avalonia / modern dev coverage
EXTRA_FILE_EXTENSIONS = {
    "axaml": "xaml",
    "slnx": "visualstudio",
    "csx": "csharp",
    "props": "settings",
    "targets": "xml",
    "xlf": "i18n",
    "icns": "image",
    "avif": "image",
    "webp": "image",
    "m4a": "audio",
    "mkv": "video",
    "mov": "video",
    "nupkg": "nuget",
    "tgz": "zip",
    "fish": "console",
    "zsh": "console",
    "bash": "console",
    "sh": "console",
    "ps1": "powershell",
    "psm1": "powershell",
    "bat": "console",
    "cmd": "console",
    "json5": "json",
    "jsonc": "json",
    "jsonl": "json",
    "ndjson": "json",
    "csv": "table",
    "tsv": "table",
    "sqlite": "database",
    "sqlite3": "database",
    "db": "database",
    "sql": "database",
    "md": "markdown",
    "markdown": "markdown",
    "mdx": "mdx",
    "txt": "document",
    "rst": "document",
    "log": "log",
    "patch": "diff",
    "diff": "diff",
    "lock": "lock",
}

EXTRA_FILE_NAMES = {
    "directory.build.props": "settings",
    "directory.packages.props": "nuget",
    "global.json": "json",
    "nuget.config": "nuget",
    "dockerfile": "docker",
    "docker-compose.yml": "docker",
    "docker-compose.yaml": "docker",
    "compose.yml": "docker",
    "compose.yaml": "docker",
    ".gitignore": "git",
    ".gitattributes": "git",
    ".gitmodules": "git",
    ".editorconfig": "editorconfig",
    "readme": "readme",
    "readme.md": "readme",
    "changelog": "changelog",
    "changelog.md": "changelog",
    "license": "certificate",
    "license.md": "certificate",
    "license.txt": "certificate",
    "package.json": "nodejs",
    "package-lock.json": "nodejs",
    "pnpm-lock.yaml": "pnpm",
    "pnpm-workspace.yaml": "pnpm",
    "yarn.lock": "yarn",
    "bun.lock": "bun",
    "bun.lockb": "bun",
    "tsconfig.json": "tsconfig",
    "cargo.toml": "rust",
    "cargo.lock": "lock",
    "go.mod": "go-mod",
    "go.sum": "go-mod",
    "makefile": "makefile",
    "cmakelists.txt": "cmake",
    ".env": "tune",
    ".env.local": "tune",
    ".env.development": "tune",
    ".env.production": "tune",
    ".env.example": "tune",
}

EXTRA_FOLDER_NAMES = {
    "src": "folder-src",
    "source": "folder-src",
    "tests": "folder-test",
    "test": "folder-test",
    "__tests__": "folder-test",
    "spec": "folder-test",
    "docs": "folder-docs",
    "doc": "folder-docs",
    "design": "folder-ui",
    "ui": "folder-ui",
    "assets": "folder-resource",
    "resources": "folder-resource",
    "images": "folder-images",
    "img": "folder-images",
    "icons": "folder-images",
    "components": "folder-components",
    "views": "folder-views",
    "pages": "folder-views",
    "styles": "folder-css",
    "css": "folder-css",
    "scripts": "folder-scripts",
    "tools": "folder-tools",
    "config": "folder-config",
    "configs": "folder-config",
    "settings": "folder-config",
    "utils": "folder-utils",
    "helpers": "folder-utils",
    "common": "folder-shared",
    "shared": "folder-shared",
    "lib": "folder-lib",
    "libs": "folder-lib",
    "api": "folder-api",
    "services": "folder-api",
    "models": "folder-class",
    "types": "folder-interface",
    "hooks": "folder-hook",
    "store": "folder-store",
    "state": "folder-store",
    "storage": "folder-database",
    "public": "folder-public",
    "static": "folder-public",
    "dist": "folder-dist",
    "build": "folder-dist",
    "out": "folder-dist",
    "bin": "folder-dist",
    "obj": "folder-temp",
    "artifacts": "folder-dist",
    "packaging": "folder-packages",
    "packages": "folder-packages",
    "plugins": "folder-plugin",
    "builtinplugins": "folder-plugin",
    "node_modules": "folder-node",
    ".github": "folder-github",
    ".git": "folder-git",
    ".vscode": "folder-vscode",
    "android": "folder-android",
    "ios": "folder-ios",
    "locales": "folder-i18n",
    "locale": "folder-i18n",
    "i18n": "folder-i18n",
    "localization": "folder-i18n",
    "db": "folder-database",
    "database": "folder-database",
    "migrations": "folder-database",
    "docker": "folder-docker",
    "tmp": "folder-temp",
    "temp": "folder-temp",
    "cache": "folder-temp",
    "logs": "folder-log",
    "log": "folder-log",
    "client": "folder-client",
    "server": "folder-server",
    "host": "folder-server",
    "core": "folder-core",
    "layout": "folder-layout",
    "routes": "folder-routes",
    "middleware": "folder-middleware",
    "controllers": "folder-controller",
    "app": "folder-app",
    "updates": "folder-update",
    "theme": "folder-theme",
    "themes": "folder-theme",
    "appearance": "folder-theme",
    "workflows": "folder-ci",
    ".claude": "folder-config",
    ".agents": "folder-config",
}


def tag_name(el: ET.Element) -> str:
    return el.tag.split("}")[-1]


def parse_style(style_str: str | None) -> dict[str, str]:
    result: dict[str, str] = {}
    if not style_str:
        return result
    for part in style_str.split(";"):
        if ":" in part:
            k, v = part.split(":", 1)
            result[k.strip()] = v.strip()
    return result


def parse_float(val: str | None, default: float = 0.0) -> float:
    if not val:
        return default
    m = re.match(r"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)", val)
    return float(m.group(1)) if m else default


def fmt(v: float) -> str:
    s = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if s in ("", "-0") else s


def parse_color(raw: str | None, gradients: dict[str, str]) -> str | None:
    if raw is None:
        return None
    raw = raw.strip()
    if not raw or raw.lower() == "none" or raw.lower() == "transparent":
        return None
    if raw.lower() == "currentcolor":
        return "currentColor"
    m = re.match(r"url\(#([^)]+)\)", raw)
    if m:
        return gradients.get(m.group(1))
    # Named colors occasionally present in SVG
    named = {
        "white": "#ffffff",
        "black": "#000000",
        "red": "#ff0000",
        "blue": "#0000ff",
        "green": "#008000",
        "yellow": "#ffff00",
        "orange": "#ffa500",
        "gray": "#808080",
        "grey": "#808080",
    }
    if raw.lower() in named:
        return named[raw.lower()]
    if raw.startswith("#"):
        h = raw.lower()
        if len(h) == 4:
            h = "#" + "".join(c * 2 for c in h[1:])
        return h
    rgb = re.match(r"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", raw)
    if rgb:
        return f"#{int(rgb.group(1)):02x}{int(rgb.group(2)):02x}{int(rgb.group(3)):02x}"
    return None


def rect_to_path(el: ET.Element) -> str | None:
    x = parse_float(el.get("x"))
    y = parse_float(el.get("y"))
    w = parse_float(el.get("width"))
    h = parse_float(el.get("height"))
    if w <= 0 or h <= 0:
        return None
    rx = parse_float(el.get("rx"))
    ry = parse_float(el.get("ry"), rx)
    if rx == 0 and ry > 0:
        rx = ry
    rx = min(rx, w / 2)
    ry = min(ry, h / 2)
    if rx <= 0 and ry <= 0:
        return f"M{fmt(x)},{fmt(y)}h{fmt(w)}v{fmt(h)}h{fmt(-w)}Z"
    return (
        f"M{fmt(x + rx)},{fmt(y)}"
        f"H{fmt(x + w - rx)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + w)},{fmt(y + ry)}"
        f"V{fmt(y + h - ry)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + w - rx)},{fmt(y + h)}"
        f"H{fmt(x + rx)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x)},{fmt(y + h - ry)}"
        f"V{fmt(y + ry)}A{fmt(rx)},{fmt(ry)} 0 0 1 {fmt(x + rx)},{fmt(y)}Z"
    )


def circle_to_path(el: ET.Element) -> str | None:
    cx = parse_float(el.get("cx"))
    cy = parse_float(el.get("cy"))
    r = parse_float(el.get("r"))
    if r <= 0:
        return None
    return (
        f"M{fmt(cx - r)},{fmt(cy)}"
        f"A{fmt(r)},{fmt(r)} 0 1 0 {fmt(cx + r)},{fmt(cy)}"
        f"A{fmt(r)},{fmt(r)} 0 1 0 {fmt(cx - r)},{fmt(cy)}Z"
    )


def ellipse_to_path(el: ET.Element) -> str | None:
    cx = parse_float(el.get("cx"))
    cy = parse_float(el.get("cy"))
    rx = parse_float(el.get("rx"))
    ry = parse_float(el.get("ry"))
    if rx <= 0 or ry <= 0:
        return None
    return (
        f"M{fmt(cx - rx)},{fmt(cy)}"
        f"A{fmt(rx)},{fmt(ry)} 0 1 0 {fmt(cx + rx)},{fmt(cy)}"
        f"A{fmt(rx)},{fmt(ry)} 0 1 0 {fmt(cx - rx)},{fmt(cy)}Z"
    )


def poly_to_path(el: ET.Element, close: bool) -> str | None:
    pts = re.findall(r"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", el.get("points") or "")
    if len(pts) < 4:
        return None
    pairs = [f"{pts[i]},{pts[i + 1]}" for i in range(0, len(pts) - 1, 2)]
    return f"M{pairs[0]}L" + " ".join(pairs[1:]) + ("Z" if close else "")


def collect_gradients(svg_root: ET.Element) -> dict[str, str]:
    grads: dict[str, str] = {}
    for el in svg_root.iter():
        t = tag_name(el)
        if t in ("linearGradient", "radialGradient") and el.get("id"):
            stops = [c for c in el if tag_name(c) == "stop"]
            if stops:
                # Pick middle stop for balanced representative solid color
                mid = stops[len(stops) // 2]
                style = parse_style(mid.get("style"))
                col = mid.get("stop-color") or style.get("stop-color")
                parsed = parse_color(col, {})
                if parsed:
                    grad_id = el.get("id")
                    if grad_id:
                        grads[grad_id] = parsed
    return grads


def parse_transform_matrix(t: str | None) -> list[float] | None:
    """Returns 6-element affine matrix [a, b, c, d, e, f] if non-identity."""
    if not t:
        return None
    # Start with identity: [a, b, c, d, e, f] where x' = a*x + c*y + e, y' = b*x + d*y + f
    m = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0]

    def mul(m1: list[float], m2: list[float]) -> list[float]:
        a1, b1, c1, d1, e1, f1 = m1
        a2, b2, c2, d2, e2, f2 = m2
        return [
            a1 * a2 + c1 * b2,
            b1 * a2 + d1 * b2,
            a1 * c2 + c1 * d2,
            b1 * c2 + d1 * d2,
            a1 * e2 + c1 * f2 + e1,
            b1 * e2 + d1 * f2 + f1,
        ]

    for kind, args_str in re.findall(r"(matrix|translate|scale|rotate)\s*\(([^)]*)\)", t):
        nums = [float(x) for x in re.findall(r"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", args_str)]
        if kind == "matrix" and len(nums) == 6:
            m = mul(m, nums)
        elif kind == "translate" and len(nums) >= 1:
            tx = nums[0]
            ty = nums[1] if len(nums) > 1 else 0.0
            m = mul(m, [1.0, 0.0, 0.0, 1.0, tx, ty])
        elif kind == "scale" and len(nums) >= 1:
            sx = nums[0]
            sy = nums[1] if len(nums) > 1 else sx
            m = mul(m, [sx, 0.0, 0.0, sy, 0.0, 0.0])
        elif kind == "rotate" and len(nums) >= 1:
            rad = math.radians(nums[0])
            co, si = math.cos(rad), math.sin(rad)
            if len(nums) == 3:
                cx, cy = nums[1], nums[2]
                m = mul(m, [1.0, 0.0, 0.0, 1.0, cx, cy])
                m = mul(m, [co, si, -si, co, 0.0, 0.0])
                m = mul(m, [1.0, 0.0, 0.0, 1.0, -cx, -cy])
            else:
                m = mul(m, [co, si, -si, co, 0.0, 0.0])

    if all(abs(a - b) < 1e-6 for a, b in zip(m, [1.0, 0.0, 0.0, 1.0, 0.0, 0.0])):
        return None
    return [round(v, 5) for v in m]


def mul_mat(m1: list[float] | None, m2: list[float] | None) -> list[float] | None:
    if m1 is None:
        return m2
    if m2 is None:
        return m1
    a1, b1, c1, d1, e1, f1 = m1
    a2, b2, c2, d2, e2, f2 = m2
    return [
        round(a1 * a2 + c1 * b2, 5),
        round(b1 * a2 + d1 * b2, 5),
        round(a1 * c2 + c1 * d2, 5),
        round(b1 * c2 + d1 * d2, 5),
        round(a1 * e2 + c1 * f2 + e1, 5),
        round(b1 * e2 + d1 * f2 + f1, 5),
    ]


def parse_svg(svg_path: Path) -> dict | None:
    tree = ET.parse(svg_path)
    root = tree.getroot()
    vb_raw = root.get("viewBox")
    if vb_raw:
        vb = [float(x) for x in re.findall(r"[+-]?(?:\d+\.?\d*|\.\d+)", vb_raw)]
        if len(vb) != 4 or vb[2] <= 0 or vb[3] <= 0:
            return None
    else:
        w = parse_float(root.get("width"), 24.0)
        h = parse_float(root.get("height"), 24.0)
        vb = [0.0, 0.0, w, h]

    gradients = collect_gradients(root)
    shapes: list[dict] = []

    def walk(
        el: ET.Element,
        inh_fill: str | None,
        inh_stroke: str | None,
        inh_sw: float,
        inh_opacity: float,
        inh_eo: bool,
        inh_mat: list[float] | None,
    ) -> None:
        t = tag_name(el)
        if t in ("defs", "clipPath", "linearGradient", "radialGradient", "title", "desc", "metadata"):
            return

        style = parse_style(el.get("style"))
        raw_fill = el.get("fill") if "fill" in el.attrib else style.get("fill")
        raw_stroke = el.get("stroke") if "stroke" in el.attrib else style.get("stroke")
        raw_sw = el.get("stroke-width") if "stroke-width" in el.attrib else style.get("stroke-width")
        raw_op = el.get("opacity") if "opacity" in el.attrib else style.get("opacity")
        raw_fop = el.get("fill-opacity") if "fill-opacity" in el.attrib else style.get("fill-opacity")
        raw_fr = el.get("fill-rule") if "fill-rule" in el.attrib else style.get("fill-rule")

        fill = inh_fill if raw_fill is None else parse_color(raw_fill, gradients)
        stroke = inh_stroke if raw_stroke is None else parse_color(raw_stroke, gradients)
        sw = inh_sw if raw_sw is None else parse_float(raw_sw, inh_sw)
        opacity = inh_opacity * (parse_float(raw_op, 1.0) if raw_op is not None else 1.0)
        fop = parse_float(raw_fop, 1.0) if raw_fop is not None else 1.0
        eo = inh_eo if raw_fr is None else (raw_fr.lower() == "evenodd")
        mat = mul_mat(inh_mat, parse_transform_matrix(el.get("transform")))

        d: str | None = None
        if t == "path":
            d = (el.get("d") or "").strip()
        elif t == "rect":
            d = rect_to_path(el)
        elif t == "circle":
            d = circle_to_path(el)
        elif t == "ellipse":
            d = ellipse_to_path(el)
        elif t == "polygon":
            d = poly_to_path(el, close=True)
        elif t == "polyline":
            d = poly_to_path(el, close=False)

        if d:
            eff_op = round(opacity * fop, 3)
            if eff_op > 0.001 and (fill is not None or stroke is not None):
                entry: dict = {"d": re.sub(r"\s+", " ", d)}
                if fill is not None:
                    entry["f"] = fill
                if stroke is not None:
                    entry["s"] = stroke
                    if abs(sw - 1.0) > 1e-4:
                        entry["sw"] = round(sw, 3)
                if eo:
                    entry["eo"] = True
                if eff_op < 0.999:
                    entry["o"] = eff_op
                if mat is not None:
                    entry["m"] = mat
                shapes.append(entry)

        for child in el:
            walk(child, fill, stroke, sw, opacity, eo, mat)

    walk(root, inh_fill=None, inh_stroke=None, inh_sw=1.0, inh_opacity=1.0, inh_eo=False, inh_mat=None)
    if not shapes:
        return None
    return {
        "vb": [int(v) if v.is_integer() else round(v, 3) for v in vb],
        "p": shapes,
    }


def main() -> int:
    if len(sys.argv) < 2:
        print("Usage: python3 tools/generate-file-icons.py <material-icon-theme/package>", file=sys.stderr)
        return 2

    pkg = Path(sys.argv[1])
    manifest = json.loads((pkg / "dist" / "material-icons.json").read_text(encoding="utf-8"))
    icon_defs: dict[str, dict] = manifest["iconDefinitions"]
    icons_dir = pkg / "icons"

    wanted_ids: set[str] = set()
    for icon_id in FILE_ICONS:
        if icon_id in icon_defs:
            wanted_ids.add(icon_id)
            light_id = f"{icon_id}_light"
            if light_id in icon_defs:
                wanted_ids.add(light_id)
        else:
            print(f"warning: missing file icon '{icon_id}'", file=sys.stderr)

    for folder_id in FOLDER_ICONS:
        for variant in (folder_id, f"{folder_id}-open"):
            if variant in icon_defs:
                wanted_ids.add(variant)
            else:
                print(f"warning: missing folder icon '{variant}'", file=sys.stderr)

    parsed_icons: dict[str, dict] = {}
    for icon_id in sorted(wanted_ids):
        rel = icon_defs[icon_id]["iconPath"]
        svg_path = (pkg / "dist" / rel).resolve()
        if not svg_path.exists():
            svg_path = icons_dir / Path(rel).name
        parsed = parse_svg(svg_path)
        if parsed is None:
            print(f"warning: failed to parse {icon_id} ({svg_path})", file=sys.stderr)
            continue
        parsed_icons[icon_id] = parsed

    # Filter manifest mappings to only icons we included, plus our curated extras
    file_extensions: dict[str, str] = {}
    for ext, target in manifest.get("fileExtensions", {}).items():
        if target in parsed_icons:
            file_extensions[ext.lower()] = target
    for ext, target in EXTRA_FILE_EXTENSIONS.items():
        if target in parsed_icons:
            file_extensions[ext.lower()] = target

    file_names: dict[str, str] = {}
    for name, target in manifest.get("fileNames", {}).items():
        if target in parsed_icons:
            file_names[name.lower()] = target
    for name, target in EXTRA_FILE_NAMES.items():
        if target in parsed_icons:
            file_names[name.lower()] = target

    folder_names: dict[str, str] = {}
    for name, target in manifest.get("folderNames", {}).items():
        if target in parsed_icons and f"{target}-open" in parsed_icons:
            folder_names[name.lower()] = target
    for name, target in EXTRA_FOLDER_NAMES.items():
        if target in parsed_icons and f"{target}-open" in parsed_icons:
            folder_names[name.lower()] = target

    # Light theme overrides for icons that ship a `_light` variant
    light_variants = {
        icon_id[:-6]: icon_id
        for icon_id in parsed_icons
        if icon_id.endswith("_light") and icon_id[:-6] in parsed_icons
    }

    payload = {
        "defaultFile": "file",
        "defaultFolder": "folder",
        "defaultFolderOpen": "folder-open",
        "fileNames": dict(sorted(file_names.items())),
        "fileExtensions": dict(sorted(file_extensions.items())),
        "folderNames": dict(sorted(folder_names.items())),
        "lightVariants": dict(sorted(light_variants.items())),
        "icons": parsed_icons,
    }

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    OUT_JSON.write_text(json.dumps(payload, separators=(",", ":"), ensure_ascii=True), encoding="utf-8")

    license_src = pkg / "LICENSE"
    if license_src.exists():
        (OUT_DIR / "LICENSE-material-icon-theme.txt").write_text(
            license_src.read_text(encoding="utf-8"), encoding="utf-8"
        )

    size_kb = OUT_JSON.stat().st_size / 1024.0
    print(
        f"Wrote {OUT_JSON} ({size_kb:.1f} KB): "
        f"{len(parsed_icons)} icons, {len(file_names)} fileNames, "
        f"{len(file_extensions)} fileExtensions, {len(folder_names)} folderNames, "
        f"{len(light_variants)} lightVariants"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
