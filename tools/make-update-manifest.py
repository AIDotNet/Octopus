#!/usr/bin/env python3
"""Writes update.json for a release, and signs it (design/10-自动更新.md §5).

    tools/make-update-manifest.py --tag v1.2.1112 --dist dist [--repo AIDotNet/Octopus] [--key key.pem]

Reads the packages in <dist> that an install can update itself from:
    Octopus-<version>-win-<arch>-setup.exe   (Inno Setup)
    Octopus-<version>-osx-<arch>.zip         (the signed .app, zipped with ditto)
    Octopus-<version>-linux-<arch>.run       (the .run installer)
and writes <dist>/update.json with each one's download address, size and SHA-256. With --key (or the environment
variable UPDATE_SIGNING_KEY_FILE) it also writes <dist>/update.json.sig: the ECDSA P-256 / SHA-256 signature of the
manifest's bytes, DER, base64 (openssl does the signing). Portable archives (the Windows .zip, the Linux .tar.gz) are left
out: those installs only get a link to the release page.
"""
import argparse
import base64
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile

VERSION = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)(-preview\.\d+)?$")
# Per system, the one kind of file an installed copy updates from.
KINDS = {"win": "-setup.exe", "osx": ".zip", "linux": ".run"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--tag", required=True, help="the release tag, e.g. v1.2.1112 or v1.3.0-preview.2")
    parser.add_argument("--dist", required=True, help="the folder with the release files")
    parser.add_argument("--out", default=None, help="output folder for update.json(.sig); defaults to --dist")
    parser.add_argument("--repo", default="AIDotNet/Octopus", help="owner/name on GitHub")
    parser.add_argument("--download-base-url", default=None, help="base URL for asset downloads (without trailing slash); defaults to GitHub Releases")
    parser.add_argument("--prefer-zip", action="store_true", help="prefer compressed -update.zip packages for Windows and Linux when present")
    parser.add_argument("--key", default=os.environ.get("UPDATE_SIGNING_KEY_FILE"), help="PEM private key to sign with")
    parser.add_argument("--published-at", default=None, help="ISO 8601 time; now by default")
    args = parser.parse_args()

    if not VERSION.match(args.tag):
        print(f"not a release tag: {args.tag} (expected vX.Y.Z or vX.Y.Z-preview.N)", file=sys.stderr)
        return 2
    version = args.tag[1:] if args.tag.startswith("v") else args.tag
    tag = "v" + version
    download_base = (args.download_base_url or f"https://github.com/{args.repo}/releases/download/{tag}").rstrip("/")

    pattern = re.compile(r"^Octopus-" + re.escape(version) + r"-(?P<os>win|osx|linux)-(?P<arch>[a-z0-9]+)(?P<kind>-update\.zip|-setup\.exe|\.zip|\.run)$")
    candidates = {}
    for name in sorted(os.listdir(args.dist)):
        match = pattern.match(name)
        if not match:
            continue
        os_name, arch, kind = match["os"], match["arch"], match["kind"]
        allowed = [KINDS[os_name]]
        if args.prefer_zip and os_name in ("win", "linux"):
            allowed = ["-update.zip", KINDS[os_name]]
        if kind not in allowed:
            continue
        target = f"{os_name}-{arch}"
        priority = allowed.index(kind)
        if target not in candidates or priority < candidates[target][0]:
            candidates[target] = (priority, name)

    assets = {}
    for target, (_, name) in sorted(candidates.items()):
        path = os.path.join(args.dist, name)
        assets[target] = {
            "name": name,
            "url": f"{download_base}/{name}",
            "size": os.path.getsize(path),
            "sha256": sha256(path),
        }
    if not assets:
        print(f"no update packages for {version} in {args.dist}", file=sys.stderr)
        return 1

    published = args.published_at or datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    manifest = {
        "schema": 1,
        "version": version,
        "publishedAt": published,
        "notesUrl": f"https://github.com/{args.repo}/releases/tag/{tag}",
        "assets": assets,
    }
    out_dir = args.out or args.dist
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, "update.json")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"wrote {out}: {', '.join(sorted(assets))}")

    if args.key:
        sig = out + ".sig"
        with tempfile.TemporaryDirectory() as tmp:
            der = os.path.join(tmp, "update.json.der")
            subprocess.run(["openssl", "dgst", "-sha256", "-sign", args.key, "-out", der, out], check=True)
            with open(der, "rb") as f:
                signature = base64.b64encode(f.read()).decode("ascii")
        with open(sig, "w", encoding="ascii", newline="\n") as f:
            f.write(signature + "\n")
        print(f"signed {sig}")
    else:
        print("no signing key: update.json is NOT signed, and installs will not take this release", file=sys.stderr)
    return 0


def sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


if __name__ == "__main__":
    sys.exit(main())
