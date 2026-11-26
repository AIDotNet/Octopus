#!/usr/bin/env python3
"""Builds the built-in Bot connector product logos (src/Octopus.App/Assets/ConnectorIcons/connector-icons.json).

Normalizes authentic official brand SVGs into a compact viewBox + path-list JSON
(same schema as model-icons.json) so Octopus renders crisp multi-color and monochrome
product logos directly through Avalonia's DrawingContext with zero reflection (NativeAOT-safe).

Usage:
  python3 tools/generate-connector-icons.py <dir-containing-connector-svgs>
"""
from __future__ import annotations

import importlib.util
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "src" / "Octopus.App" / "Assets" / "ConnectorIcons"
OUT_JSON = OUT_DIR / "connector-icons.json"

# Load the shared SVG parser from tools/generate-model-icons.py
_spec = importlib.util.spec_from_file_location(
    "generate_model_icons", ROOT / "tools" / "generate-model-icons.py"
)
assert _spec and _spec.loader
_mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_mod)

# Connector app id -> (svg filename, color overrides or default root fill)
CONNECTOR_SOURCES: dict[str, tuple[str, str | None, dict[str, str] | None]] = {
    "github":     ("github.svg",          "currentColor", None),
    "vercel":     ("vercel.svg",          "currentColor", None),
    "cloudflare": ("cloudflare.svg",      None,           None),
    "supabase":   ("supabase.svg",        None,           None),
    "neon":       ("neon.svg",            None,           None),
    "netlify":    ("netlify.svg",         None,           {"#014847": "currentColor"}),
    "gitlab":     ("gitlab.svg",          None,           None),
    "context7":   ("context7.svg",        None,           None),
    "slack":      ("slack.svg",           None,           None),
    "sentry":     ("sentry.svg",          None,           {"#362d59": "#6c5fc7"}),
    "datadog":    ("datadog.svg",         None,           {"#632ca6": "#7742c0"}),
    "pagerduty":  ("pagerduty-color.svg", None,           None),
    "linear":     ("linear.svg",          None,           None),
    "notion":     ("notion.svg",          "#000000",      None),
    "jira":       ("jira-color.svg",      None,           None),
    "google":      ("google-lobe.svg",     None,           None),
    "intercom":    ("intercom.svg",        "#286efa",      None),
    "feishu":      ("feishu.svg",          None,           None),
    "dingtalk":    ("dingtalk.svg",        "#0089ff",      None),
    "tencentdocs": ("tencentdocs.svg",     None,           None),
    "gitee":       ("gitee.svg",           "#c71d23",      None),
    "yunxiao":     ("yunxiao.svg",         None,           None),
    "jihulab":     ("jihulab.svg",         None,           None),
    "amap":        ("amap.svg",            None,           None),
    "baidumap":    ("baidumap.svg",        None,           None),
    "tencentmap":  ("tencentmap.svg",      None,           None),
}


def parse_connector_svg(svg_path: Path, default_fill: str | None, overrides: dict[str, str] | None) -> dict | None:
    tree = ET.parse(svg_path)
    root = tree.getroot()
    vb_raw = root.get("viewBox")
    if vb_raw:
        vb = [float(x) for x in _mod.NUMBER_RE.findall(vb_raw)]
        if len(vb) != 4 or vb[2] <= 0 or vb[3] <= 0:
            return None
    else:
        w = _mod.parse_float(root.get("width"), 24.0)
        h = _mod.parse_float(root.get("height"), 24.0)
        vb = [0.0, 0.0, w, h]

    gradients = _mod.collect_gradients(root, vb, overrides)
    shapes: list[dict] = []

    def walk(el, inh_fill, inh_grad, inh_stroke, inh_sw, inh_opacity, inh_eo, inh_mat):
        t = _mod.tag_name(el)
        if t in ("defs", "clipPath", "mask", "linearGradient", "radialGradient", "title", "desc", "metadata"):
            return

        style = _mod.parse_style(el.get("style"))
        raw_fill = el.get("fill") if "fill" in el.attrib else style.get("fill")
        raw_stroke = el.get("stroke") if "stroke" in el.attrib else style.get("stroke")
        raw_sw = el.get("stroke-width") if "stroke-width" in el.attrib else style.get("stroke-width")
        raw_op = el.get("opacity") if "opacity" in el.attrib else style.get("opacity")
        raw_fop = el.get("fill-opacity") if "fill-opacity" in el.attrib else style.get("fill-opacity")
        raw_fr = el.get("fill-rule") if "fill-rule" in el.attrib else style.get("fill-rule")

        if raw_fill is None:
            fill, grad = inh_fill, inh_grad
        else:
            fill, grad = _mod.resolve_paint(raw_fill, gradients, overrides)

        if raw_stroke is None:
            stroke = inh_stroke
        else:
            stroke, _ = _mod.resolve_paint(raw_stroke, gradients, overrides)

        sw = inh_sw if raw_sw is None else _mod.parse_float(raw_sw, inh_sw)
        opacity = inh_opacity * (_mod.parse_float(raw_op, 1.0) if raw_op is not None else 1.0)
        fop = _mod.parse_float(raw_fop, 1.0) if raw_fop is not None else 1.0
        eo = inh_eo if raw_fr is None else (raw_fr.lower() == "evenodd")
        mat = _mod.mul_mat(inh_mat, _mod.parse_transform_matrix(el.get("transform")))

        d = None
        if t == "path":
            raw_d = (el.get("d") or "").strip()
            d = _mod.normalize_path_data(raw_d) if raw_d else None
        elif t == "rect":
            d = _mod.rect_to_path(el)
        elif t == "circle":
            d = _mod.circle_to_path(el)
        elif t == "ellipse":
            d = _mod.ellipse_to_path(el)
        elif t == "polygon":
            d = _mod.poly_to_path(el, close=True)
        elif t == "polyline":
            d = _mod.poly_to_path(el, close=False)

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

    root_fill_raw = root.get("fill")
    if root_fill_raw:
        root_fill, root_grad = _mod.resolve_paint(root_fill_raw, gradients, overrides)
    else:
        root_fill, root_grad = (default_fill or "currentColor", None)
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
        print("Usage: python3 tools/generate-connector-icons.py <svg-dir>", file=sys.stderr)
        return 2

    src_dir = Path(sys.argv[1])
    icons: dict[str, dict] = {}
    for app_id, (filename, default_fill, overrides) in sorted(CONNECTOR_SOURCES.items()):
        svg_path = src_dir / filename
        if not svg_path.exists():
            print(f"error: missing {svg_path}", file=sys.stderr)
            return 1
        parsed = parse_connector_svg(svg_path, default_fill, overrides)
        if not parsed:
            print(f"error: failed to parse {svg_path}", file=sys.stderr)
            return 1
        icons[app_id] = parsed
        print(f"  {app_id:12s} vb={parsed['vb']} paths={len(parsed['p'])}")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    OUT_JSON.write_text(json.dumps({"icons": icons}, separators=(",", ":"), ensure_ascii=True), encoding="utf-8")
    size_kb = OUT_JSON.stat().st_size / 1024.0
    print(f"Wrote {OUT_JSON} ({size_kb:.1f} KB): {len(icons)} connector icons")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
