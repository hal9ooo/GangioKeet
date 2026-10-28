import json
import sys
import time
import types
import wave
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS))

import stt_eval_cleaners  # noqa: E402
import stt_eval_finalizer  # noqa: E402
import stt_eval_provenance  # noqa: E402
from stt_eval import load_manifest  # noqa: E402


def _write_wav(path: Path) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(16000)
        handle.writeframes(b"\x00\x00" * 1600)
    return path


def _corpus(tmp_path: Path):
    base = tmp_path / "corpus"
    audio = _write_wav(base / "audio" / "clip-1.wav")
    manifest = base / "manifest.jsonl"
    manifest.write_text(
        json.dumps(
            {
                "id": "clip-1",
                "audio_path": "audio/clip-1.wav",
                "reference_raw": "uno due",
                "reference_final": "uno due",
                "category": "it_general",
                "profile": "code",
                "technical_terms": [],
                "is_silence": False,
            }
        )
        + "\n",
        encoding="utf-8",
    )
    raw = {
        "candidate": {"id": "fake_stt", "model_id": "fake-model"},
        "manifest": str(manifest),
        "clips": [
            {
                "id": "clip-1",
                "category": "it_general",
                "profile": "code",
                "is_silence": False,
                "duration_seconds": 0.1,
                "raw_hypothesis": "uno due",
                "raw": {
                    "hypothesis": "uno due",
                    "hypotheses": ["uno due", "uno due", "uno due"],
                    "score": {"wer": 0.0},
                },
            }
        ],
        "aggregate": {"raw": {"wer": 0.0}},
    }
    raw_path = tmp_path / "raw.json"
    raw_path.write_text(json.dumps(raw), encoding="utf-8")
    return manifest, raw_path, audio


class FakeFinalizer:
    def __init__(self, cleaner_id):
        self.cleaner_id = cleaner_id
        self.loaded = False
        self.closed = False
        self.calls = []
        self.warnings = []

    def load(self):
        self.loaded = True

    def finalize(self, raw_text, profile, *, force_cleaner=False):
        self.calls.append((raw_text, profile, force_cleaner))
        time.sleep(0.001)
        if force_cleaner:
            return raw_text + " extra"
        return raw_text

    def close(self):
        self.closed = True


def test_cleaner_registry_has_three_models():
    assert set(stt_eval_finalizer.CLEANER_IDS) == {
        "qwen3_1_7b_4bit",
        "qwen3_5_0_8b_4bit",
        "qwen2_5_1_5b_instruct_4bit",
    }


def test_run_cleaner_applies_production_and_forced(tmp_path):
    manifest, raw_path, _ = _corpus(tmp_path)
    finalizer = FakeFinalizer("fake")
    output = tmp_path / "cleaner.json"

    result = stt_eval_cleaners.run_cleaner(
        raw_path,
        manifest,
        "qwen3_1_7b_4bit",
        output,
        finalizer_factory=lambda cleaner_id: finalizer,
    )

    assert finalizer.loaded is True
    assert finalizer.closed is True
    assert len(finalizer.calls) == 6  # 3 hypotheses x 2 modes
    assert sum(call[2] is False for call in finalizer.calls) == 3
    assert sum(call[2] is True for call in finalizer.calls) == 3

    clip = result["clips"][0]
    assert clip["production"]["hypothesis"] == "uno due"
    assert clip["production"]["score"]["wer"] == 0.0
    assert clip["forced"]["hypothesis"] == "uno due extra"
    assert clip["forced"]["score"]["wer"] == pytest.approx(0.5)
    assert len(clip["production_latency_ms"]) == 3
    assert len(clip["forced_latency_ms"]) == 3
    assert result["cleaner"]["id"] == "qwen3_1_7b_4bit"
    assert result["aggregate"]["production"]["wer"] == 0.0
    assert result["peak_rss_mb"] > 0
    assert result["peak_metal_mb"] is not None
    assert result["measured_seconds"] > 0
    assert result["load_seconds"] >= 0
    assert output.is_file()


def test_run_cleaner_rejects_raw_manifest_mismatch(tmp_path):
    manifest, raw_path, _ = _corpus(tmp_path)
    raw = json.loads(raw_path.read_text(encoding="utf-8"))
    raw["clips"][0]["id"] = "missing"
    raw_path.write_text(json.dumps(raw), encoding="utf-8")

    with pytest.raises(ValueError, match="manifest"):
        stt_eval_cleaners.run_cleaner(
            raw_path,
            manifest,
            "qwen3_1_7b_4bit",
            tmp_path / "out.json",
            finalizer_factory=lambda cleaner_id: FakeFinalizer(cleaner_id),
        )


