#!/usr/bin/env python3
"""Verify SEC-05 (GHSA-p3jp-9v83-76w9 / CVE-2026-111497) .NET 10 release assets.

Reads already-generated project.assets.json files for Octopus.App and
Octopus.SignalForwarder (never invokes dotnet restore or fetches packages).

One-line restore + check example (using isolated artifacts directory):
  dotnet restore src/Octopus.App/Octopus.App.csproj -p:Configuration=Release -p:RuntimeIdentifier=osx-arm64 -p:UseArtifactsOutput=true -p:ArtifactsPath=/tmp/sec05 && \
  dotnet restore src/Octopus.SignalForwarder/Octopus.SignalForwarder.csproj -p:Configuration=Release -p:RuntimeIdentifier=osx-arm64 -p:UseArtifactsOutput=true -p:ArtifactsPath=/tmp/sec05 && \
  docs/audit/phase-0b/probes/toolchain/check_release_assets.py --rid osx-arm64 --artifacts-dir /tmp/sec05
"""

import argparse
import json
import pathlib
import sys
import xml.etree.ElementTree as ET


def parse_version(raw: str) -> tuple[int, ...]:
    core = raw.strip().split("-", 1)[0].split("+", 1)[0]
    parts = tuple(int(part) for part in core.split("."))
    if len(parts) < 3:
        raise ValueError(f"Expected major.minor.patch version, got {raw!r}")
    return parts


def parse_range_lower_bound(version_range: str) -> tuple[int, ...]:
    trimmed = version_range.strip().lstrip("[(").rstrip("])")
    lower = trimmed.split(",", 1)[0].strip()
    return parse_version(lower)


def is_patched_net10(version: tuple[int, ...], minimum: tuple[int, ...]) -> bool:
    return version[0] == 10 and version >= minimum


def fmt_version(version: tuple[int, ...]) -> str:
    return ".".join(str(part) for part in version)


def check_global_json(
    path: pathlib.Path,
    min_sdk: tuple[int, ...],
    rows: list[tuple[str, str, str, str, str, str]],
    failures: list[str],
) -> None:
    if not path.is_file():
        failures.append(f"global-json: missing {path}")
        rows.append(("global-json", "repo", "-", "global.json", "MISSING", "FAIL"))
        return

    data = json.loads(path.read_text(encoding="utf-8"))
    sdk = data.get("sdk", {})
    version_text = sdk.get("version", "")
    roll_forward = sdk.get("rollForward", "")

    try:
        version = parse_version(version_text)
        ok_ver = version[0] == 10 and version[1] == 0 and version >= min_sdk
    except ValueError:
        ok_ver = False

    rows.append(("global-json", "repo", "-", "sdk.version", version_text or "MISSING", "PASS" if ok_ver else "FAIL"))
    if not ok_ver:
        failures.append(
            f"global-json: sdk.version={version_text!r} must be in .NET 10.0 and >= {fmt_version(min_sdk)}"
        )

    ok_rf = roll_forward == "latestFeature"
    rows.append(("global-json", "repo", "-", "sdk.rollForward", roll_forward or "MISSING", "PASS" if ok_rf else "FAIL"))
    if not ok_rf:
        failures.append(f"global-json: sdk.rollForward={roll_forward!r} must preserve 'latestFeature'")


