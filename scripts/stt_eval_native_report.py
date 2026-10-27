"""Score native C# replay reports with the portable, reference-aware metrics."""
from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from stt_eval_metrics import aggregate_scores
from stt_eval_punctuation import aggregate_punctuation, score_record


def _latency(values: list[float]) -> dict:
    values = sorted(values)
    def percentile(q):
        return values[max(0, math.ceil(q * len(values)) - 1)] if values else None
    return dict(count=len(values), p50_ms=percentile(.5), p95_ms=percentile(.95),
                max_ms=max(values) if values else None,
                over_4000=sum(x > 4000 for x in values),
                fraction_over_4000=sum(x > 4000 for x in values) / len(values) if values else None)


def summarize_report(report: dict) -> dict:
    rows = report["clips"]
    raw, final, raw_punctuation, final_punctuation = [], [], [], []
    latency = defaultdict(list)
    outputs = defaultdict(set)
    unreviewed = synthetic = fallbacks = failures = quality = number_changes = negation_changes = 0
    technical_expected = technical_missing = 0
    for row in rows:
        duration = row.get("duration_seconds")
        bucket = "unknown" if duration is None else "0-10" if duration <= 10 else "10-30" if duration <= 30 else "30-60" if duration <= 60 else "over-60"
        if row.get("total_ms") is not None:
            latency[bucket].append(row["total_ms"])
        outputs[row["id"]].add((row.get("raw_text"), row.get("final_text"), tuple(row.get("warnings", []))))
        fallback = bool(row.get("warnings"))
        fallbacks += fallback
        failed = row.get("status", "completed") != "completed"
        failures += failed
        if row.get("reference_status") == "synthetic_expected":
            synthetic += 1
            continue
        raw_score = score_record(row, row.get("raw_text") or "")
        if not raw_score["scored"]:
            unreviewed += 1
            continue
        if not failed and row.get("raw_text") is not None:
            if raw_score["raw"] is not None:
                raw.append(dict(score=raw_score["raw"], is_silence=row.get("is_silence", False)))
            raw_punctuation.append(raw_score["punctuation"])
        if failed or fallback or row.get("final_text") is None:
            continue
        score = score_record(row, row["final_text"])
        if score["final"] is not None:
            final.append(dict(score=score["final"], is_silence=row.get("is_silence", False)))
            final_punctuation.append(score["punctuation"])
            quality += 1
            reference = row.get("reference_final", "")
            number_changes += re.findall(r"\d+(?:[.,]\d+)*", reference) != re.findall(r"\d+(?:[.,]\d+)*", row["final_text"])
            negation_changes += re.findall(r"\b(?:non|not|never|senza)\b", reference.lower()) != re.findall(r"\b(?:non|not|never|senza)\b", row["final_text"].lower())
            for term in row.get("technical_terms", []):
                technical_expected += 1
                technical_missing += term not in row["final_text"]
    return dict(unique_clips=len(outputs), rows=len(rows), unreviewed_rows=unreviewed,
                synthetic_rows=synthetic, fallback_rows=fallbacks, failed_rows=failures,
                successful_quality_rows=quality, nondeterministic_clips=sum(len(x) > 1 for x in outputs.values()),
                raw=aggregate_scores(raw) if raw else None,
                final=aggregate_scores(final) if final else None,
                raw_punctuation=aggregate_punctuation(raw_punctuation),
                final_punctuation=aggregate_punctuation(final_punctuation),
                number_sequence_changes=number_changes, negation_token_changes=negation_changes,
                technical_terms_expected=technical_expected, technical_terms_missing=technical_missing,
                latency_by_bucket={key: _latency(value) for key, value in latency.items()})


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--freeze-raw", type=Path)
    args = parser.parse_args()
    data = json.loads(args.input.read_text(encoding="utf-8-sig"))
    summary = summarize_report(data)
    summary["source_report_sha256"] = hashlib.sha256(args.input.read_bytes()).hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    if args.freeze_raw:
        fixed, seen = [], set()
        for row in data["clips"]:
            if row.get("status") != "completed" or row["id"] in seen:
                continue
            if row.get("raw_text") is None:
                raise ValueError("Cannot freeze a row without raw transcription.")
            fixed.append(dict(row, fixed_source_report_sha256=summary["source_report_sha256"]))
            seen.add(row["id"])
        args.freeze_raw.parent.mkdir(parents=True, exist_ok=True)
        args.freeze_raw.write_text("".join(json.dumps(x, ensure_ascii=False) + "\n" for x in fixed), encoding="utf-8")
    print(json.dumps({k: summary[k] for k in ("unique_clips", "failed_rows", "fallback_rows", "raw", "final")}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
