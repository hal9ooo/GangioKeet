import hashlib
import json
import subprocess
import sys
import textwrap
import time
import wave
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS))

import stt_eval  # noqa: E402
import stt_eval_adapters  # noqa: E402
import stt_eval_metrics  # noqa: E402
from stt_eval_adapters import (  # noqa: E402
    AdapterUnavailable,
    UnknownCandidate,
    create_adapter,
)
from stt_eval_metrics import (  # noqa: E402
    aggregate_scores,
    normalize_for_wer,
    score_transcripts,
)
from stt_eval import (  # noqa: E402
    ManifestError,
    load_manifest,
    main,
    run_candidate,
)

EXAMPLE_MANIFEST = (
    ROOT / "tests" / "fixtures" / "stt_eval" / "manifest.example.jsonl"
)

CANDIDATE_IDS = {
    "parakeet_baseline",
    "qwen3_asr_1_7b_8bit",
    "cohere_transcribe_03_2026",
    "nemotron_3_5_asr_streaming_0_6b",
    "whisper_large_v3_turbo",
}


# ---------------------------------------------------------------------------
# Metrics
# ---------------------------------------------------------------------------


def test_normalize_apostrophes_case_and_punctuation():
    assert normalize_for_wer("L'API, è OK!") == "l'api è ok"
    assert normalize_for_wer("L’API") == "l'api"
    assert normalize_for_wer("  Ciao   MONDO.  ") == "ciao mondo"
    assert normalize_for_wer("Fast-API / WebSocket") == "fast api websocket"
    assert normalize_for_wer("ＦａｓｔＡＰＩ") == "fastapi"


def test_score_single_substitution_over_three_words():
    result = score_transcripts("uno due tre", "uno due quattro")

    assert result["substitutions"] == 1
    assert result["deletions"] == 0
    assert result["insertions"] == 0
    assert result["reference_words"] == 3
    assert result["wer"] == pytest.approx(1 / 3)
    assert result["false_positive"] is False


def test_score_insertions_and_deletions():
    inserted = score_transcripts("uno due", "uno due tre")
    assert inserted["insertions"] == 1
    assert inserted["reference_words"] == 2
    assert inserted["wer"] == pytest.approx(0.5)

    deleted = score_transcripts("uno due tre", "uno")
    assert deleted["deletions"] == 2
    assert deleted["wer"] == pytest.approx(2 / 3)


def test_empty_reference_is_false_positive_not_invented_wer():
    noisy = score_transcripts("", "grazie")
    assert noisy["wer"] is None
    assert noisy["false_positive"] is True
    assert noisy["reference_words"] == 0

    silent = score_transcripts("", "")
    assert silent["wer"] is None
    assert silent["false_positive"] is False


def test_aggregate_is_weighted_not_naive_average_and_keeps_silence_apart():
    per_clip = [
        {
            "is_silence": False,
            "score": score_transcripts("a b c", "a b x"),
        },
        {
            "is_silence": False,
            "score": score_transcripts(
                "a b c d e f g h i", "a b c d e f g h i"
            ),
        },
        {
            "is_silence": True,
            "score": score_transcripts("", "grazie"),
        },
    ]

    aggregate = aggregate_scores(per_clip)

    # Weighted: one error over twelve reference words, not (1/3 + 0) / 2.
    assert aggregate["wer"] == pytest.approx(1 / 12)
    assert aggregate["wer"] != pytest.approx(1 / 6)
    assert aggregate["reference_words"] == 12
    assert aggregate["silence_clips"] == 1
    assert aggregate["silence_false_positives"] == 1
    assert aggregate["silence_false_positive_rate"] == pytest.approx(1.0)


# ---------------------------------------------------------------------------
# Manifest validation
# ---------------------------------------------------------------------------


def _entry(**overrides):
    entry = {
        "id": "clip-1",
        "audio_path": "audio/clip-1.wav",
        "reference_raw": "uno due tre",
        "reference_final": "uno due tre corretto",
        "category": "it_general",
        "profile": "code",
        "technical_terms": [],
        "is_silence": False,
    }
    entry.update(overrides)
    return entry


def _write_wav(
    path: Path,
    *,
    frames: int = 1600,
    channels: int = 1,
    rate: int = 16000,
    sampwidth: int = 2,
) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(sampwidth)
        handle.setframerate(rate)
        handle.writeframes(b"\x00" * frames * channels * sampwidth)
    return path


def _write_manifest(path: Path, entries) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        "".join(json.dumps(entry) + "\n" for entry in entries),
        encoding="utf-8",
    )
    return path