def check_project_assets(
    project: str,
    assets_path: pathlib.Path,
    rid: str,
    min_pack: tuple[int, ...],
    rows: list[tuple[str, str, str, str, str, str]],
    failures: list[str],
) -> None:
    if not assets_path.is_file():
        failures.append(f"restored-assets: {project} missing {assets_path}")
        rows.append(("restored-assets", project, rid, "project.assets.json", "MISSING", "FAIL"))
        return

    data = json.loads(assets_path.read_text(encoding="utf-8"))
    target_key = f"net10.0/{rid}"
    rid_target = data.get("targets", {}).get(target_key)
    if not isinstance(rid_target, dict):
        failures.append(f"restored-assets: {project} ({rid}) missing targets[{target_key!r}] in {assets_path}")
        rows.append(("restored-assets", project, rid, f"targets[{target_key}]", "MISSING", "FAIL"))
        return

    target_versions: dict[str, tuple[int, ...]] = {}
    for key in rid_target:
        if "/" in key:
            pkg_id, ver_str = key.split("/", 1)
            target_versions[pkg_id.lower()] = parse_version(ver_str)

    lib_versions: dict[str, tuple[int, ...]] = {}
    for key in data.get("libraries", {}):
        if "/" in key:
            pkg_id, ver_str = key.split("/", 1)
            lib_versions[pkg_id.lower()] = parse_version(ver_str)

    required_target_packs = (
        "Microsoft.DotNet.ILCompiler",
        f"runtime.{rid}.Microsoft.DotNet.ILCompiler",
        "Microsoft.NET.ILLink.Tasks",
    )
    for pkg_id in required_target_packs:
        pkg_key = pkg_id.lower()
        target_ver = target_versions.get(pkg_key)
        lib_ver = lib_versions.get(pkg_key)
        if target_ver is None:
            rows.append(("restored-assets", project, rid, pkg_id, "MISSING", "FAIL"))
            failures.append(f"restored-assets: {project} ({rid}) missing {pkg_id} in targets[{target_key!r}]")
            continue
        ok = is_patched_net10(target_ver, min_pack) and lib_ver == target_ver
        ver_label = fmt_version(target_ver)
        rows.append(("restored-assets", project, rid, pkg_id, ver_label, "PASS" if ok else "FAIL"))
        if lib_ver != target_ver:
            failures.append(
                f"restored-assets: {project} ({rid}) {pkg_id} target={ver_label} != library={lib_ver}"
            )
        elif not is_patched_net10(target_ver, min_pack):
            failures.append(
                f"restored-assets: {project} ({rid}) {pkg_id}={ver_label} < {fmt_version(min_pack)} (GHSA-p3jp-9v83-76w9)"
            )

    downloads = (
        data.get("project", {})
        .get("frameworks", {})
        .get("net10.0", {})
        .get("downloadDependencies", [])
    )
    download_versions = {
        dep["name"].lower(): parse_range_lower_bound(dep["version"])
        for dep in downloads
        if isinstance(dep, dict) and "name" in dep and "version" in dep
    }

    required_download_packs = (
        f"Microsoft.NETCore.App.Runtime.NativeAOT.{rid}",
        f"Microsoft.NETCore.App.Runtime.{rid}",
        f"Microsoft.AspNetCore.App.Runtime.{rid}",
        f"runtime.{rid}.Microsoft.DotNet.ILCompiler",
    )
    for pack_id in required_download_packs:
        ver = download_versions.get(pack_id.lower())
        if ver is None:
            rows.append(("restored-assets", project, rid, f"download:{pack_id}", "MISSING", "FAIL"))
            failures.append(f"restored-assets: {project} ({rid}) missing downloadDependency {pack_id}")
            continue
        ok = is_patched_net10(ver, min_pack)
        ver_label = fmt_version(ver)
        rows.append(("restored-assets", project, rid, f"download:{pack_id}", ver_label, "PASS" if ok else "FAIL"))
        if not ok:
            failures.append(
                f"restored-assets: {project} ({rid}) download:{pack_id}={ver_label} < {fmt_version(min_pack)} (GHSA-p3jp-9v83-76w9)"
            )


