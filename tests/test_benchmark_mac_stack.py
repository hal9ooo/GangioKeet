import pytest

from scripts import benchmark_mac_stack


def test_summarize_nearest_rank():
    summary = benchmark_mac_stack.summarize([1.0, 2.0, 3.0, 100.0])
    assert summary["count"] == 4
    assert summary["p50"] == 2.5
    assert summary["p95"] == 100.0
    assert summary["max"] == 100.0


def test_parse_args_accepts_pair():
    args = benchmark_mac_stack.parse_args(
        [
            "--candidates",
            "models/mac-candidates.json",
            "--pair",
            "primary",
            "--audio",
            "tests/fixtures/it_short.pcm",
            "--output",
            "report.json",
        ]
    )
    assert args.pair == "primary"
    assert args.candidates == "models/mac-candidates.json"


def test_parse_args_rejects_unknown_pair():
    with pytest.raises(SystemExit):
        benchmark_mac_stack.parse_args(
            [
                "--candidates",
                "models/mac-candidates.json",
                "--pair",
                "unknown",
                "--audio",
                "a.pcm",
                "--output",
                "report.json",
            ]
        )


def test_build_report_contains_required_fields():
    report = benchmark_mac_stack.build_report(
        pair_id="primary",
        load_ms=12.5,
        samples=[
            {"duration_s": 4.0, "stt_ms": 10.0, "llm_ms": 20.0, "total_ms": 30.0}
        ],
        metal_peak_bytes=1,
        working_set_bytes=2,
        swap_before=3,
        swap_after=4,
    )
    assert report["pair"] == "primary"
    assert report["load_ms"] == 12.5
    assert report["metal_peak_bytes"] == 1
    assert report["swap_before"] == 3
    assert report["swap_after"] == 4
    assert report["samples"]
    assert report["summary"]["total_ms"]["count"] == 1


def test_repeat_pcm_reaches_requested_duration():
    pcm = b"\x00\x00" * 16000  # one second of PCM16 mono 16 kHz
    assert len(benchmark_mac_stack.repeat_pcm(pcm, 2.5)) == 80000
