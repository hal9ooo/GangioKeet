from scripts.stt_eval_native_report import summarize_report


def test_native_report_keeps_failures_and_unreviewed_out_of_quality_denominators():
    base = dict(reference_raw="come funziona", reference_final="Come funziona?", reference_status="provided",
                is_silence=False, duration_seconds=3, warm=True, stt_ms=100, llm_ms=50, total_ms=150,
                raw_text="come funziona", final_text="Come funziona?", warnings=[], status="completed")
    report = dict(phase="pipeline", clips=[dict(base, id="a", **{"pass": 1}),
        dict(base, id="a", **{"pass": 2}),
        dict(base, id="b", reference_status="unreviewed", **{"pass": 1}),
        dict(base, id="c", warnings=["llm_timeout"], final_text="come funziona", **{"pass": 1})])
    summary = summarize_report(report)
    assert summary["unique_clips"] == 3
    assert summary["unreviewed_rows"] == 1
    assert summary["fallback_rows"] == 1
    assert summary["successful_quality_rows"] == 2
    assert summary["final"]["wer"] == 0
    assert summary["final_punctuation"]["missed_questions"] == 0
    assert summary["latency_by_bucket"]["0-10"]["p95_ms"] == 150


def test_raw_only_asr_has_no_final_quality_score():
    summary = summarize_report(dict(phase="asr", clips=[dict(id="raw", reference_raw="ciao",
        reference_final="", reference_status="raw_only", raw_text="ciao", final_text=None,
        status="completed", warnings=[], total_ms=10, duration_seconds=2)]))
    assert summary["raw"]["wer"] == 0
    assert summary["final"] is None
