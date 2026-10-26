"""Phase B: apply one cleaner to one Phase A raw transcript report."""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
for _entry in (SCRIPT_DIR, ROOT):
    if str(_entry) not in sys.path:
        sys.path.insert(0, str(_entry))

import stt_eval  # noqa: E402
import stt_eval_metrics  # noqa: E402
from stt_eval_finalizer import (  # noqa: E402
    CLEANER_IDS,
    CleanerUnavailable,
    PipelineFinalizer,
    UnknownCleaner,
)


def _summary(values: list[float]) -> dict:
    return stt_eval._latency_summary(values)


def run_cleaner(
    raw_path: Path,
    manifest_path: Path,
    cleaner_id: str,
    output: Path,
    *,
    finalizer_factory=None,
) -> dict:
    raw_path = Path(raw_path)
    manifest_path = Path(manifest_path)
    raw = json.loads(raw_path.read_text(encoding="utf-8"))
    manifest = {
        clip["id"]: clip
        for clip in stt_eval.load_manifest(manifest_path)
    }
    raw_clips = raw.get("clips")
    if not isinstance(raw_clips, list) or not raw_clips:
        raise ValueError(f"raw report has no clips: {raw_path}")

    finalizer = (
        finalizer_factory(cleaner_id)
        if finalizer_factory is not None
        else PipelineFinalizer(cleaner_id)
    )
    clip_results: list[dict] = []
    production_per_clip: list[dict] = []
    forced_per_clip: list[dict] = []
    measured_seconds = 0.0
    load_started = time.perf_counter()
    load_seconds = 0.0

    try:
        finalizer.load()
        load_seconds = time.perf_counter() - load_started
        for raw_clip in raw_clips:
            clip_id = raw_clip.get("id")
            reference = manifest.get(clip_id)
            if reference is None:
                raise ValueError(
                    f"clip '{clip_id}' is missing from the manifest"
                )

            hypotheses = (
                raw_clip.get("raw", {}).get("hypotheses")
                or [raw_clip.get("raw_hypothesis", "")]
            )
            if not isinstance(hypotheses, list) or not hypotheses:
                raise ValueError(f"clip '{clip_id}' has no raw hypotheses")

            production_texts: list[str] = []
            forced_texts: list[str] = []
            production_latencies: list[float] = []
            forced_latencies: list[float] = []
            production_warnings: list[list[str]] = []
            forced_warnings: list[list[str]] = []

            for hypothesis in hypotheses:
                started = time.perf_counter()
                production_text = finalizer.finalize(
                    hypothesis, reference["profile"], force_cleaner=False
                )
                production_latencies.append(
                    (time.perf_counter() - started) * 1000.0
                )
                production_texts.append(production_text)
                production_warnings.append(list(finalizer.warnings))

                started = time.perf_counter()
                forced_text = finalizer.finalize(
                    hypothesis, reference["profile"], force_cleaner=True
                )
                forced_latencies.append(
                    (time.perf_counter() - started) * 1000.0
                )
                forced_texts.append(forced_text)
                forced_warnings.append(list(finalizer.warnings))

            measured_seconds += (
                sum(production_latencies) + sum(forced_latencies)
            ) / 1000.0
            production_score = stt_eval_metrics.score_transcripts(
                reference["reference_final"], production_texts[0]
            )
            forced_score = stt_eval_metrics.score_transcripts(
                reference["reference_final"], forced_texts[0]
            )
            production_per_clip.append(
                {
                    "is_silence": reference["is_silence"],
                    "score": production_score,
                }
            )
            forced_per_clip.append(
                {
                    "is_silence": reference["is_silence"],
                    "score": forced_score,
                }
            )
            clip_results.append(
                {
                    "id": clip_id,
                    "category": reference["category"],
                    "profile": reference["profile"],
                    "is_silence": reference["is_silence"],
                    "duration_seconds": raw_clip.get("duration_seconds"),
                    "raw_hypothesis": hypotheses[0],
                    "production": {
                        "hypothesis": production_texts[0],
                        "hypotheses": production_texts,
                        "score": production_score,
                        "deterministic": all(
                            text == production_texts[0]
                            for text in production_texts
                        ),
                        "warnings": production_warnings,
                    },
                    "forced": {
                        "hypothesis": forced_texts[0],
                        "hypotheses": forced_texts,
                        "score": forced_score,
                        "deterministic": all(
                            text == forced_texts[0]
                            for text in forced_texts
                        ),
                        "warnings": forced_warnings,
                    },
                    "production_latency_ms": [
                        round(value, 3) for value in production_latencies
                    ],
                    "forced_latency_ms": [
                        round(value, 3) for value in forced_latencies
                    ],
                    "production_latency": _summary(production_latencies),
                    "forced_latency": _summary(forced_latencies),
                }
            )
    finally:
        try:
            finalizer.close()
        except Exception:
            pass

    result = {
        "schema_version": 1,
        "cleaner": {
            "id": cleaner_id,
            "model_id": getattr(finalizer, "model_id", None),
            "revision": getattr(finalizer, "revision", None),
            "precision": getattr(finalizer, "precision", None),
            "status": getattr(finalizer, "status", None),
        },
        "source_raw": str(raw_path.resolve()),
        "manifest": str(manifest_path.resolve()),
        "load_seconds": round(load_seconds, 3),
        "measured_seconds": round(measured_seconds, 3),
        "peak_rss_mb": round(stt_eval._peak_rss_mb(), 1),
        "peak_metal_mb": stt_eval._peak_metal_mb(),
        "environment": stt_eval._environment(),
        "provenance": {
            "repo_commit": stt_eval._repo_commit(),
            "harness_revision": stt_eval.HARNESS_REVISION,
            "normalizer_revision": stt_eval_metrics.NORMALIZER_REVISION,
            "raw_sha256": stt_eval._sha256_file(raw_path),
            "manifest_sha256": stt_eval._sha256_file(manifest_path),
        },
        "clips": clip_results,
        "aggregate": {
            "production": stt_eval_metrics.aggregate_scores(
                production_per_clip
            ),
            "forced": stt_eval_metrics.aggregate_scores(forced_per_clip),
        },
    }
    stt_eval._write_report(Path(output), result)
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Apply one cleaner to a Phase A raw STT report. "
            "No STT model is loaded."
        )
    )
    parser.add_argument("--raw", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--cleaner")
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--list-cleaners",
        action="store_true",
        help="print cleaner ids and exit",
    )
    args = parser.parse_args(argv)

    if args.list_cleaners:
        print("\n".join(CLEANER_IDS))
        return 0
    if (
        args.raw is None
        or args.manifest is None
        or args.cleaner is None
        or args.output is None
    ):
        parser.error(
            "the following arguments are required: "
            "--raw, --manifest, --cleaner, --output"
        )

    try:
        result = run_cleaner(
            args.raw, args.manifest, args.cleaner, args.output
        )
    except (
        stt_eval.ManifestError,
        CleanerUnavailable,
        UnknownCleaner,
        ValueError,
        OSError,
    ) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    production = result["aggregate"]["production"]
    forced = result["aggregate"]["forced"]
    print(
        f"cleaner: {result['cleaner']['id']} "
        f"({result['cleaner']['model_id']})"
    )
    print(
        f"production WER: {production['wer']:.4f} "
        f"({production['errors']}/{production['reference_words']})"
    )
    print(
        f"forced WER: {forced['wer']:.4f} "
        f"({forced['errors']}/{forced['reference_words']})"
    )
    print(f"report: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
