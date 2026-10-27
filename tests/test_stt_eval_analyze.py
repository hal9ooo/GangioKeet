import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS))

import stt_eval_analyze  # noqa: E402


def test_raw_summary_reports_missing_question_even_when_wer_is_zero(tmp_path):
    manifest = {"one": {"duration_bucket": "short", "reference_raw": "come funziona",
                        "reference_final": "Come funziona?", "is_silence": False}}
    report = {"clips": [{"id": "one", "raw": {"score": _score(0, 2),
               "hypothesis": "Come funziona."}}], "pooled_latency": {"p50": 10, "p95": 10},
               "rtf_summary": {"p50": 0.1}, "load_seconds": 1, "peak_rss_mb": 100}
    (tmp_path / "candidate.json").write_text(json.dumps(report), encoding="utf-8")
    summary = stt_eval_analyze.raw_summary(manifest, tmp_path, bootstrap=2)["candidate"]
    assert summary["wer"] == 0
    assert summary["punctuation"]["missed_questions"] == 1


def test_old_unannotated_capture_does_not_enter_analysis_denominators(tmp_path):
    manifest = {"one": {"duration_bucket": "short", "reference_raw": "",
                        "reference_final": "", "is_silence": False}}
    report = {"clips": [{"id": "one", "raw": {"score": _score(0, 2),
               "hypothesis": "Come funziona."}}], "pooled_latency": {"p50": 10, "p95": 10},
               "rtf_summary": {"p50": 0.1}, "load_seconds": 1, "peak_rss_mb": 100}
    (tmp_path / "candidate.json").write_text(json.dumps(report), encoding="utf-8")
    summary = stt_eval_analyze.raw_summary(manifest, tmp_path, bootstrap=2)["candidate"]
    assert summary["wer"] is None
    assert summary["punctuation"] is None


def _score(errors: int, words: int) -> dict:
    return {
        "substitutions": errors,
        "deletions": 0,
        "insertions": 0,
        "reference_words": words,
        "wer": errors / words if words else None,
        "false_positive": False,
    }


def test_analyze_produces_clip_and_cluster_bootstrap(tmp_path):
    manifest = tmp_path / "manifest.jsonl"
    manifest.write_text(
        "\n".join(
            json.dumps(
                {
                    "id": cid,
                    "duration_bucket": bucket,
                    "reference_raw": "uno due",
                }
            )
            for cid, bucket in [
                ("fleurs_it_1_0", "short"),
                ("fleurs_it_2_0", "medium"),
            ]
        )
        + "\n",
        encoding="utf-8",
    )
    raw_dir = tmp_path / "raw"
    clean_dir = tmp_path / "cleaners"
    raw_dir.mkdir()
    clean_dir.mkdir()

    raw = {
        "clips": [
            {"id": "fleurs_it_1_0", "raw": {"score": _score(0, 2)}},
            {"id": "fleurs_it_2_0", "raw": {"score": _score(2, 2)}},
        ],
        "pooled_latency": {"p50": 10.0, "p95": 20.0},
        "rtf_summary": {"p50": 0.1},
        "load_seconds": 1.0,
        "peak_rss_mb": 100.0,
        "peak_metal_mb": 200.0,
        "nondeterministic_clips": [],
    }
    (raw_dir / "parakeet_baseline.json").write_text(
        json.dumps(raw), encoding="utf-8"
    )

    cleaner = {
        "cleaner": {"id": "qwen3_1_7b_4bit"},
        "load_seconds": 1.0,
        "peak_rss_mb": 100.0,
        "peak_metal_mb": 200.0,
        "clips": [
            {
                "id": "fleurs_it_1_0",
                "production": {
                    "score": _score(1, 2),
                    "deterministic": True,
                    "warnings": [],
                },
                "forced": {
                    "score": _score(1, 2),
                    "deterministic": True,
                    "warnings": [],
                },
                "production_latency_ms": [10.0, 10.0],
                "forced_latency_ms": [10.0, 10.0],
            },
            {
                "id": "fleurs_it_2_0",
                "production": {
                    "score": _score(2, 2),
                    "deterministic": True,
                    "warnings": [],
                },
                "forced": {
                    "score": _score(2, 2),
                    "deterministic": True,
                    "warnings": [],
                },
                "production_latency_ms": [10.0, 10.0],
                "forced_latency_ms": [10.0, 10.0],
            },
        ],
    }
    (clean_dir / "qwen3_1_7b_4bit__parakeet_baseline.json").write_text(
        json.dumps(cleaner), encoding="utf-8"
    )

    summary = stt_eval_analyze.analyze(
        raw_dir, clean_dir, manifest, bootstrap=50
    )

    baseline = summary["raw"]["parakeet_baseline"]
    assert baseline["wer"] == 0.5
    assert len(baseline["delta_ci"]) == 2
    assert len(baseline["delta_ci_cluster"]) == 2
    assert baseline["clips"]["fleurs_it_1_0"] == (0, 2)

    cleaner_summary = summary["cleaners"][
        "qwen3_1_7b_4bit__parakeet_baseline"
    ]
    assert cleaner_summary["production_wer"] == 0.75
    assert cleaner_summary["delta_vs_raw"] == 0.25
    assert len(cleaner_summary["delta_ci_cluster"]) == 2
