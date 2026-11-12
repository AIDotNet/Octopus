#!/usr/bin/env python3
"""Version numbers and release checks for Octopus (see ../SKILL.md and design/10-自动更新.md §3, §5).

    release_version.py status [--date MMDD]       existing versions and the candidates for the next one
    release_version.py check <version>            exit 0 when <version> may be released next
    release_version.py verify <tag>               checks a published release: assets, signed manifest, sizes
    release_version.py verify-dir <dir>           checks update.json + update.json.sig in a folder

Versions are major.minor.build with an optional -preview.N, compared like SemVer: number by number, then a preview below
the release with the same numbers, then previews by N. The build is the release date as month * 100 + day (1112 for
November 12, 105 for January 5: a number has no leading zero); across a year the minor goes up, so versions only grow.
"""
import argparse
import base64
import datetime
import json
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
PATTERN = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)(?:-preview\.(\d+))?$")
ENDPOINTS = os.path.join(ROOT, "src", "Octopus.Updates", "UpdateEndpoints.cs")
# What every release must carry. {v} is the version without the leading v.
EXPECTED_ASSETS = [
    "Octopus-{v}-osx-arm64.dmg", "Octopus-{v}-osx-x64.dmg",
    "Octopus-{v}-osx-arm64.zip", "Octopus-{v}-osx-x64.zip",
    "Octopus-{v}-win-x64-setup.exe", "Octopus-{v}-win-x64.zip",
    "Octopus-{v}-linux-x64.run", "Octopus-{v}-linux-x64.tar.gz",
    "update.json", "update.json.sig",
]
MANIFEST_TARGETS = ["osx-arm64", "osx-x64", "win-x64", "linux-x64"]


class Version:
    def __init__(self, major, minor, build, preview=None):
        self.major, self.minor, self.build, self.preview = major, minor, build, preview

    @staticmethod
    def parse(text):
        m = PATTERN.match(text.strip())
        if not m:
            return None
        return Version(int(m[1]), int(m[2]), int(m[3]), int(m[4]) if m[4] is not None else None)

    def key(self):
        # A release sorts after its previews: (numbers, 1, 0) > (numbers, 0, N).
        return (self.major, self.minor, self.build, 0 if self.preview is not None else 1, self.preview or 0)

    def numbers(self):
        return (self.major, self.minor, self.build)

    def __str__(self):
        base = f"{self.major}.{self.minor}.{self.build}"
        return base if self.preview is None else f"{base}-preview.{self.preview}"


def run(args, **kw):
    return subprocess.run(args, cwd=ROOT, check=True, capture_output=True, text=True, **kw).stdout


def tags():
    """Release tags in the local repository and on origin, parsed; other tags are ignored."""
    names = set(run(["git", "tag", "--list", "v*"]).split())
    try:
        remote = run(["git", "ls-remote", "--tags", "origin", "v*"])
        names.update(line.split("refs/tags/")[1].removesuffix("^{}") for line in remote.splitlines() if "refs/tags/" in line)
    except subprocess.CalledProcessError:
        print("warning: origin could not be read; only local tags are known", file=sys.stderr)
    found = []
    for name in names:
        v = Version.parse(name)
        if v:
            found.append((v, name))
    return sorted(found, key=lambda t: t[0].key())


def tag_year(name):
    try:
        return int(run(["git", "log", "-1", "--format=%cs", name])[:4])
    except (subprocess.CalledProcessError, ValueError):
        return None


def candidates(today):
    """The versions that may come next, each with why."""
    known = tags()
    mmdd = today.month * 100 + today.day
    if not known:
        base = Version(0, 1, mmdd)
        return known, [(str(base), "first release"), (f"{base}-preview.1", "first preview")]
    latest, latest_name = known[-1]
    year = tag_year(latest_name) or today.year
    if year < today.year:
        base = Version(latest.major, latest.minor + 1, mmdd)  # a new year: minor up, the date starts over
    elif mmdd > latest.build:
        base = Version(latest.major, latest.minor, mmdd)
    else:
        base = Version(latest.major, latest.minor, latest.build + 1)  # a second line on the same day
    result = []
    if latest.preview is not None:
        result.append((f"{latest.major}.{latest.minor}.{latest.build}-preview.{latest.preview + 1}", "next preview of the current preview line"))
        result.append((f"{latest.major}.{latest.minor}.{latest.build}", "release of the current preview line (promote it)"))
    result.append((f"{base}-preview.1", "new preview line, dated today"))
    result.append((str(base), "new release, dated today"))
    result.append((f"{latest.major + 1}.0.{mmdd}", "new major version (only when asked for)"))
    return known, result


def cmd_status(args):
    today = datetime.date.today()
    if args.date:
        today = today.replace(month=int(args.date[:2]), day=int(args.date[2:]))
    known, options = candidates(today)
    stable = [n for v, n in known if v.preview is None]
    preview = [n for v, n in known if v.preview is not None]
    print(f"latest:          {known[-1][1] if known else '(none)'}")
    print(f"latest release:  {stable[-1] if stable else '(none)'}")
    print(f"latest preview:  {preview[-1] if preview else '(none)'}")
    print("candidates:")
    for version, why in options:
        print(f"  v{version:<24} {why}")
    return 0


