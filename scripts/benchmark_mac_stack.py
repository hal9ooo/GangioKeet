#!/usr/bin/env python3
"""Benchmark the candidate MLX pairs on this Mac.

This is a development tool, not the server: it loads both engines, runs a
warm-up and records latency plus Metal/swap usage.  It deliberately does
not import the production engine classes so it can select a pair before
those classes are changed.
"""

from __future__ import annotations

import argparse
import json
import math
import statistics
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from dictation_server.model_manifest import load_candidates  # noqa: E402

SAMPLE_RATE = 16000


def summarize(values: list[float]) -> dict[str, float | int | None]:
    if not values:
        return {"count": 0, "p50": None, "p95": None, "max": None}
    ordered = sorted(float(value) for value in values)
    index = max(0, math.ceil(0.95 * len(ordered)) - 1)
    return {
        "count": len(ordered),
        "p50": statistics.median(ordered),
        "p95": ordered[index],
        "max": ordered[-1],
    }


def build_report(
    *,
    pair_id: str,
    load_ms: float,
    samples: list[dict[str, float]],
    metal_peak_bytes: int,
    working_set_bytes: int,
    swap_before: int,
    swap_after: int,
) -> dict:
    return {
        "pair": pair_id,
        "load_ms": load_ms,
        "metal_peak_bytes": metal_peak_bytes,
        "working_set_bytes": working_set_bytes,
        "swap_before": swap_before,
        "swap_after": swap_after,
        "samples": samples,
        "summary": {
            key: summarize([sample[key] for sample in samples])
            for key in ("total_ms", "stt_ms", "llm_ms")
        },
    }


def repeat_pcm(pcm: bytes, seconds: float) -> bytes:
    if not pcm:
        raise ValueError("base PCM must not be empty")
    target_samples = int(seconds * SAMPLE_RATE)
    target_bytes = target_samples * 2
    repeats = target_bytes // len(pcm) + 1
    return (pcm * repeats)[:target_bytes]


def swap_used_bytes() -> int:
    try:
        output = subprocess.check_output(
            ["sysctl", "-n", "vm.swapusage"], text=True, stderr=subprocess.DEVNULL
        )
    except (OSError, subprocess.CalledProcessError):
        return 0
    for token in output.split():
        if token.endswith("M"):
            try:
                return int(float(token[:-1]) * 1024 * 1024)
            except ValueError:
                continue
        if token.endswith("G"):
            try:
                return int(float(token[:-1]) * 1024**3)
            except ValueError:
                continue
    return 0


def _extract_text(result) -> str:
    if isinstance(result, list):
        return " ".join(getattr(item, "text", "") or "" for item in result)
    return getattr(result, "text", None) or ""


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Benchmark MLX dictation pairs")
    parser.add_argument("--candidates", required=True)
    parser.add_argument("--root", default="models")
    parser.add_argument("--pair", required=True, choices=["primary", "stretch", "control"])
    parser.add_argument("--audio", required=True, help="base PCM16 mono 16 kHz file")
    parser.add_argument("--output", required=True)
    parser.add_argument("--short-seconds", type=float, default=4.0)
    parser.add_argument("--medium-seconds", type=float, default=20.0)
    parser.add_argument("--long-seconds", type=float, default=60.0)
    parser.add_argument("--short-runs", type=int, default=5)
    parser.add_argument("--medium-runs", type=int, default=3)
    parser.add_argument("--long-runs", type=int, default=1)
    return parser.parse_args(argv)


def _run_sample(stt, llm, tokenizer, pcm: bytes) -> dict[str, float]:
    from dictation_server.audio import pcm_bytes_to_mlx_array
    from mlx_lm import generate as mlx_generate

    audio = pcm_bytes_to_mlx_array(pcm)
    started = time.perf_counter()
    result = stt.generate(audio, language="Italian", temperature=0.0)
    stt_ms = (time.perf_counter() - started) * 1000.0
    raw = _extract_text(result).strip()

    started = time.perf_counter()
    if raw:
        mlx_generate(
            llm,
            tokenizer,
            prompt=(
                "<profile>default</profile>\n"
                f"<raw_dictation>{raw}</raw_dictation>"
            ),
            max_tokens=64,
            verbose=False,
        )
    llm_ms = (time.perf_counter() - started) * 1000.0
    return {
        "duration_s": len(pcm) / 2 / SAMPLE_RATE,
        "stt_ms": stt_ms,
        "llm_ms": llm_ms,
        "total_ms": stt_ms + llm_ms,
    }


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        import mlx.core as mx
        from mlx_audio.stt import load as load_stt
        from mlx_lm import load as load_llm
    except ImportError as exc:
        print(f"error: MLX runtime unavailable: {exc}", file=sys.stderr)
        return 1

    pairs = load_candidates(args.candidates)
    pair = pairs[args.pair]
    root = Path(args.root)
    base_pcm = Path(args.audio).read_bytes()

    started = time.perf_counter()
    stt = load_stt(str(root / pair.stt.id))
    llm, tokenizer = load_llm(str(root / pair.cleaner.id))
    load_ms = (time.perf_counter() - started) * 1000.0

    swap_before = swap_used_bytes()
    mx.metal.reset_peak_memory()
    _run_sample(stt, llm, tokenizer, repeat_pcm(base_pcm, args.short_seconds))

    samples: list[dict[str, float]] = []
    schedule = (
        (args.short_seconds, args.short_runs),
        (args.medium_seconds, args.medium_runs),
        (args.long_seconds, args.long_runs),
    )
    for seconds, runs in schedule:
        pcm = repeat_pcm(base_pcm, seconds)
        for _ in range(runs):
            samples.append(_run_sample(stt, llm, tokenizer, pcm))

    report = build_report(
        pair_id=args.pair,
        load_ms=load_ms,
        samples=samples,
        metal_peak_bytes=int(mx.metal.get_peak_memory()),
        working_set_bytes=int(
            mx.device_info().get("max_recommended_working_set_size", 0)
        ),
        swap_before=swap_before,
        swap_after=swap_used_bytes(),
    )
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report["summary"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
