#!/usr/bin/env python3
"""Builds the built-in AI model/provider icons (src/Octopus.App/Assets/ModelIcons/model-icons.json).

Source (MIT, see Assets/ModelIcons/README.md):
  @lobehub/icons-static-svg (https://icons.lobehub.com/)

Normalizes each selected SVG into a compact viewBox + path-list JSON (including linear/radial
gradients and currentColor for monochrome logos) so Octopus can render crisp model icons directly
through Avalonia's DrawingContext with zero reflection (NativeAOT-safe) and zero visual child
overhead in popovers and lists.

Usage:
  python3 tools/generate-model-icons.py <path/to/@lobehub/icons-static-svg/package>
"""
from __future__ import annotations

import json
import math
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "src" / "Octopus.App" / "Assets" / "ModelIcons"
OUT_JSON = OUT_DIR / "model-icons.json"

# Canonical icon id -> SVG filename (without .svg) in @lobehub/icons-static-svg/icons/
MODEL_ICONS: dict[str, str] = {
    # Core frontier & coding models
    "claude": "claude-color",
    "anthropic": "anthropic",
    "openai": "openai",
    "gemini": "gemini-color",
    "gemma": "gemma-color",
    "google": "google-color",
    "deepmind": "deepmind-color",
    "palm": "palm-color",
    "vertexai": "vertexai-color",
    "deepseek": "deepseek-color",
    "grok": "grok",
    "xai": "xai",
    "qwen": "qwen-color",
    "meta": "meta-color",
    "mistral": "mistral-color",
    "kimi": "kimi-color",
    "moonshot": "moonshot",
    "minimax": "minimax-color",
    "zai": "zai",
    "zhipu": "zhipu-color",
    "chatglm": "chatglm-color",
    "glmv": "glmv-color",
    "codegeex": "codegeex-color",
    "doubao": "doubao-color",
    "bytedance": "bytedance-color",
    "stepfun": "stepfun-color",
    "baichuan": "baichuan-color",
    "yi": "yi-color",
    "cohere": "cohere-color",
    "aya": "aya-color",
    "perplexity": "perplexity-color",
    "nvidia": "nvidia-color",
    "microsoft": "microsoft-color",
    "hunyuan": "hunyuan-color",
    "wenxin": "wenxin-color",
    "spark": "spark-color",
    "internlm": "internlm-color",
    "sensenova": "sensenova-color",
    "nova": "nova-color",
    "aws": "aws-color",
    "bedrock": "bedrock-color",
    "ollama": "ollama",
    "openrouter": "openrouter",
    "cursor": "cursor",
    "githubcopilot": "githubcopilot",
    "xiaomimimo": "xiaomimimo",
    "longcat": "longcat-color",
    "kwaipilot": "kwaipilot-color",
    "ling": "ling-color",
    "muse": "muse-color",
    "flux": "flux",
    "stability": "stability-color",
    "sora": "sora-color",
    "dalle": "dalle-color",
    "ibm": "ibm",
    "ai21": "ai21",
    "upstage": "upstage-color",
    "snowflake": "snowflake-color",
    "dbrx": "dbrx-color",
    "liquid": "liquid",
    "ai2": "ai2-color",
    "reka": "reka",
    "inflection": "inflection",
    "voyage": "voyage-color",
    "jina": "jina",
    "huggingface": "huggingface-color",
    "kling": "kling-color",
    "suno": "suno",
    "v0": "v0",
    "morph": "morph-color",
    "skywork": "skywork-color",
    "tiangong": "tiangong-color",
    "nanobanana": "nanobanana",
    "menlo": "menlo",
    "baiducloud": "baiducloud-color",
    "alibaba": "alibaba-color",
}

# Brand color tweaks for readability on both light and dark surfaces (matching Assets/AgentLogos/README.md).
COLOR_OVERRIDES: dict[str, dict[str, str]] = {
    "qwen": {"#082dff": "#615ced"},
}

