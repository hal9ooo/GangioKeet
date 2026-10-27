"""Portable analysis for the public Italian STT benchmark.

Reads Phase A raw JSON and Phase B cleaner JSON, then reports WER, paired
bootstrap CIs (clip and sentence-cluster), latency, memory and cleaner deltas.
"""

from __future__ import annotations

import argparse
import json
import random
import re
from collections import Counter, defaultdict
from pathlib import Path

from stt_eval_punctuation import aggregate_punctuation, score_record

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_RAW = ROOT / ".superpowers" / "stt-benchmark" / "raw"
DEFAULT_CLEANERS = ROOT / ".superpowers" / "stt-benchmark" / "cleaners"
DEFAULT_MANIFEST = (
    ROOT
    / "tests"
    / "fixtures"
    / "stt_eval"
    / "public"
    / "fleurs_it"
    / "manifest.jsonl"
)
BOOTSTRAP = 2000
SEED = 20261026
_CLUSTER_RE = re.compile(r"^(fleurs_it_.+)_\d+$")


def load_manifest(path: Path) -> dict:
    out = {}
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if line.strip():
            row = json.loads(line)
            out[row["id"]] = row
    return out


def counts(score: dict | None) -> tuple[int, int]:
    if score is None or score.get("wer") is None:
        return 0, 0
    return (
        score["substitutions"]
        + score["deletions"]
        + score["insertions"],
        score["reference_words"],
    )


def aggregate(rows: list[tuple[int, int]]) -> float | None:
    errors = sum(e for e, _ in rows)
    words = sum(n for _, n in rows)
    return None if not words else errors / words


