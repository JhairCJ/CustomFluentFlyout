#!/usr/bin/env python3
"""List the ASR model repositories of one or more Hugging Face organizations.

The dictation catalog only ever grows from what already exists, so the first
step of picking models is seeing the whole shelf. This prints one line per
repository: downloads, likes, pipeline tag and the gguf/onnx tags, which is
what tells us whether a model is a drop-in or needs a converter.

    python tools/hf_list_models.py nvidia moondream oruk --asr --limit 100
    python tools/hf_list_models.py csukuangfj k2-fsa --search parakeet
    python tools/hf_list_models.py nvidia --asr --tag gguf
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

API = "https://huggingface.co/api/models"
TIMEOUT = 30
PAGE = 100


def get(url: str) -> list[dict]:
    request = urllib.request.Request(url, headers={"User-Agent": "hf-list-models"})
    with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
        return json.load(response)


def page_url(args: argparse.Namespace, author: str, skip: int) -> str:
    query = {
        "limit": PAGE,
        "skip": skip,
        "sort": "downloads",
        "direction": -1,
        "full": "false",
    }
    if author:
        query["author"] = author
    if args.asr:
        query["filter"] = "automatic-speech-recognition"
    if args.tag:
        query["filter"] = args.tag
    if args.search:
        query["search"] = args.search
    return f"{API}?{urllib.parse.urlencode(query)}"


def interesting(tags: list[str]) -> str:
    marks = [tag for tag in tags if tag in {
        "gguf", "onnx", "ct2", "nemo", "safetensors", "parakeet", "parakeet_tdt",
        "parakeet_ctc", "whisper", "moonshine", "wav2vec2", "speecht5", "k2-fsa",
        "streaming-asr", "conformer", "fastconformer", "8-bit", "4-bit", "FP8",
        "vllm", "transformers.js", "onnx-asr",
    }]
    return " ".join(marks)


def author_models(author: str, args: argparse.Namespace) -> list[dict]:
    rows: list[dict] = []
    for skip in range(0, args.limit, PAGE):
        try:
            rows += get(page_url(args, author, skip))
        except urllib.error.HTTPError as error:
            print(f"# {author}: HTTP {error.code} {error.reason}", file=sys.stderr)
            break
        if len(rows) < PAGE:
            break
    return rows[: args.limit]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("authors", nargs="+", help="organizations or users")
    parser.add_argument("--asr", action="store_true", help="only automatic-speech-recognition")
    parser.add_argument("--tag", help="extra tag filter, e.g. gguf")
    parser.add_argument("--search", help="substring of the repository id")
    parser.add_argument("--limit", type=int, default=PAGE, help="max rows per author")
    parser.add_argument("--min-downloads", type=int, default=0)
    args = parser.parse_args()

    for author in args.authors:
        print(f"# {author}")
        for model in author_models(author, args):
            downloads = model.get("downloads") or 0
            if downloads < args.min_downloads:
                continue
            identifier = model.get("id", "?")
            if args.search and args.search.lower() not in identifier.lower():
                continue
            tags = model.get("tags") or []
            print(
                f"{downloads:>10}  {model.get('likes') or 0:>5}  "
                f"{(model.get('lastModified') or '')[:10]}  {identifier:<70} {interesting(tags)}"
            )
        print()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