def _corpus(tmp_path: Path):
    base = tmp_path / "corpus"
    _write_wav(base / "audio" / "clip-1.wav")
    _write_wav(base / "audio" / "clip-2.wav", frames=3200)
    _write_wav(base / "audio" / "clip-3.wav", frames=4800)
    entries = [
        _entry(),
        _entry(
            id="clip-2",
            audio_path="audio/clip-2.wav",
            reference_raw="ehm configura fast a pi",
            reference_final="Configura FastAPI",
            category="it_technical",
            technical_terms=["FastAPI"],
        ),
        _entry(
            id="clip-3",
            audio_path="audio/clip-3.wav",
            reference_raw="",
            reference_final="",
            category="silence_noise",
            is_silence=True,
        ),
    ]
    return base, _write_manifest(base / "manifest.jsonl", entries)


def test_manifest_resolves_paths_relative_to_manifest(tmp_path):
    base, manifest = _corpus(tmp_path)

    clips = load_manifest(manifest)

    assert clips[0]["audio_path"] == (base / "audio" / "clip-1.wav").resolve()
    assert clips[2]["is_silence"] is True


def test_manifest_rejects_missing_audio(tmp_path):
    base = tmp_path / "corpus"
    manifest = _write_manifest(
        base / "manifest.jsonl",
        [_entry(audio_path="audio/missing.wav")],
    )

    with pytest.raises(ManifestError, match="audio"):
        load_manifest(manifest)


def test_manifest_rejects_duplicate_ids(tmp_path):
    base, manifest = _corpus(tmp_path)
    entries = [
        _entry(),
        _entry(audio_path="audio/clip-2.wav"),
    ]
    _write_manifest(manifest, entries)

    with pytest.raises(ManifestError, match="duplicate"):
        load_manifest(manifest)


def test_manifest_rejects_escaping_path(tmp_path):
    base = tmp_path / "corpus"
    outside = _write_wav(tmp_path / "outside.wav")
    manifest = _write_manifest(
        base / "manifest.jsonl",
        [_entry(audio_path=f"../{outside.name}")],
    )

    with pytest.raises(ManifestError, match="escap"):
        load_manifest(manifest)


def test_manifest_rejects_absolute_path(tmp_path):
    base = tmp_path / "corpus"
    absolute = _write_wav(tmp_path / "absolute.wav")
    manifest = _write_manifest(
        base / "manifest.jsonl",
        [_entry(audio_path=str(absolute))],
    )

    with pytest.raises(ManifestError, match="relative"):
        load_manifest(manifest)


def test_manifest_rejects_unknown_category(tmp_path):
    base = tmp_path / "corpus"
    manifest = _write_manifest(
        base / "manifest.jsonl",
        [_entry(category="made_up")],
    )

    with pytest.raises(ManifestError, match="category"):
        load_manifest(manifest)


def test_manifest_rejects_missing_field(tmp_path):
    base = tmp_path / "corpus"
    entry = _entry()
    del entry["technical_terms"]
    manifest = _write_manifest(base / "manifest.jsonl", [entry])

    with pytest.raises(ManifestError, match="technical_terms"):
        load_manifest(manifest)


def test_manifest_rejects_malformed_json(tmp_path):
    manifest = tmp_path / "corpus" / "manifest.jsonl"
    manifest.parent.mkdir(parents=True)
    manifest.write_text("{not valid json\n", encoding="utf-8")

    with pytest.raises(ManifestError, match="JSON"):
        load_manifest(manifest)


@pytest.mark.parametrize(
    ("wav_kwargs", "message"),
    [
        ({"sampwidth": 1}, "PCM16"),
        ({"channels": 2}, "mono"),
        ({"rate": 8000}, "16000"),
    ],
)
def test_manifest_rejects_non_pcm16_mono_16k_wav(
    tmp_path, wav_kwargs, message
):
    base = tmp_path / "corpus"
    _write_wav(base / "audio" / "clip-1.wav", **wav_kwargs)
    manifest = _write_manifest(base / "manifest.jsonl", [_entry()])

    with pytest.raises(ManifestError, match=message):
        load_manifest(manifest)


def test_schema_example_parses_with_audio_validation_bypassed():
    clips = load_manifest(EXAMPLE_MANIFEST, require_audio=False)

    assert len(clips) >= 1
    assert {clip["category"] for clip in clips} <= set(
        stt_eval.ALLOWED_CATEGORIES
    )
    assert all(clip["technical_terms"] == [] for clip in clips)


