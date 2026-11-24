#!/usr/bin/env python3
"""Packages compressed update archives and publishes a release to Octopus Server (https://octoterm.si).

    tools/publish-release-server.py --tag v1.2.1124 --dist dist --server https://octoterm.si --token <token> [--key key.pem] [--notes-file notes.md]

Leaves --dist untouched (so GitHub Releases keeps its exact original files). In a temporary staging folder it:
  1. Copies all release artifacts (excluding GitHub's update.json / update.json.sig).
  2. Compresses Windows (-setup.exe) and Linux (.run) installers into -update.zip archives (macOS is already a ditto .zip).
  3. Runs tools/make-update-manifest.py with --prefer-zip and --download-base-url <server>/releases/download/<tag> to
     produce and ECDSA-sign the server's update.json and update.json.sig.
  4. Uploads to Octopus Server via PUT /v1/ci/releases/{tag} -> PUT .../assets/{name} -> POST .../publish.
"""
import argparse
import hashlib
import http.client
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.parse
import zipfile

VERSION = re.compile(r"^v(\d+\.\d+\.\d+(-preview\.\d+)?)$")
CHUNK_SIZE = 1 << 20  # 1 MiB


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--tag", required=True, help="Release tag, e.g. v1.2.1124")
    parser.add_argument("--dist", required=True, help="Directory with built release artifacts")
    parser.add_argument("--server", default="https://octoterm.si", help="Octopus Server origin")
    parser.add_argument("--token", default=os.environ.get("OCTOPUS_RELEASE_TOKEN"), help="CI Bearer upload token")
    parser.add_argument("--repo", default="AIDotNet/Octopus", help="GitHub owner/repo for notesUrl")
    parser.add_argument("--key", default=os.environ.get("UPDATE_SIGNING_KEY_FILE"), help="PEM private key to sign update.json")
    parser.add_argument("--notes-file", default=None, help="Optional Markdown release notes file")
    parser.add_argument("--notes-url", default=None, help="Optional release notes URL")
    args = parser.parse_args()

    match = VERSION.match(args.tag)
    if not match:
        print(f"invalid tag: {args.tag}", file=sys.stderr)
        return 2
    if not args.token:
        print("OCTOPUS_RELEASE_TOKEN is required", file=sys.stderr)
        return 2

    version = match.group(1)
    server = args.server.rstrip("/")
    notes = None
    if args.notes_file and os.path.isfile(args.notes_file):
        with open(args.notes_file, "r", encoding="utf-8") as f:
            notes = f.read().strip() or None
    notes_url = args.notes_url or f"https://github.com/{args.repo}/releases/tag/{args.tag}"

    with tempfile.TemporaryDirectory(prefix="octopus-server-release-") as staging:
        prepare_staging(args.dist, staging, version)

        manifest_script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "make-update-manifest.py")
        cmd = [
            sys.executable,
            manifest_script,
            "--tag", args.tag,
            "--dist", staging,
            "--repo", args.repo,
            "--download-base-url", f"{server}/releases/download/{args.tag}",
            "--prefer-zip",
        ]
        if args.key:
            cmd.extend(["--key", args.key])
        result = subprocess.run(cmd, check=False)
        if result.returncode != 0:
            print("warning: update manifest was not generated (no desktop update packages?)", file=sys.stderr)

        # 1. Create or update draft release.
        draft_body = json.dumps({"notes": notes, "notesUrl": notes_url}).encode("utf-8")
        request_with_retry(
            server,
            "PUT",
            f"/v1/ci/releases/{urllib.parse.quote(args.tag, safe='')}",
            token=args.token,
            body_bytes=draft_body,
            headers={"Content-Type": "application/json"},
        )
        print(f"draft ready: {args.tag}")

        # 2. Upload all staged files (upload update.json / update.json.sig last so asset verification is clean).
        files = sorted(f for f in os.listdir(staging) if os.path.isfile(os.path.join(staging, f)))
        ordered = [f for f in files if f not in ("update.json", "update.json.sig")]
        for special in ("update.json", "update.json.sig"):
            if special in files:
                ordered.append(special)

        for name in ordered:
            path = os.path.join(staging, name)
            digest = sha256_file(path)
            size = os.path.getsize(path)
            print(f"uploading {name} ({size:,} bytes)...")
            request_with_retry(
                server,
                "PUT",
                f"/v1/ci/releases/{urllib.parse.quote(args.tag, safe='')}/assets/{urllib.parse.quote(name, safe='')}",
                token=args.token,
                file_path=path,
                headers={
                    "Content-Type": "application/octet-stream",
                    "X-Content-Sha256": digest,
                },
            )

        # 3. Publish atomically.
        published = request_with_retry(
            server,
            "POST",
            f"/v1/ci/releases/{urllib.parse.quote(args.tag, safe='')}/publish",
            token=args.token,
            body_bytes=b"{}",
            headers={"Content-Type": "application/json"},
        )
        print(f"published to {server}: {published}")

    return 0