def check_bundled_versions_metadata(
    props_path: pathlib.Path,
    cross_rids: list[str],
    min_pack: tuple[int, ...],
    rows: list[tuple[str, str, str, str, str, str]],
    failures: list[str],
) -> None:
    if not props_path.is_file():
        failures.append(f"sdk-metadata: missing {props_path}")
        rows.append(("sdk-metadata", "sdk", "-", "Microsoft.NETCoreSdk.BundledVersions.props", "MISSING", "FAIL"))
        return

    root = ET.parse(props_path).getroot()
    ilc_items = [
        elem for elem in root.iter()
        if elem.tag.endswith("KnownILCompilerPack") and elem.attrib.get("TargetFramework") == "net10.0"
    ]
    if len(ilc_items) != 1:
        failures.append(f"sdk-metadata: expected 1 net10.0 KnownILCompilerPack in {props_path}, found {len(ilc_items)}")
        return

    ilc = ilc_items[0]
    ilc_ver = parse_version(ilc.attrib["ILCompilerPackVersion"])
    supported_rids = {
        item.strip().lower()
        for item in ilc.attrib.get("ILCompilerRuntimeIdentifiers", "").split(";")
        if item.strip()
    }
    fw_versions: dict[str, tuple[int, ...]] = {}
    for elem in root.iter():
        if elem.tag.endswith("KnownFrameworkReference") and elem.attrib.get("TargetFramework") == "net10.0":
            ident = elem.attrib.get("Include", "")
            if ident in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
                fw_versions[ident] = parse_version(elem.attrib["LatestRuntimeFrameworkVersion"])

    for rid in cross_rids:
        rid_supported = rid.lower() in supported_rids
        ok_ilc = rid_supported and is_patched_net10(ilc_ver, min_pack)
        ilc_label = fmt_version(ilc_ver) if rid_supported else f"{fmt_version(ilc_ver)}(unsupported-rid)"
        rows.append(("sdk-metadata", "KnownILCompilerPack", rid, "Microsoft.DotNet.ILCompiler", ilc_label, "PASS" if ok_ilc else "FAIL"))
        if not ok_ilc:
            failures.append(
                f"sdk-metadata: KnownILCompilerPack ({rid})={ilc_label} < {fmt_version(min_pack)} or unsupported RID"
            )

        for fw_name in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
            fw_ver = fw_versions.get(fw_name)
            if fw_ver is None:
                rows.append(("sdk-metadata", "KnownFrameworkReference", rid, fw_name, "MISSING", "FAIL"))
                failures.append(f"sdk-metadata: missing net10.0 KnownFrameworkReference {fw_name}")
                continue
            ok_fw = is_patched_net10(fw_ver, min_pack)
            fw_label = fmt_version(fw_ver)
            rows.append(("sdk-metadata", "KnownFrameworkReference", rid, fw_name, fw_label, "PASS" if ok_fw else "FAIL"))
            if not ok_fw:
                failures.append(
                    f"sdk-metadata: KnownFrameworkReference {fw_name} ({rid})={fw_label} < {fmt_version(min_pack)}"
                )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Check SEC-05 .NET 10 release restore assets for Octopus.App and Octopus.SignalForwarder."
    )
    parser.add_argument("--rid", default="osx-arm64", help="Target RID in project.assets.json (default: osx-arm64)")
    parser.add_argument("--repo-root", type=pathlib.Path, default=pathlib.Path("."), help="Repository root (default: .)")
    parser.add_argument("--artifacts-dir", type=pathlib.Path, help="ArtifactsPath root when restored with UseArtifactsOutput=true")
    parser.add_argument("--app-assets", type=pathlib.Path, help="Explicit path to Octopus.App project.assets.json")
    parser.add_argument("--forwarder-assets", type=pathlib.Path, help="Explicit path to Octopus.SignalForwarder project.assets.json")
    parser.add_argument("--global-json", type=pathlib.Path, help="Explicit path to global.json (default: <repo-root>/global.json)")
    parser.add_argument("--no-global-json", action="store_true", help="Skip global.json check")
    parser.add_argument("--min-pack-version", default="10.0.14", help="Minimum patched .NET 10 pack version (default: 10.0.14)")
    parser.add_argument("--min-sdk-version", default="10.0.403", help="Minimum patched .NET 10 SDK version (default: 10.0.403)")
    parser.add_argument("--bundled-versions", type=pathlib.Path, help="Optional SDK Microsoft.NETCoreSdk.BundledVersions.props for cross-RID SDK metadata")
    parser.add_argument("--cross-rids", default="osx-x64,win-x64,linux-x64", help="Comma-separated cross-RIDs for --bundled-versions")
    args = parser.parse_args(argv)

    min_pack = parse_version(args.min_pack_version)
    min_sdk = parse_version(args.min_sdk_version)

    if args.app_assets is not None:
        app_assets = args.app_assets
    elif args.artifacts_dir is not None:
        app_assets = args.artifacts_dir / "obj" / "Octopus.App" / "project.assets.json"
    else:
        app_assets = args.repo_root / "src" / "Octopus.App" / "obj" / "project.assets.json"

    if args.forwarder_assets is not None:
        forwarder_assets = args.forwarder_assets
    elif args.artifacts_dir is not None:
        forwarder_assets = args.artifacts_dir / "obj" / "Octopus.SignalForwarder" / "project.assets.json"
    else:
        forwarder_assets = args.repo_root / "src" / "Octopus.SignalForwarder" / "obj" / "project.assets.json"

    rows: list[tuple[str, str, str, str, str, str]] = []
    failures: list[str] = []

    if not args.no_global_json:
        global_json_path = args.global_json if args.global_json is not None else args.repo_root / "global.json"
        check_global_json(global_json_path, min_sdk, rows, failures)

    check_project_assets("Octopus.App", app_assets, args.rid, min_pack, rows, failures)
    check_project_assets("Octopus.SignalForwarder", forwarder_assets, args.rid, min_pack, rows, failures)

    if args.bundled_versions is not None:
        cross_rids = [rid.strip() for rid in args.cross_rids.split(",") if rid.strip()]
        check_bundled_versions_metadata(args.bundled_versions, cross_rids, min_pack, rows, failures)

    print("source\tproject\trid\titem\tversion\tstatus")
    for row in rows:
        print("\t".join(row))

    if failures:
        for msg in failures:
            print(f"ERROR: {msg}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
