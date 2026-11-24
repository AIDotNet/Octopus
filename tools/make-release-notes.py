#!/usr/bin/env python3
"""Writes the release notes of a tag as Markdown, from the commits since the previous release.

    tools/make-release-notes.py --tag v1.0.1133 [--repo AIDotNet/Octopus] [--out notes.md]

GitHub's generated notes only list merged pull requests, so a release made of commits pushed straight to main came out
with nothing but a "Full Changelog" link. These notes list every commit instead (merge commits left out), grouped into
new / fixed / improved by the verb the subject starts with (commits that only touch tests, CI or design docs left
out), with a link to the pull request a commit came in through
and "#N" references turned into links. The last line is GitHub's "**Full Changelog**: <compare>" so the app's
ReleaseNotesTidy shows it as a link.

The previous release of a stable tag is the stable tag before it (a stable release lists everything since the last
stable one, previews included); of a preview tag, the tag before it of either kind. Needs the full history and the
tags (actions/checkout with fetch-depth: 0).
"""
import argparse
import re
import subprocess
import sys

TAG = re.compile(r"^v(\d+)\.(\d+)\.(\d+)(?:-preview\.(\d+))?$")
MERGE_PR = re.compile(r"^Merge pull request #(\d+) from ")
ISSUE_REF = re.compile(r"(?<![\w/\[])#(\d+)\b")
MAX_ENTRIES = 80

# The first word of a subject decides its section; anything else is an improvement.
NEW = {"add", "adds", "added", "support", "supports", "introduce", "introduces", "implement", "implements", "new", "allow", "allows", "enable", "enables", "show", "shows"}
FIXED = {"fix", "fixes", "fixed", "repair", "resolve", "resolves", "correct", "corrects", "prevent", "prevents", "avoid", "avoids", "stop", "stops", "restore", "restores", "handle"}
SECTIONS = (("new", "New"), ("fixed", "Fixed"), ("improved", "Improved"))
# Housekeeping that says nothing to someone installing the update.
SKIP = re.compile(r"^(Merge (branch|remote-tracking branch) |Revert \"Revert |Bump version|Release v?\d)", re.IGNORECASE)
# A commit that only touches these changes nothing in what ships.
INTERNAL = re.compile(r"^(tests/|\.github/|design/|docs/)")


def git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def key(tag: str):
    m = TAG.match(tag)
    if not m:
        return None
    major, minor, patch, preview = m.groups()
    # A preview sorts before the stable release of the same number.
    return (int(major), int(minor), int(patch), int(preview) if preview else 1 << 30)


def previous_tag(tag: str) -> str | None:
    current = key(tag)
    if current is None:
        return None
    stable = "-preview." not in tag
    best = None
    for other in git("tag", "--list", "v*").split():
        k = key(other)
        if k is None or k >= current or (stable and "-preview." in other):
            continue
        if best is None or k > best[0]:
            best = (k, other)
    return best[1] if best else None


def pull_requests(range_spec: str) -> dict[str, int]:
    """The commit → pull request it was merged in, from the "Merge pull request #N" commits in the range."""
    owner = {}
    for line in git("log", "--merges", "--format=%H %P%x1f%s", range_spec).splitlines():
        ids, _, subject = line.partition("\x1f")
        m = MERGE_PR.match(subject)
        parts = ids.split()
        if not m or len(parts) < 3:
            continue
        number = int(m.group(1))
        for sha in git("rev-list", f"{parts[1]}..{parts[2]}").split():
            owner.setdefault(sha, number)
    return owner


def internal_only(sha: str) -> bool:
    files = [f for f in git("show", "--name-only", "--format=", sha).splitlines() if f]
    return bool(files) and all(INTERNAL.match(f) for f in files)


def section(subject: str) -> str:
    word = re.split(r"[\s:(]", subject.strip(), maxsplit=1)[0].lower()
    if word in NEW:
        return "new"
    if word in FIXED:
        return "fixed"
    return "improved"


def notes(tag: str, repo: str) -> str:
    previous = previous_tag(tag)
    range_spec = f"{previous}..{tag}" if previous else tag
    base = f"https://github.com/{repo}"
    owner = pull_requests(range_spec)

    grouped: dict[str, list[str]] = {name: [] for name, _ in SECTIONS}
    seen = set()
    count = 0
    for line in git("log", "--no-merges", "--format=%H%x1f%s", range_spec).splitlines():
        sha, _, subject = line.partition("\x1f")
        subject = subject.strip()
        if not subject or SKIP.match(subject) or subject in seen or internal_only(sha):
            continue
        seen.add(subject)
        count += 1
        if count > MAX_ENTRIES:
            continue
        text = ISSUE_REF.sub(lambda m: f"[#{m.group(1)}]({base}/issues/{m.group(1)})", subject)
        pr = owner.get(sha)
        if pr is not None and f"#{pr}]" not in text:
            text += f" ([#{pr}]({base}/pull/{pr}))"
        grouped[section(subject)].append(f"- {text}")

    used = [(title, grouped[name]) for name, title in SECTIONS if grouped[name]]
    out = []
    if len(used) == 1:
        out.extend(used[0][1])
    else:
        for title, items in used:
            if out:
                out.append("")
            out.append(f"### {title}")
            out.append("")
            out.extend(items)
    if count > MAX_ENTRIES:
        out.append(f"- … and {count - MAX_ENTRIES} more")
    if previous:
        if out:
            out.append("")
        out.append(f"**Full Changelog**: {base}/compare/{previous}...{tag}")
    return "\n".join(out).strip() + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tag", required=True, help="The release tag, e.g. v1.0.1133")
    parser.add_argument("--repo", default="AIDotNet/Octopus", help="GitHub owner/repo for the links")
    parser.add_argument("--out", default=None, help="Write here instead of to stdout")
    args = parser.parse_args()
    text = notes(args.tag, args.repo)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
