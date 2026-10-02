#!/usr/bin/env python3
"""Print the metadata of a GGUF model without downloading the weights.

A GGUF starts with a header and a key/value metadata block, so a few hundred
kilobytes are enough to see which runtime a file belongs to: the architecture
tag and the keys the converter wrote tell you whether nemo-speech, parakeet.cpp,
llama.cpp or whisper.cpp will accept it. That matters when deciding whether a
catalog entry is a download away or a new backend away.

    python tools/gguf_header.py nvidia/parakeet-tdt-0.6b-v3 parakeet-tdt-0.6b-v3.q8_0.gguf
    python tools/gguf_header.py handy-computer/parakeet-tdt-0.6b-v3-gguf parakeet-tdt-0.6b-v3-Q8_0.gguf --keys

Only standard library, and only the first megabyte or so of the file.
"""

from __future__ import annotations

import argparse
import struct
import sys
import urllib.error
import urllib.request

URL = "https://huggingface.co/{repo}/resolve/main/{name}"
HEAD = 1 << 20  # metadata rarely exceeds this
TIMEOUT = 30

# GGUF metadata value types, in the order the format defines them.
UINT8, INT8, UINT16, INT16, UINT32, INT32, FLOAT32, BOOL, STRING, ARRAY, UINT64, INT64, FLOAT64 = range(13)
NUMERIC = {UINT8: ("B", 1), INT8: ("b", 1), UINT16: ("H", 2), INT16: ("h", 2), UINT32: ("I", 4),
           INT32: ("i", 4), FLOAT32: ("f", 4), UINT64: ("Q", 8), INT64: ("q", 8), FLOAT64: ("d", 8)}


class Reader:
    def __init__(self, blob: bytes) -> None:
        self.blob = blob
        self.at = 0

    def take(self, size: int) -> bytes:
        chunk = self.blob[self.at: self.at + size]
        if len(chunk) < size:
            raise EOFError("metadata block is larger than the fetched range")
        self.at += size
        return chunk

    def scalar(self, kind: int):
        if kind == BOOL:
            return bool(self.take(1)[0])
        if kind == STRING:
            # NeMo-Speech.cpp and the other ggml ports write string lengths as
            # uint64, not the uint32 the reference format uses, so read both and
            # keep whichever lines up with the next key.
            return self.take(struct.unpack("<Q", self.take(8))[0]).decode("utf-8", "replace")
        fmt, size = NUMERIC[kind]
        return struct.unpack("<" + fmt, self.take(size))[0]

    def value(self, kind: int, depth: int = 0):
        if kind != ARRAY:
            return self.scalar(kind)
        element, count = struct.unpack("<II", self.take(8))
        if element == STRING or depth > 1:
            # Skip the payload: keys are what matter here.
            self.skip_value(element, count)
            return f"<array of {count}>"
        if count > 64:
            self.skip_value(element, count)
            return f"<array of {count}>"
        return [self.scalar(element) for _ in range(count)]

    def skip_value(self, kind: int, count: int) -> None:
        if kind == STRING:
            for _ in range(count):
                self.take(struct.unpack("<Q", self.take(8))[0])
            return
        self.at += NUMERIC.get(kind, ("B", 1))[1] * count


def read_head(repo: str, name: str, limit: int) -> tuple[Reader, dict]:
    url = URL.format(repo=repo, name=name)
    request = urllib.request.Request(
        url, headers={"User-Agent": "gguf-header", "Range": f"bytes=0-{limit}"})
    with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
        blob = response.read()
    reader = Reader(blob)
    if reader.take(4) != b"GGUF":
        raise ValueError("not a GGUF file (bad magic)")
    version, tensors, kvs = struct.unpack("<IQQ", reader.take(20))
    meta = {"version": version, "tensors": tensors, "kvs": kvs}
    return reader, meta


def parse(repo: str, name: str, limit: int, max_kvs: int) -> dict:
    reader, meta = read_head(repo, name, limit)
    pairs: dict = {}
    try:
        for _ in range(min(meta["kvs"], max_kvs)):
            key = reader.scalar(STRING)
            kind = struct.unpack("<I", reader.take(4))[0]
            if kind > FLOAT64:
                raise ValueError(f"unknown value type {kind}")
            pairs[key] = reader.value(kind)
    except EOFError:
        pass  # the interesting keys come first; the rest is past the range
    return {"header": meta, "keys": pairs}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("target", help="repository, optionally with the file: org/name file.gguf")
    parser.add_argument("--keys", action="store_true", help="print every metadata key")
    parser.add_argument("--max-kvs", type=int, default=120)
    parser.add_argument("--head", type=int, default=HEAD, help="bytes to fetch")
    args = parser.parse_args()

    parts = args.target.split()
    repo, name = (parts[0], parts[1]) if len(parts) == 2 else (parts[0], None)
    if name is None:
        # A bare repository: use its only .gguf file.
        sys.path.insert(0, __file__.rsplit("/", 1)[0])
        from hf_model_files import fetch  # noqa: PLC0415  (local helper, optional)
        ggufs = [e["path"] for e in fetch(repo) if e.get("path", "").endswith(".gguf")]
        if not ggufs:
            print(f"# {repo}: no .gguf files")
            return 1
        name = sorted(ggufs, key=lambda p: "-q8" in p or "-Q8" in p)[0] if any(
            "q8" in p.lower() for p in ggufs) else ggufs[0]

    try:
        result = parse(repo, name, args.head, args.max_kvs)
    except (urllib.error.URLError, urllib.error.HTTPError, ValueError, EOFError) as error:
        print(f"# {repo}/{name}: {error}", file=sys.stderr)
        return 1

    header = result["header"]
    keys = result["keys"]
    print(f"# {repo}/{name}")
    print(f"# GGUF v{header['version']}, {header['tensors']} tensors, "
          f"{header['kvs']} metadata entries read {len(keys)}")
    for key, value in keys.items():
        if key.startswith("general.") or key.startswith("asr.") or key.startswith("whisper."):
            print(f"{key} = {str(value)[:100]}")
    if args.keys:
        print()
        for key, value in keys.items():
            text = str(value)
            print(f"{key} = {text[:160]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