def cmd_check(args):
    v = Version.parse(args.version)
    if v is None or args.version.lstrip("v") != str(v):
        print(f"not a release version: {args.version} (expected X.Y.Z or X.Y.Z-preview.N, no leading zeros)", file=sys.stderr)
        return 1
    known = tags()
    if any(existing.key() == v.key() for existing, _ in known):
        print(f"v{v} exists already; a published version is never reused", file=sys.stderr)
        return 1
    if known and v.key() <= known[-1][0].key():
        print(f"v{v} is not above the latest version {known[-1][1]}: installs would never see it", file=sys.stderr)
        return 1
    print(f"ok: v{v} ({'preview' if v.preview is not None else 'release'})")
    return 0


def public_keys():
    text = open(ENDPOINTS, encoding="utf-8").read()
    block = text[text.index("PublicKeys"):]
    return re.findall(r'"([A-Za-z0-9+/=]{80,})"', block)


def verify_signature(manifest_path, signature_path):
    """True when one of the keys in UpdateEndpoints.cs signed the manifest (openssl, like the app does)."""
    with open(signature_path, "rb") as f:
        raw = f.read()
    der = raw if raw[:1] == b"\x30" else base64.b64decode(raw.strip())
    with tempfile.TemporaryDirectory() as tmp:
        sig = os.path.join(tmp, "sig.der")
        with open(sig, "wb") as f:
            f.write(der)
        for i, key in enumerate(public_keys()):
            pem = os.path.join(tmp, f"key{i}.pem")
            with open(pem, "w") as f:
                f.write("-----BEGIN PUBLIC KEY-----\n" + "\n".join(key[j:j + 64] for j in range(0, len(key), 64)) + "\n-----END PUBLIC KEY-----\n")
            r = subprocess.run(["openssl", "dgst", "-sha256", "-verify", pem, "-signature", sig, manifest_path], capture_output=True, text=True)
            if r.returncode == 0:
                return True
    return False


def check_manifest(folder, version, asset_sizes):
    """Problems with update.json in <folder>; asset_sizes maps release file names to sizes (None: not compared)."""
    problems = []
    manifest_path = os.path.join(folder, "update.json")
    if not verify_signature(manifest_path, manifest_path + ".sig"):
        problems.append("update.json is not signed by a key in UpdateEndpoints.PublicKeys: installs will ignore this release")
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
    if version is not None and manifest.get("version") != version:
        problems.append(f"update.json says version {manifest.get('version')}, the tag says {version}")
    assets = manifest.get("assets", {})
    for target in MANIFEST_TARGETS:
        if target not in assets:
            problems.append(f"update.json has no package for {target}: those installs can only be pointed at the page")
    for target, asset in assets.items():
        if len(asset.get("sha256", "")) != 64:
            problems.append(f"{target}: no valid sha256")
        if asset_sizes is not None:
            if asset["name"] not in asset_sizes:
                problems.append(f"{target}: {asset['name']} is not a file of the release")
            elif asset_sizes[asset["name"]] != asset["size"]:
                problems.append(f"{target}: size {asset['size']} in update.json, {asset_sizes[asset['name']]} on the release")
    return manifest, problems


def cmd_verify(args):
    tag = args.tag if args.tag.startswith("v") else "v" + args.tag
    v = Version.parse(tag)
    if v is None:
        print(f"not a release tag: {tag}", file=sys.stderr)
        return 1
    info = json.loads(run(["gh", "release", "view", tag, "--json", "assets,isPrerelease,isDraft,url"]))
    sizes = {a["name"]: a["size"] for a in info["assets"]}
    problems = []
    if info["isDraft"]:
        problems.append("the release is still a draft")
    if info["isPrerelease"] != (v.preview is not None):
        problems.append(f"isPrerelease is {info['isPrerelease']} for a {'preview' if v.preview is not None else 'release'}")
    for name in (n.format(v=str(v)) for n in EXPECTED_ASSETS):
        if name not in sizes:
            problems.append(f"missing asset: {name}")
    if "update.json" in sizes and "update.json.sig" in sizes:
        with tempfile.TemporaryDirectory() as tmp:
            run(["gh", "release", "download", tag, "--pattern", "update.json*", "--dir", tmp])
            _, more = check_manifest(tmp, str(v), sizes)
            problems += more
    print(info["url"])
    return report(problems)


def cmd_verify_dir(args):
    _, problems = check_manifest(args.dir, None, None)
    return report(problems)


def report(problems):
    for p in problems:
        print(f"problem: {p}")
    print("ok" if not problems else f"{len(problems)} problem(s)")
    return 0 if not problems else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("status")
    p.add_argument("--date", help="MMDD instead of today (to try the rules)")
    p.set_defaults(func=cmd_status)
    p = sub.add_parser("check")
    p.add_argument("version")
    p.set_defaults(func=cmd_check)
    p = sub.add_parser("verify")
    p.add_argument("tag")
    p.set_defaults(func=cmd_verify)
    p = sub.add_parser("verify-dir")
    p.add_argument("dir")
    p.set_defaults(func=cmd_verify_dir)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