def test_run_cleaner_closes_finalizer_on_error(tmp_path):
    manifest, raw_path, _ = _corpus(tmp_path)
    finalizer = FakeFinalizer("fake")

    def broken(*args, **kwargs):
        raise RuntimeError("boom")

    finalizer.finalize = broken
    with pytest.raises(RuntimeError, match="boom"):
        stt_eval_cleaners.run_cleaner(
            raw_path,
            manifest,
            "qwen3_1_7b_4bit",
            tmp_path / "out.json",
            finalizer_factory=lambda cleaner_id: finalizer,
        )
    assert finalizer.closed is True


class _FakeLLM:
    instances = []
    clean_mode = "ok"

    def __init__(self, model_id, glossary=None):
        self.model_id = model_id
        self.glossary = glossary
        self.frozen = False
        self.cleaned = []
        _FakeLLM.instances.append(self)

    def load(self):
        pass

    def freeze(self, prompt):
        self.prompt = prompt
        self.frozen = True

    def clean(self, raw_text, profile, *, deadline=None):
        self.cleaned.append((raw_text, profile, deadline))
        if self.clean_mode == "timeout":
            from dictation_server.llm import LLMDeadlineExceeded

            raise LLMDeadlineExceeded("late")
        if self.clean_mode == "fail":
            raise RuntimeError("bad")
        return f"cleaned:{raw_text}"


class _FakePipeline:
    instances = []

    def __init__(self, stt, llm, cfg):
        self.stt = stt
        self.llm = llm
        self.cfg = cfg
        _FakePipeline.instances.append(self)

    async def process(self, pcm, profile="default", bypass_llm=False):
        assert self.stt.text
        return types.SimpleNamespace(
            final_text=f"pipeline:{self.stt.text}",
            warnings=[],
            fast_path=False,
        )


@pytest.fixture
def fake_cleaner_modules(monkeypatch):
    from dictation_server import llm as llm_module
    from dictation_server import pipeline as pipeline_module

    monkeypatch.setattr(llm_module, "LLMCleaner", _FakeLLM)
    monkeypatch.setattr(pipeline_module, "Pipeline", _FakePipeline)
    monkeypatch.setattr(
        stt_eval_provenance,
        "cached_snapshot",
        lambda model_id, revision: Path("/pinned-cleaner"),
    )
    _FakeLLM.instances = []
    _FakeLLM.clean_mode = "ok"
    _FakePipeline.instances = []
    yield


def test_pipeline_finalizer_production_and_forced(fake_cleaner_modules):
    finalizer = stt_eval_finalizer.PipelineFinalizer(
        "qwen3_1_7b_4bit", ROOT / "config.yaml"
    )
    finalizer.load()

    assert _FakeLLM.instances[0].frozen is True
    assert _FakePipeline.instances[0].stt.__class__.__name__ == "EchoSTT"

    production = finalizer.finalize("ciao", "code")
    forced = finalizer.finalize("ciao", "code", force_cleaner=True)

    assert production == "pipeline:ciao"
    assert forced == "cleaned:ciao"
    assert finalizer.fast_path is False
    finalizer.close()


def test_pipeline_finalizer_forced_timeout_falls_back(fake_cleaner_modules):
    _FakeLLM.clean_mode = "timeout"
    finalizer = stt_eval_finalizer.PipelineFinalizer(
        "qwen3_1_7b_4bit", ROOT / "config.yaml"
    )
    finalizer.load()

    output = finalizer.finalize("usa docker", "chat", force_cleaner=True)

    assert output == "usa Docker"
    assert finalizer.warnings == ["llm_timeout"]
    finalizer.close()


def test_unknown_cleaner_is_rejected():
    with pytest.raises(stt_eval_finalizer.UnknownCleaner):
        stt_eval_finalizer.PipelineFinalizer("does-not-exist", ROOT / "config.yaml")


def test_not_run_cleaner_reports_reason():
    finalizer = stt_eval_finalizer.PipelineFinalizer(
        "qwen3_5_0_8b_4bit", ROOT / "config.yaml"
    )
    with pytest.raises(
        stt_eval_finalizer.CleanerUnavailable, match="NOT RUN"
    ):
        finalizer.load()