NUMBER_RE = re.compile(r"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")
CMD_PARAM_COUNTS: dict[str, int] = {
    "M": 2, "m": 2,
    "L": 2, "l": 2,
    "H": 1, "h": 1,
    "V": 1, "v": 1,
    "C": 6, "c": 6,
    "S": 4, "s": 4,
    "Q": 4, "q": 4,
    "T": 2, "t": 2,
    "A": 7, "a": 7,
    "Z": 0, "z": 0,
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
    m = NUMBER_RE.match(val.strip())
    return float(m.group(0)) if m else default


def parse_coord(val: str | None, default: float, is_user_space: bool, size: float) -> float:
    """Parses a gradient coordinate. Percentage values convert to [0..1] (or [0..size] in userSpaceOnUse)."""
    if val is None or val.strip() == "":
        return default
    s = val.strip()
    if s.endswith("%"):
        pct = float(s[:-1]) / 100.0
        return pct * size if is_user_space else pct
    return float(s)


def parse_offset(val: str | None, default: float) -> float:
    if val is None or val.strip() == "":
        return default
    s = val.strip()
    if s.endswith("%"):
        return max(0.0, min(1.0, float(s[:-1]) / 100.0))
    return max(0.0, min(1.0, float(s)))


def fmt(v: float) -> str:
    s = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if s in ("", "-0") else s


def normalize_hex_color(raw: str | None, overrides: dict[str, str] | None = None) -> str | None:
    if raw is None:
        return None
    raw = raw.strip()
    if not raw or raw.lower() in ("none", "transparent"):
        return None
    if raw.lower() == "currentcolor":
        return "currentColor"
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
    low = raw.lower()
    if low in named:
        col = named[low]
    elif low.startswith("#"):
        if len(low) == 4:
            col = "#" + "".join(c * 2 for c in low[1:])
        else:
            col = low
    else:
        rgb = re.match(r"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", low)
        if rgb:
            col = f"#{int(rgb.group(1)):02x}{int(rgb.group(2)):02x}{int(rgb.group(3)):02x}"
        else:
            return None
    if overrides and col in overrides:
        return overrides[col]
    return col


def normalize_path_data(d: str) -> str:
    """Tokenizes SVG path data strictly per SVG 1.1 grammar so Avalonia's StreamGeometry.Parse
    never chokes on compact arc flags like `a14 14 0 01-4-3` or leading-dot floats `.5`."""
    out: list[str] = []
    i = 0
    n = len(d)
    cmd = ""

    def skip_ws_comma() -> None:
        nonlocal i
        while i < n and (d[i].isspace() or d[i] == ","):
            i += 1

    def read_number() -> str:
        nonlocal i
        skip_ws_comma()
        m = NUMBER_RE.match(d, i)
        if not m:
            raise ValueError(f"Expected number at index {i} in path: {d[max(0, i-15):i+15]!r}")
        i = m.end()
        s = m.group(0)
        # Ensure leading zero for fractional numbers like ".5" or "-.5" for strict parsers.
        if s.startswith("."):
            s = "0" + s
        elif s.startswith("-."):
            s = "-0" + s[1:]
        elif s.startswith("+."):
            s = "0" + s[1:]
        elif s.startswith("+"):
            s = s[1:]
        return s

    def read_flag() -> str:
        nonlocal i
        skip_ws_comma()
        if i < n and d[i] in ("0", "1"):
            ch = d[i]
            i += 1
            return ch
        raise ValueError(f"Expected arc flag at index {i} in path: {d[max(0, i-15):i+15]!r}")

    while True:
        skip_ws_comma()
        if i >= n:
            break
        ch = d[i]
        if ch in CMD_PARAM_COUNTS:
            cmd = ch
            i += 1
            if cmd in ("Z", "z"):
                out.append(cmd)
                continue
        else:
            if not cmd:
                raise ValueError(f"Unexpected char {ch!r} at start of path")
            # Implicit repeated command (after M/m becomes L/l)
            if cmd == "M":
                cmd = "L"
            elif cmd == "m":
                cmd = "l"

        count = CMD_PARAM_COUNTS[cmd]
        if cmd in ("A", "a"):
            rx = read_number()
            ry = read_number()
            rot = read_number()
            laf = read_flag()
            sf = read_flag()
            x = read_number()
            y = read_number()
            out.append(f"{cmd} {rx} {ry} {rot} {laf} {sf} {x} {y}")
        else:
            nums = [read_number() for _ in range(count)]
            out.append(f"{cmd} " + " ".join(nums))

    return " ".join(out)


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
        return f"M {fmt(x)} {fmt(y)} h {fmt(w)} v {fmt(h)} h {fmt(-w)} Z"
    return (
        f"M {fmt(x + rx)} {fmt(y)} "
        f"H {fmt(x + w - rx)} A {fmt(rx)} {fmt(ry)} 0 0 1 {fmt(x + w)} {fmt(y + ry)} "
        f"V {fmt(y + h - ry)} A {fmt(rx)} {fmt(ry)} 0 0 1 {fmt(x + w - rx)} {fmt(y + h)} "
        f"H {fmt(x + rx)} A {fmt(rx)} {fmt(ry)} 0 0 1 {fmt(x)} {fmt(y + h - ry)} "
        f"V {fmt(y + ry)} A {fmt(rx)} {fmt(ry)} 0 0 1 {fmt(x + rx)} {fmt(y)} Z"
    )


def circle_to_path(el: ET.Element) -> str | None:
    cx = parse_float(el.get("cx"))
    cy = parse_float(el.get("cy"))
    r = parse_float(el.get("r"))
    if r <= 0:
        return None
    return (
        f"M {fmt(cx - r)} {fmt(cy)} "
        f"A {fmt(r)} {fmt(r)} 0 1 0 {fmt(cx + r)} {fmt(cy)} "
        f"A {fmt(r)} {fmt(r)} 0 1 0 {fmt(cx - r)} {fmt(cy)} Z"
    )


def ellipse_to_path(el: ET.Element) -> str | None:
    cx = parse_float(el.get("cx"))
    cy = parse_float(el.get("cy"))
    rx = parse_float(el.get("rx"))
    ry = parse_float(el.get("ry"))
    if rx <= 0 or ry <= 0:
        return None
    return (
        f"M {fmt(cx - rx)} {fmt(cy)} "
        f"A {fmt(rx)} {fmt(ry)} 0 1 0 {fmt(cx + rx)} {fmt(cy)} "
        f"A {fmt(rx)} {fmt(ry)} 0 1 0 {fmt(cx - rx)} {fmt(cy)} Z"
    )


def poly_to_path(el: ET.Element, close: bool) -> str | None:
    pts = NUMBER_RE.findall(el.get("points") or "")
    if len(pts) < 4:
        return None
    segs = [f"M {pts[0]} {pts[1]}"]
    for idx in range(2, len(pts) - 1, 2):
        segs.append(f"L {pts[idx]} {pts[idx + 1]}")
    if close:
        segs.append("Z")
    return " ".join(segs)


def collect_gradients(svg_root: ET.Element, vb: list[float], overrides: dict[str, str] | None) -> dict[str, dict]:
    grads: dict[str, dict] = {}
    vb_w, vb_h = vb[2], vb[3]
    for el in svg_root.iter():
        t = tag_name(el)
        gid = el.get("id")
        if t not in ("linearGradient", "radialGradient") or not gid:
            continue
        stops_raw = [c for c in el if tag_name(c) == "stop"]
        if not stops_raw:
            continue
        stops: list[dict] = []
        for idx, s in enumerate(stops_raw):
            st = parse_style(s.get("style"))
            raw_col = s.get("stop-color") or st.get("stop-color") or "#000000"
            col = normalize_hex_color(raw_col, overrides)
            if not col or col == "currentColor":
                col = "#000000"
            default_off = 0.0 if len(stops_raw) == 1 else idx / (len(stops_raw) - 1)
            off = round(parse_offset(s.get("offset"), default_off), 4)
            raw_op = s.get("stop-opacity") if "stop-opacity" in s.attrib else st.get("stop-opacity")
            op = round(parse_float(raw_op, 1.0), 3) if raw_op is not None else 1.0
            entry: dict = {"t": off, "c": col}
            if op < 0.999:
                entry["o"] = op
            stops.append(entry)

        user_space = (el.get("gradientUnits") or "").lower() == "userspaceonuse"
        if t == "linearGradient":
            x1 = round(parse_coord(el.get("x1"), 0.0, user_space, vb_w), 4)
            y1 = round(parse_coord(el.get("y1"), 0.0, user_space, vb_h), 4)
            x2 = round(parse_coord(el.get("x2"), vb_w if user_space else 1.0, user_space, vb_w), 4)
            y2 = round(parse_coord(el.get("y2"), 0.0, user_space, vb_h), 4)
            grads[gid] = {
                "k": "l",
                "u": user_space,
                "pts": [x1, y1, x2, y2],
                "st": stops,
            }
        else:
            cx = round(parse_coord(el.get("cx"), vb_w / 2.0 if user_space else 0.5, user_space, vb_w), 4)
            cy = round(parse_coord(el.get("cy"), vb_h / 2.0 if user_space else 0.5, user_space, vb_h), 4)
            r = round(parse_coord(el.get("r"), vb_w / 2.0 if user_space else 0.5, user_space, vb_w), 4)
            fx = round(parse_coord(el.get("fx"), cx, user_space, vb_w), 4)
            fy = round(parse_coord(el.get("fy"), cy, user_space, vb_h), 4)
            grads[gid] = {
                "k": "r",
                "u": user_space,
                "pts": [cx, cy, r, fx, fy],
                "st": stops,
            }
    return grads


def parse_transform_matrix(t: str | None) -> list[float] | None:
    if not t:
        return None
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
        nums = [float(x) for x in NUMBER_RE.findall(args_str)]
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


def resolve_paint(raw: str | None, gradients: dict[str, dict], overrides: dict[str, str] | None) -> tuple[str | None, dict | None]:
    if raw is None:
        return None, None
    s = raw.strip()
    if not s or s.lower() in ("none", "transparent"):
        return None, None
    m = re.match(r"url\(#([^)]+)\)", s)
    if m:
        grad = gradients.get(m.group(1))
        return (None, grad) if grad is not None else (None, None)
    return normalize_hex_color(s, overrides), None


def parse_svg(svg_path: Path, icon_id: str) -> dict | None:
    tree = ET.parse(svg_path)
    root = tree.getroot()
    vb_raw = root.get("viewBox")
    if vb_raw:
        vb = [float(x) for x in NUMBER_RE.findall(vb_raw)]
        if len(vb) != 4 or vb[2] <= 0 or vb[3] <= 0:
            return None
    else:
        w = parse_float(root.get("width"), 24.0)
        h = parse_float(root.get("height"), 24.0)
        vb = [0.0, 0.0, w, h]

    overrides = COLOR_OVERRIDES.get(icon_id)
    gradients = collect_gradients(root, vb, overrides)

    # Special case: nova-color.svg uses an alpha <mask> containing the star silhouette over a
    # gradient-filled rect. Extracting the alpha mask's path with that gradient yields the exact artwork.
    if icon_id == "nova":
        mask_path = None
        for el in root.iter():
            if tag_name(el) == "mask" and "alpha" in (el.get("style") or ""):
                for child in el:
                    if tag_name(child) == "path" and child.get("d"):
                        mask_path = normalize_path_data(child.get("d") or "")
                        break
        grad = next(iter(gradients.values()), None)
        if mask_path and grad:
            return {
                "vb": [int(v) if v.is_integer() else round(v, 3) for v in vb],
                "p": [{"d": mask_path, "g": grad}],
            }

    shapes: list[dict] = []

    def walk(
        el: ET.Element,
        inh_fill: str | None,
        inh_grad: dict | None,
        inh_stroke: str | None,
        inh_sw: float,
        inh_opacity: float,
        inh_eo: bool,
        inh_mat: list[float] | None,
    ) -> None:
        t = tag_name(el)
        if t in ("defs", "clipPath", "mask", "linearGradient", "radialGradient", "title", "desc", "metadata"):
            return

        style = parse_style(el.get("style"))
        raw_fill = el.get("fill") if "fill" in el.attrib else style.get("fill")
        raw_stroke = el.get("stroke") if "stroke" in el.attrib else style.get("stroke")
        raw_sw = el.get("stroke-width") if "stroke-width" in el.attrib else style.get("stroke-width")
        raw_op = el.get("opacity") if "opacity" in el.attrib else style.get("opacity")
        raw_fop = el.get("fill-opacity") if "fill-opacity" in el.attrib else style.get("fill-opacity")
        raw_fr = el.get("fill-rule") if "fill-rule" in el.attrib else style.get("fill-rule")

        if raw_fill is None:
            fill, grad = inh_fill, inh_grad
        else:
            fill, grad = resolve_paint(raw_fill, gradients, overrides)

        if raw_stroke is None:
            stroke = inh_stroke
        else:
            stroke, _ = resolve_paint(raw_stroke, gradients, overrides)

        sw = inh_sw if raw_sw is None else parse_float(raw_sw, inh_sw)
        opacity = inh_opacity * (parse_float(raw_op, 1.0) if raw_op is not None else 1.0)
        fop = parse_float(raw_fop, 1.0) if raw_fop is not None else 1.0
        eo = inh_eo if raw_fr is None else (raw_fr.lower() == "evenodd")
        mat = mul_mat(inh_mat, parse_transform_matrix(el.get("transform")))

        d: str | None = None
        if t == "path":
            raw_d = (el.get("d") or "").strip()
            d = normalize_path_data(raw_d) if raw_d else None
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
            if eff_op > 0.001 and (fill is not None or grad is not None or stroke is not None):
                entry: dict = {"d": d}
                if grad is not None:
                    entry["g"] = grad
                elif fill is not None:
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
            walk(child, fill, grad, stroke, sw, opacity, eo, mat)

    # SVG default fill is black if unspecified, unless root specifies fill="currentColor" etc.
    root_fill_raw = root.get("fill")
    root_fill, root_grad = resolve_paint(root_fill_raw, gradients, overrides) if root_fill_raw else ("currentColor", None)
    root_eo = (root.get("fill-rule") or "").lower() == "evenodd"
    for child in root:
        walk(child, root_fill, root_grad, None, 1.0, 1.0, root_eo, None)

    if not shapes:
        return None
    return {
        "vb": [int(v) if v.is_integer() else round(v, 3) for v in vb],
        "p": shapes,
    }


def main() -> int:
    if len(sys.argv) < 2:
        print("Usage: python3 tools/generate-model-icons.py <@lobehub/icons-static-svg/package>", file=sys.stderr)
        return 2

    pkg = Path(sys.argv[1])
    icons_dir = pkg / "icons"
    if not icons_dir.is_dir():
        print(f"error: icons directory not found at {icons_dir}", file=sys.stderr)
        return 1

    parsed_icons: dict[str, dict] = {}
    for icon_id, svg_name in sorted(MODEL_ICONS.items()):
        svg_path = icons_dir / f"{svg_name}.svg"
        if not svg_path.exists():
            print(f"warning: missing SVG '{svg_name}.svg' for '{icon_id}'", file=sys.stderr)
            continue
        parsed = parse_svg(svg_path, icon_id)
        if parsed is None:
            print(f"warning: failed to parse '{svg_name}.svg' for '{icon_id}'", file=sys.stderr)
            continue
        parsed_icons[icon_id] = parsed

    payload = {"icons": parsed_icons}
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    OUT_JSON.write_text(json.dumps(payload, separators=(",", ":"), ensure_ascii=True), encoding="utf-8")

    license_src = pkg / "LICENSE"
    if license_src.exists():
        (OUT_DIR / "LICENSE-lobehub-icons.txt").write_text(
            license_src.read_text(encoding="utf-8"), encoding="utf-8"
        )

    size_kb = OUT_JSON.stat().st_size / 1024.0
    print(f"Wrote {OUT_JSON} ({size_kb:.1f} KB): {len(parsed_icons)} model icons")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