# ---------------------------------------------------------------------------
# Runner with a fake adapter
# ---------------------------------------------------------------------------


class FakeAdapter:
    candidate_id = "fake"
    model_id = "fake-model"
    revision = "rev-1"
    precision = "int8"
    status = "test"

    OUTPUTS = {
        3200: "uno due quattro",
        6400: "ehm configura fast a pi",
        9600: "grazie",
    }
    FINAL_OUTPUTS = {
        "uno due quattro": "uno due tre",
        "ehm configura fast a pi": "configura fastapi",
        "grazie": "",
    }

    def __init__(self, *, with_finalizer=True):
        self.with_finalizer = with_finalizer
        self.loads = 0
        self.calls = []

    def load(self):
        self.loads += 1

    def transcribe(self, pcm):
        self.calls.append(len(pcm))
        return self.OUTPUTS[len(pcm)]

    def finalize(self, text, profile):
        return self.FINAL_OUTPUTS.get(text, text)


class SleepyAdapter(FakeAdapter):
    """Adapter with deterministic sleeps in load and every transcription."""

    delay = 0.02
    load_delay = 0.03

    def load(self):
        time.sleep(self.load_delay)
        super().load()

    def transcribe(self, pcm):
        time.sleep(self.delay)
        return super().transcribe(pcm)


class NoFinalizerAdapter(FakeAdapter):
    def __init__(self):
        super().__init__(with_finalizer=False)
        # The runner must detect the absence of an optional finalizer.
        self.finalize = None


class DriftingAdapter(FakeAdapter):
    def __init__(self):
        super().__init__()
        self._counts = {}

    def transcribe(self, pcm):
        key = len(pcm)
        seen = self._counts.get(key, 0)
        self._counts[key] = seen + 1
        self.calls.append(key)
        if seen == 0:
            return self.OUTPUTS[key]
        return self.OUTPUTS[key] + " rumore"


def _run_fake(tmp_path, adapter, *, warmups=2, repeats=3, name="report.json"):
    base, manifest = _corpus(tmp_path)
    output = tmp_path / name

    def factory(candidate_id):
        assert candidate_id == "fake"
        return adapter

    result = run_candidate(
        "fake",
        manifest,
        output,
        warmups,
        repeats,
        adapter_factory=factory,
    )
    return result, output, adapter


def test_runner_measures_only_repeats_and_separates_raw_final(tmp_path):
    result, output, adapter = _run_fake(tmp_path, FakeAdapter())

    # Three clips x (2 warmups + 3 measured) calls, but only measured samples
    # appear in the report.
    assert len(adapter.calls) == 3 * (2 + 3)
    assert output.is_file()
    written = json.loads(output.read_text(encoding="utf-8"))
    assert written["candidate"]["id"] == "fake"
    assert written["warmups"] == 2
    assert written["repeats"] == 3

    clips = {clip["id"]: clip for clip in result["clips"]}
    first = clips["clip-1"]
    assert first["raw"]["hypothesis"] == "uno due quattro"
    assert first["raw"]["hypotheses"] == ["uno due quattro"] * 3
    assert first["raw"]["score"]["wer"] == pytest.approx(1 / 3)
    # Final text is scored only against reference_final, not reference_raw.
    assert first["final"]["hypothesis"] == "uno due tre"
    assert first["final"]["score"]["wer"] == pytest.approx(1 / 4)
    assert len(first["latency_ms"]) == 3
    assert first["latency"]["count"] == 3
    assert first["deterministic"] is True
    assert first["text_changed"] is False

    second = clips["clip-2"]
    assert second["raw"]["score"]["wer"] == pytest.approx(0.0)
    assert second["final"]["score"]["wer"] == pytest.approx(0.0)

    silence = clips["clip-3"]
    assert silence["is_silence"] is True
    assert silence["raw"]["score"]["wer"] is None
    assert silence["raw"]["score"]["false_positive"] is True
    assert silence["final"]["score"]["false_positive"] is False

    raw_aggregate = result["aggregate"]["raw"]
    assert raw_aggregate["wer"] == pytest.approx(1 / 8)
    assert raw_aggregate["reference_words"] == 8
    assert raw_aggregate["silence_clips"] == 1
    assert raw_aggregate["silence_false_positives"] == 1
    assert raw_aggregate["silence_false_positive_rate"] == pytest.approx(1.0)

    final_aggregate = result["aggregate"]["final"]
    assert final_aggregate["wer"] == pytest.approx(1 / 6)
    assert final_aggregate["silence_false_positives"] == 0

    assert result["load_seconds"] >= 0.0
    assert result["peak_rss_mb"] > 0.0
    assert result["clips"][0]["load_seconds"] == pytest.approx(
        result["load_seconds"]
    )
    assert "environment" in result


