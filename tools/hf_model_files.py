#!/usr/bin/env python3
"""List the files of Hugging Face model repos with size and SHA-256.

The dictation catalog needs four fields per model (repository, revision, remote
file name, SHA-256) and every one of them has to be exact or the download is
rejected. This prints them straight from the Hugging Face API so nobody has to
copy them by hand.

    python tools/hf_model_files.py nvidia/parakeet-tdt-0.6b-v3 --ext gguf
    python tools/hf_model_files.py --file-list repos.txt --ext gguf --min-size 100000000

No third-party packages: it only uses urllib from the standard library.
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request

API = "https://huggingface.co/api/models/{repo}/tree/{rev}?recursive=1&expand=1"
TIMEOUT = 30


def fetch(repo: str, revision: str = "main") -> list[dict]:
    """Every file of the repository, following the API's own pagination.

    The tree endpoint answers 50 entries at a time and repeats the rest in a
    Link header, which matters: the interesting artifacts of a big repository
    (weights, not readme) sit far past the first page.
    """
    url = API.format(repo=repo, rev=revision)
    entries: list[dict] = []
    seen: set[str] = set()
    while url and url not in seen:
        seen.add(url)
        request = urllib.request.Request(url, headers={"User-Agent": "hf-model-files"})
        with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
            entries += json.load(response)
            url = next_page(response.headers.get("Link", ""))
    return entries


def next_page(link: str | None) -> str | None:
    """Pull the rel="next" URL out of a Link header."""
    if not link:
        return None
    for part in link.split(","):
        if 'rel="next"' in part:
            return part.split(";")[0].strip().lstrip("<").rstrip(">")
    return None


def revision_of(repo: str, revision: str) -> str:
    """Resolve 'main' into the commit sha the catalog has to pin."""
    if revision != "main":
        return revision
    url = f"https://huggingface.co/api/models/{repo}"
    request = urllib.request.Request(url, headers={"User-Agent": "hf-model-files"})
    with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
        return json.load(response)["sha"]


def human(size: int) -> str:
    value = float(size)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if value < 1024 or unit == "TB":
            return f"{value:.1f} {unit}" if unit != "B" else f"{int(value)} B"
        value /= 1024
    return f"{value:.1f} TB"


def report(repo: str, extensions: list[str], min_size: int, revision: str) -> bool:
    try:
        tree = fetch(repo, revision)
        pinned = revision_of(repo, revision)
    except urllib.error.HTTPError as error:
        print(f"# {repo}: HTTP {error.code} {error.reason}", file=sys.stderr)
        return False
    except urllib.error.URLError as error:
        print(f"# {repo}: {error.reason}", file=sys.stderr)
        return False

    rows = []
    for entry in tree:
        if entry.get("type") != "file":
            continue
        name = entry.get("path", "")
        if extensions and not any(name.endswith(ext) for ext in extensions):
            continue
        size = entry.get("size") or 0
        if size < min_size:
            continue
        sha = (entry.get("lfs") or {}).get("oid") or entry.get("blobId") or "-"
        rows.append((size, name, sha))

    if not rows:
        print(f"# {repo}: nothing matched (revision {pinned})")
        return False

    print(f"# {repo}  revision {pinned}")
    for size, name, sha in sorted(rows, reverse=True):
        print(f"{size:>14}  {human(size):>9}  {sha}  {name}")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repos", nargs="*", help="repository ids, org/name")
    parser.add_argument("--file-list", help="file with one repository id per line")
    parser.add_argument("--revision", default="main")
    parser.add_argument("--ext", action="append", default=[], help="extension filter, repeatable")
    parser.add_argument("--min-size", type=int, default=0, help="skip files smaller than this")
    args = parser.parse_args()

    repos = list(args.repos)
    if args.file_list:
        with open(args.file_list, encoding="utf-8") as handle:
            repos += [line.strip() for line in handle if line.strip() and not line.startswith("#")]
    if not repos:
        parser.error("no repositories given")

    found = 0
    for repo in repos:
        found += report(repo, [e if e.startswith(".") else f".{e}" for e in args.ext], args.min_size, args.revision)
        print()
    print(f"# {found}/{len(repos)} repositories returned files", file=sys.stderr)
    return 0 if found else 1


if __name__ == "__main__":
    raise SystemExit(main())