def prepare_staging(dist_dir: str, staging_dir: str, version: str) -> None:
    win_setup = re.compile(r"^Octopus-" + re.escape(version) + r"-win-(?P<arch>[a-z0-9]+)-setup\.exe$")
    linux_run = re.compile(r"^Octopus-" + re.escape(version) + r"-linux-(?P<arch>[a-z0-9]+)\.run$")

    for name in sorted(os.listdir(dist_dir)):
        src = os.path.join(dist_dir, name)
        if not os.path.isfile(src) or name in ("update.json", "update.json.sig"):
            continue
        shutil.copy2(src, os.path.join(staging_dir, name))

        m_win = win_setup.match(name)
        if m_win:
            zip_name = f"Octopus-{version}-win-{m_win['arch']}-update.zip"
            compress_single_file(src, os.path.join(staging_dir, zip_name), name)
            continue

        m_linux = linux_run.match(name)
        if m_linux:
            zip_name = f"Octopus-{version}-linux-{m_linux['arch']}-update.zip"
            compress_single_file(src, os.path.join(staging_dir, zip_name), name)


def compress_single_file(src_path: str, zip_path: str, arcname: str) -> None:
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        zf.write(src_path, arcname=arcname)
    raw = os.path.getsize(src_path)
    packed = os.path.getsize(zip_path)
    print(f"packed {os.path.basename(zip_path)}: {raw:,} -> {packed:,} bytes")


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(CHUNK_SIZE), b""):
            h.update(block)
    return h.hexdigest()


def request_with_retry(
    server: str,
    method: str,
    path: str,
    token: str,
    body_bytes: bytes | None = None,
    file_path: str | None = None,
    headers: dict[str, str] | None = None,
    attempts: int = 3,
) -> str:
    parsed = urllib.parse.urlsplit(server)
    is_https = parsed.scheme == "https"
    host = parsed.hostname or ""
    port = parsed.port or (443 if is_https else 80)
    full_path = parsed.path.rstrip("/") + path

    last_error = None
    for attempt in range(1, attempts + 1):
        conn = (
            http.client.HTTPSConnection(host, port, timeout=300)
            if is_https
            else http.client.HTTPConnection(host, port, timeout=300)
        )
        try:
            req_headers = {"Authorization": f"Bearer {token}", "User-Agent": "Octopus-Release-CI"}
            if headers:
                req_headers.update(headers)

            if file_path is not None:
                size = os.path.getsize(file_path)
                req_headers["Content-Length"] = str(size)
                conn.putrequest(method, full_path)
                for k, v in req_headers.items():
                    conn.putheader(k, v)
                conn.endheaders()
                with open(file_path, "rb") as f:
                    while True:
                        chunk = f.read(CHUNK_SIZE)
                        if not chunk:
                            break
                        conn.send(chunk)
            else:
                data = body_bytes or b""
                req_headers["Content-Length"] = str(len(data))
                conn.request(method, full_path, body=data, headers=req_headers)

            resp = conn.getresponse()
            resp_body = resp.read().decode("utf-8", errors="replace")
            if 200 <= resp.status < 300:
                return resp_body
            if 400 <= resp.status < 500 and resp.status != 429:
                raise RuntimeError(f"{method} {path} failed with HTTP {resp.status}: {resp_body}")
            last_error = RuntimeError(f"{method} {path} failed with HTTP {resp.status}: {resp_body}")
        except (OSError, http.client.HTTPException) as exc:
            last_error = exc
        finally:
            conn.close()

        if attempt < attempts:
            delay = attempt * 3
            print(f"retrying {method} {path} after error ({last_error}); waiting {delay}s...", file=sys.stderr)
            time.sleep(delay)

    raise RuntimeError(f"giving up on {method} {path}: {last_error}")


if __name__ == "__main__":
    sys.exit(main())