def test_runner_does_not_invent_final_metrics_without_finalizer(tmp_path):
    result, _, _ = _run_fake(
        tmp_path, NoFinalizerAdapter(), warmups=0, repeats=1
    )

    assert all(clip["final"] is None for clip in result["clips"])
    assert result["aggregate"]["final"] is None
    assert result["candidate"]["has_finalizer"] is False


def test_runner_reports_non_deterministic_text(tmp_path):
    result, _, _ = _run_fake(
        tmp_path, DriftingAdapter(), warmups=0, repeats=2
    )

    first = result["clips"][0]
    assert first["deterministic"] is False
    assert first["text_changed"] is True
    assert first["raw"]["hypothesis"] == first["raw"]["hypotheses"][0]
    assert first["raw"]["hypotheses"] == [
        "uno due quattro",
        "uno due quattro rumore",
    ]


def test_runner_adds_pooled_latency_rtf_and_nondeterministic(tmp_path):
    result, _, _ = _run_fake(
        tmp_path, SleepyAdapter(), warmups=0, repeats=1
    )

    assert result["pooled_latency"]["count"] == 3
    assert result["rtf_summary"]["count"] == 3
    for clip in result["clips"]:
        assert clip["rtf"] > 0
        assert clip["rtf"] == pytest.approx(
            (sum(clip["latency_ms"]) / len(clip["latency_ms"]) / 1000)
            / clip["duration_seconds"],
            abs=1e-5,
        )

    drifting, _, _ = _run_fake(
        tmp_path,
        DriftingAdapter(),
        warmups=0,
        repeats=2,
        name="drift.json",
    )
    assert drifting["clips"][0]["nondeterministic"] is True
    assert drifting["clips"][0]["text_changed"] is True
    assert len(drifting["nondeterministic_clips"]) == 3


def test_runner_measured_seconds_excludes_warmups_loading_and_io(tmp_path):
    result, _, _ = _run_fake(
        tmp_path, SleepyAdapter(), warmups=2, repeats=1
    )

    measured = result["measured_seconds"]
    measured_from_samples = (
        sum(sum(clip["latency_ms"]) for clip in result["clips"]) / 1000
    )

    assert measured == pytest.approx(measured_from_samples, abs=0.003)
    # Three measured calls at 20 ms each; two warm-up calls per clip and the
    # 30 ms model load must not be counted.
    assert measured == pytest.approx(0.06, abs=0.02)
    assert measured < 0.12


def test_runner_records_provenance_digests_and_versions(tmp_path):
    result, output, _ = _run_fake(tmp_path, FakeAdapter())

    provenance = result["provenance"]
    manifest = Path(result["manifest"])
    manifest_digest = hashlib.sha256(manifest.read_bytes()).hexdigest()

    assert provenance["manifest_sha256"] == manifest_digest
    repo_commit = provenance["repo_commit"]
    assert repo_commit == "unknown" or (
        len(repo_commit) == 40
        and all(character in "0123456789abcdef" for character in repo_commit)
    )
    assert provenance["harness_revision"] == stt_eval.HARNESS_REVISION
    assert (
        provenance["normalizer_revision"]
        == stt_eval_metrics.NORMALIZER_REVISION
    )
    assert provenance["mlx_audio_version"] not in (None, "", "unknown")
    assert "mlx-audio" in result["environment"]["packages"]

    for clip in result["clips"]:
        audio_digest = hashlib.sha256(
            Path(clip["audio_path"]).read_bytes()
        ).hexdigest()
        assert clip["audio_sha256"] == audio_digest
        assert provenance["audio_sha256"][clip["id"]] == audio_digest

    written = json.loads(output.read_text(encoding="utf-8"))
    assert written["provenance"] == provenance


def test_runner_output_is_reproducible(tmp_path):
    first, _, _ = _run_fake(tmp_path, FakeAdapter(), name="first.json")
    second, _, _ = _run_fake(tmp_path, FakeAdapter(), name="second.json")

    def semantic(result):
        return {
            "candidate": result["candidate"],
            "warmups": result["warmups"],
            "repeats": result["repeats"],
            "aggregate": result["aggregate"],
            "clips": [
                {
                    "id": clip["id"],
                    "raw": clip["raw"],
                    "final": clip["final"],
                    "is_silence": clip["is_silence"],
                }
                for clip in result["clips"]
            ],
        }

    assert semantic(first) == semantic(second)