def _percentile(values: list[float], q: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[int(q * (len(ordered) - 1))]


def _bootstrap(
    pairs: list[tuple[int, int, int]],
    *,
    bootstrap: int,
) -> list[float | None]:
    """pairs are (candidate_errors, reference_errors, reference_words)."""
    if not pairs:
        return [None, None]
    rng = random.Random(SEED)
    values = []
    for _ in range(bootstrap):
        sample = [pairs[rng.randrange(len(pairs))] for _ in range(len(pairs))]
        words = sum(n for _, _, n in sample)
        if words:
            values.append(
                (
                    sum(a for a, _, _ in sample)
                    - sum(b for _, b, _ in sample)
                )
                / words
            )
    values.sort()
    return [
        values[int(0.025 * len(values))],
        values[int(0.975 * len(values)) - 1],
    ]


def _cluster_id(clip_id: str) -> str:
    match = _CLUSTER_RE.match(clip_id)
    return match.group(1) if match else clip_id


def _cluster_bootstrap(
    pairs: list[tuple[int, int, int, str]],
    *,
    bootstrap: int,
) -> list[float | None]:
    groups: dict[str, list[tuple[int, int, int]]] = defaultdict(list)
    for candidate, reference, words, cluster in pairs:
        groups[cluster].append((candidate, reference, words))
    if not groups:
        return [None, None]
    keys = sorted(groups)
    rng = random.Random(SEED)
    values = []
    for _ in range(bootstrap):
        chosen = [keys[rng.randrange(len(keys))] for _ in keys]
        rows = [row for key in chosen for row in groups[key]]
        words = sum(n for _, _, n in rows)
        if words:
            values.append(
                (
                    sum(a for a, _, _ in rows)
                    - sum(b for _, b, _ in rows)
                )
                / words
            )
    values.sort()
    return [
        values[int(0.025 * len(values))],
        values[int(0.975 * len(values)) - 1],
    ]


def _unreviewed(entry: dict) -> bool:
    return entry.get("reference_status") == "unreviewed" or (
        not entry.get("is_silence", False)
        and not (entry.get("reference_raw", "").strip() or entry.get("reference_final", "").strip()))


def raw_summary(manifest: dict, raw_dir: Path, bootstrap: int) -> dict:
    out = {}
    baseline = None
    for path in sorted(Path(raw_dir).glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        clip_rows = []
        bucket = defaultdict(list)
        punctuation_scores = []
        for clip in data["clips"]:
            entry = manifest[clip["id"]]
            unreviewed = _unreviewed(entry)
            e, n = (0, 0) if unreviewed else counts(clip["raw"]["score"])
            punctuation_scores.append(_punctuation_for(entry, clip["raw"]))
            clip_rows.append((e, n))
            bucket[manifest[clip["id"]]["duration_bucket"]].append((e, n))
        out[path.stem] = {
            "wer": aggregate(clip_rows),
            "punctuation": aggregate_punctuation(punctuation_scores),
            "by_bucket": {k: aggregate(v) for k, v in bucket.items()},
            "p50_ms": data["pooled_latency"]["p50"],
            "p95_ms": data["pooled_latency"]["p95"],
            "rtf_p50": data["rtf_summary"]["p50"],
            "load_s": data["load_seconds"],
            "rss_mb": data["peak_rss_mb"],
            "metal_mb": data.get("peak_metal_mb"),
            "nondeterministic": len(data.get("nondeterministic_clips", [])),
            "clips": {
                c["id"]: ((0, 0) if _unreviewed(manifest[c["id"]])
                          else counts(c["raw"]["score"])) for c in data["clips"]
            },
        }
        if path.stem == "parakeet_baseline":
            baseline = out[path.stem]

    if baseline:
        for item in out.values():
            clip_pairs = []
            cluster_pairs = []
            for cid, (errors, words) in item["clips"].items():
                be, bn = baseline["clips"][cid]
                if words and bn:
                    clip_pairs.append((errors, be, words))
                    cluster_pairs.append(
                        (errors, be, words, _cluster_id(cid))
                    )
            item["wer_delta_vs_baseline"] = (
                item["wer"] - baseline["wer"]
                if item["wer"] is not None and baseline["wer"] is not None
                else None
            )
            item["delta_ci"] = _bootstrap(
                clip_pairs, bootstrap=bootstrap
            )
            item["delta_ci_cluster"] = _cluster_bootstrap(
                cluster_pairs, bootstrap=bootstrap
            )
    return out


def _punctuation_for(entry: dict, phase: dict) -> dict | None:
    if _unreviewed(entry):
        return None
    if isinstance(phase.get("hypothesis"), str):
        return score_record(entry, phase["hypothesis"])["punctuation"]
    return phase.get("punctuation")


def cleaner_summary(
    manifest: dict, raw: dict, cleaners_dir: Path, bootstrap: int
) -> dict:
    out = {}
    for path in sorted(Path(cleaners_dir).glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        cleaner_id, stt_id = path.stem.split("__", 1)
        production_rows = []
        forced_rows = []
        delta_pairs = []
        delta_clusters = []
        production_latencies = []
        forced_latencies = []
        nondeterministic = 0
        warnings = Counter()
        production_punctuation, forced_punctuation = [], []
        for clip in data["clips"]:
            entry = manifest[clip["id"]]
            unscored = _unreviewed(entry) or (
                "reference_final" in entry and not entry["reference_final"].strip()
                and not entry.get("is_silence", False))
            production = (0, 0) if unscored else counts(clip["production"]["score"])
            forced = (0, 0) if unscored else counts(clip["forced"]["score"])
            production_punctuation.append(_punctuation_for(entry, clip["production"]))
            forced_punctuation.append(_punctuation_for(entry, clip["forced"]))
            production_rows.append(production)
            forced_rows.append(forced)
            raw_counts = raw.get(stt_id, {}).get("clips", {}).get(clip["id"])
            if forced[1] and raw_counts and raw_counts[1]:
                delta_pairs.append(
                    (forced[0], raw_counts[0], raw_counts[1])
                )
                delta_clusters.append(
                    (
                        forced[0],
                        raw_counts[0],
                        raw_counts[1],
                        _cluster_id(clip["id"]),
                    )
                )
            production_latencies.extend(clip["production_latency_ms"])
            forced_latencies.extend(clip["forced_latency_ms"])
            if not clip["production"]["deterministic"]:
                nondeterministic += 1
            for warning in clip["production"]["warnings"]:
                warnings.update(warning)

        production_wer = aggregate(production_rows)
        raw_wer = raw.get(stt_id, {}).get("wer")
        out[path.stem] = {
            "stt": stt_id,
            "cleaner": cleaner_id,
            "production_wer": production_wer,
            "forced_wer": aggregate(forced_rows),
            "production_punctuation": aggregate_punctuation(production_punctuation),
            "forced_punctuation": aggregate_punctuation(forced_punctuation),
            "production_p50_ms": _percentile(production_latencies, 0.5),
            "production_p95_ms": _percentile(production_latencies, 0.95),
            "forced_p50_ms": _percentile(forced_latencies, 0.5),
            "forced_p95_ms": _percentile(forced_latencies, 0.95),
            "load_s": data.get("load_seconds"),
            "rss_mb": data.get("peak_rss_mb"),
            "metal_mb": data.get("peak_metal_mb"),
            "nondeterministic": nondeterministic,
            "warnings": dict(warnings),
            "delta_vs_raw": (
                production_wer - raw_wer
                if production_wer is not None and raw_wer is not None
                else None
            ),
            "delta_ci": _bootstrap(delta_pairs, bootstrap=bootstrap),
            "delta_ci_cluster": _cluster_bootstrap(
                delta_clusters, bootstrap=bootstrap
            ),
        }
    return out


def analyze(
    raw_dir: Path,
    cleaners_dir: Path,
    manifest_path: Path,
    *,
    bootstrap: int = BOOTSTRAP,
) -> dict:
    manifest = load_manifest(manifest_path)
    raw = raw_summary(manifest, raw_dir, bootstrap)
    cleaners = cleaner_summary(manifest, raw, cleaners_dir, bootstrap)
    return {"raw": raw, "cleaners": cleaners}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Analyze Phase A/B JSON for the Italian STT benchmark."
    )
    parser.add_argument("--raw-dir", type=Path, default=DEFAULT_RAW)
    parser.add_argument(
        "--cleaners-dir", type=Path, default=DEFAULT_CLEANERS
    )
    parser.add_argument(
        "--manifest", type=Path, default=DEFAULT_MANIFEST
    )
    parser.add_argument("--bootstrap", type=int, default=BOOTSTRAP)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)

    summary = analyze(
        args.raw_dir,
        args.cleaners_dir,
        args.manifest,
        bootstrap=args.bootstrap,
    )
    text = json.dumps(summary, indent=2, ensure_ascii=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
        print(f"summary: {args.output}")
    else:
        print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
