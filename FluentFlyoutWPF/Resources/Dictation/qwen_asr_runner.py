"""Persistent local runner for the official qwen-asr package."""

import argparse
import json
import os
import sys

os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")


def configure_utf8_streams() -> None:
    """Keep redirected JSON output Unicode-safe on Windows code pages."""
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="strict")


def emit(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def main() -> int:
    configure_utf8_streams()
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", default="cpu")
    args = parser.parse_args()

    try:
        import torch
        from qwen_asr import Qwen3ASRModel

        is_cuda = args.device.startswith("cuda")
        model = Qwen3ASRModel.from_pretrained(
            args.model,
            dtype=torch.bfloat16 if is_cuda else torch.float32,
            device_map=args.device,
            max_inference_batch_size=1,
            max_new_tokens=512,
        )
    except Exception as exc:  # noqa: BLE001 - forwarded to the desktop app
        emit({"ok": False, "ready": False, "error": f"{type(exc).__name__}: {exc}"})
        return 1

    emit({"ok": True, "ready": True})
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            request = json.loads(line)
            results = model.transcribe(
                audio=request["audio"],
                language=request.get("language"),
            )
            result = results[0]
            emit({
                "ok": True,
                "text": getattr(result, "text", ""),
                "language": getattr(result, "language", None),
            })
        except Exception as exc:  # noqa: BLE001 - one bad request must be reportable
            emit({"ok": False, "error": f"{type(exc).__name__}: {exc}"})

    return 0


if __name__ == "__main__":
    sys.exit(main())