# ---------------------------------------------------------------------------
# Adapters
# ---------------------------------------------------------------------------


def test_registry_contains_required_candidates():
    assert set(stt_eval_adapters.CANDIDATE_IDS) == CANDIDATE_IDS


def test_unknown_candidate_is_rejected_with_clear_list():
    with pytest.raises(UnknownCandidate) as error:
        create_adapter("not-a-candidate")

    message = str(error.value)
    for candidate_id in CANDIDATE_IDS:
        assert candidate_id in message


def test_unverified_candidate_reports_missing_runtime(monkeypatch):
    def missing_runtime(name):
        raise ModuleNotFoundError(name)

    monkeypatch.setattr(
        stt_eval_adapters.importlib, "import_module", missing_runtime
    )
    adapter = create_adapter("qwen3_asr_1_7b_8bit")

    with pytest.raises(
        AdapterUnavailable, match="runtime 'mlx_audio' unavailable"
    ):
        adapter.load()


def test_unverified_candidate_reports_not_yet_verified(monkeypatch):
    monkeypatch.setattr(
        stt_eval_adapters.importlib,
        "import_module",
        lambda name: object(),
    )
    adapter = create_adapter("qwen3_asr_1_7b_8bit")

    with pytest.raises(AdapterUnavailable, match="not yet verified"):
        adapter.load()


def test_eval_modules_do_not_import_optional_runtimes_by_default():
    code = textwrap.dedent(
        """
        import sys
        sys.path.insert(0, "scripts")
        import stt_eval
        import stt_eval_adapters
        import stt_eval_metrics

        assert "mlx" not in sys.modules, "mlx imported eagerly"
        assert "mlx_audio" not in sys.modules, "mlx_audio imported eagerly"
        assert "parakeet_mlx" not in sys.modules, (
            "parakeet_mlx imported eagerly"
        )
        print(",".join(sorted(stt_eval_adapters.CANDIDATE_IDS)))
        """
    )

    completed = subprocess.run(
        [sys.executable, "-c", code],
        cwd=ROOT,
        capture_output=True,
        text=True,
    )

    assert completed.returncode == 0, completed.stderr
    assert set(completed.stdout.strip().split(",")) == CANDIDATE_IDS


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def test_cli_rejects_missing_manifest(tmp_path, capsys):
    output = tmp_path / "report.json"

    code = main(
        [
            "--manifest",
            str(tmp_path / "missing.jsonl"),
            "--candidate",
            "parakeet_baseline",
            "--warmups",
            "0",
            "--repeats",
            "1",
            "--output",
            str(output),
        ]
    )

    captured = capsys.readouterr()
    assert code != 0
    assert "manifest" in captured.err.lower()
    assert not output.exists()


def test_cli_rejects_unknown_candidate(tmp_path, capsys):
    _, manifest = _corpus(tmp_path)
    output = tmp_path / "report.json"

    code = main(
        [
            "--manifest",
            str(manifest),
            "--candidate",
            "not-a-candidate",
            "--warmups",
            "0",
            "--repeats",
            "1",
            "--output",
            str(output),
        ]
    )

    captured = capsys.readouterr()
    assert code != 0
    assert "unknown candidate" in captured.err.lower()
    for candidate_id in CANDIDATE_IDS:
        assert candidate_id in captured.err
    assert not output.exists()


def test_cli_rejects_unavailable_runtime(tmp_path, capsys, monkeypatch):
    base, manifest = _corpus(tmp_path)
    output = tmp_path / "report.json"

    def missing_runtime(name):
        raise ModuleNotFoundError(name)

    monkeypatch.setattr(stt_eval_adapters.importlib, "import_module",
                        missing_runtime)

    code = main(
        [
            "--manifest",
            str(manifest),
            "--candidate",
            "qwen3_asr_1_7b_8bit",
            "--warmups",
            "0",
            "--repeats",
            "1",
            "--output",
            str(output),
        ]
    )

    captured = capsys.readouterr()
    assert code != 0
    assert "runtime 'mlx_audio' unavailable" in captured.err
    assert not output.exists()


def test_cli_help_works(capsys):
    with pytest.raises(SystemExit) as exit_info:
        main(["--help"])

    assert exit_info.value.code == 0
    assert "usage" in capsys.readouterr().out.lower()
