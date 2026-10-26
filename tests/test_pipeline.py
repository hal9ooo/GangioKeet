import asyncio
import logging
import time
from pathlib import Path

import pytest

from dictation_server.config import load_config
from dictation_server.llm import LLMDeadlineExceeded
from dictation_server.pipeline import Pipeline
from tests.fakes import (
    ClearCounter,
    FakeLLM,
    FakeSTT,
    SlowFakeLLM,
    Trace,
    TracedFakeLLM,
    TracedFakeSTT,
)
from tests.helpers import make_silence

CONFIG_PATH = Path(__file__).resolve().parents[1] / "config.yaml"


def _pipeline(stt=None, llm=None, clear=None):
    return Pipeline(
        stt or FakeSTT(),
        llm or FakeLLM(),
        load_config(CONFIG_PATH),
        clear,
    )


class ScriptedSTT(FakeSTT):
    def __init__(self, text):
        super().__init__()
        self.text = text

    def transcribe(self, audio):
        self.seen.append(audio)
        return self.text


class ExplodingLLM(FakeLLM):
    def clean(self, raw_text, profile, *, deadline=None):
        self.calls.append((raw_text, profile))
        time.sleep(0.02)
        raise RuntimeError("qwen exploded")


@pytest.mark.asyncio
async def test_fast_path_skips_llm():
    stt, llm = FakeSTT(), FakeLLM()
    p = Pipeline(stt, llm, load_config(CONFIG_PATH))
    r = await p.process(make_silence(0.5), profile="chat", bypass_llm=True)
    assert r.fast_path is True
    assert r.final_text == r.raw_text
    assert llm.calls == []
    assert r.metrics.stt_ms >= 0
    assert r.metrics.llm_ms == 0


@pytest.mark.asyncio
async def test_automatic_fast_path_skips_llm():
    class ShortSTT(FakeSTT):
        def transcribe(self, audio):
            self.seen.append(audio)
            return "ciao come stai"

    llm = FakeLLM()
    p = Pipeline(ShortSTT(), llm, load_config(CONFIG_PATH))
    r = await p.process(make_silence(0.5))
    assert r.fast_path is True
    assert r.final_text == "ciao come stai"
    assert llm.calls == []


@pytest.mark.asyncio
async def test_override_forces_llm_even_for_short_text():
    llm = FakeLLM()
    p = Pipeline(FakeSTT(), llm, load_config(CONFIG_PATH))
    r = await p.process(make_silence(0.5), profile="code")
    assert r.raw_text == "ehm ciao fast a pi"
    assert r.fast_path is False
    assert r.final_text == "[code] ehm ciao fast a pi"
    assert llm.calls == [("ehm ciao fast a pi", "code")]


@pytest.mark.asyncio
async def test_metal_lock_serializes_engines():
    events = []
    stt, llm = TracedFakeSTT(events), TracedFakeLLM(events)
    p = Pipeline(stt, llm, load_config(CONFIG_PATH))
    await asyncio.gather(
        p.process(make_silence(0.5)),
        p.process(make_silence(0.5)),
    )
    assert Trace.no_overlap(events)
    assert len(stt.seen) == 2
    assert len(llm.calls) == 2


@pytest.mark.asyncio
async def test_warm_up_touches_both_engines():
    stt, llm = FakeSTT(), FakeLLM()
    p = Pipeline(stt, llm, load_config(CONFIG_PATH))
    await p.warm_up()
    assert len(stt.seen) == 1
    assert stt.seen[0].shape == (16000,)
    assert len(llm.calls) >= 1


@pytest.mark.asyncio
async def test_clear_metal_runs_on_every_completion():
    counter = ClearCounter()
    p = Pipeline(FakeSTT(), FakeLLM(), load_config(CONFIG_PATH), counter)
    await p.process(make_silence(0.5))
    assert counter.n == 1


@pytest.mark.asyncio
async def test_empty_audio_returns_empty_result_without_stt_and_clears():
    stt, llm, counter = FakeSTT(), FakeLLM(), ClearCounter()
    p = Pipeline(stt, llm, load_config(CONFIG_PATH), counter)
    r = await p.process(b"", profile="chat")
    assert r.raw_text == ""
    assert r.final_text == ""
    assert r.fast_path is True
    assert r.metrics.stt_ms == 0
    assert r.metrics.llm_ms == 0
    assert stt.seen == []
    assert llm.calls == []
    assert counter.n == 1


@pytest.mark.asyncio
async def test_metrics_total_covers_stages():
    p = Pipeline(FakeSTT(), FakeLLM(), load_config(CONFIG_PATH))
    r = await p.process(make_silence(0.5), profile="code")
    assert r.metrics.total_ms >= r.metrics.stt_ms + r.metrics.llm_ms


@pytest.mark.asyncio
async def test_process_cancellation_releases_lock_and_clears_metal_cache():
    counter = ClearCounter()
    p = Pipeline(
        FakeSTT(),
        SlowFakeLLM(delay=0.3),
        load_config(CONFIG_PATH),
        counter,
    )
    t = asyncio.create_task(p.process(make_silence(0.5), profile="code"))
    await asyncio.sleep(0.05)
    t.cancel()
    with pytest.raises(asyncio.CancelledError):
        await t
    assert counter.n >= 1                       # clear dopo fine reale del worker
    assert p.llm.finished is True                # il thread non prosegue dopo unlock
    r = await p.process(make_silence(0.5), bypass_llm=True)
    assert r.fast_path is True                  # nessun worker orfano sul lock


@pytest.mark.asyncio
async def test_cancellation_logs_worker_failure(caplog):
    class SlowFailingLLM(FakeLLM):
        def clean(self, raw_text, profile, *, deadline=None):
            self.calls.append((raw_text, profile))
            time.sleep(0.1)
            raise RuntimeError("llm exploded")

    counter = ClearCounter()
    p = Pipeline(
        FakeSTT(), SlowFailingLLM(), load_config(CONFIG_PATH), counter
    )
    with caplog.at_level(logging.WARNING, logger="dictation_server.pipeline"):
        t = asyncio.create_task(p.process(make_silence(0.5)))
        await asyncio.sleep(0.02)
        t.cancel()
        with pytest.raises(asyncio.CancelledError):
            await t

    assert "llm exploded" in caplog.text
    assert counter.n == 1


@pytest.mark.asyncio
async def test_engine_error_still_clears_and_releases_lock():
    class BrokenOnceSTT(FakeSTT):
        def __init__(self):
            super().__init__()
            self.fail = True

        def transcribe(self, audio):
            if self.fail:
                self.fail = False
                raise RuntimeError("stt broke")
            return super().transcribe(audio)

    counter = ClearCounter()
    p = Pipeline(BrokenOnceSTT(), FakeLLM(), load_config(CONFIG_PATH), counter)
    with pytest.raises(RuntimeError, match="stt broke"):
        await p.process(make_silence(0.5))
    assert counter.n == 1
    r = await p.process(make_silence(0.5), bypass_llm=True)
    assert r.fast_path is True


@pytest.mark.asyncio
async def test_pcm_conversion_runs_inside_the_metal_lock(monkeypatch):
    import dictation_server.pipeline as pipeline_module

    calls = []

    def recording_conversion(pcm):
        calls.append(len(pcm))
        return object()

    monkeypatch.setattr(
        pipeline_module, "pcm_bytes_to_mlx_array", recording_conversion
    )
    counter = ClearCounter()
    stt = FakeSTT()
    p = Pipeline(stt, FakeLLM(), load_config(CONFIG_PATH), counter)

    await p._metal.acquire()
    task = asyncio.create_task(p.process(make_silence(0.1), bypass_llm=True))
    try:
        await asyncio.sleep(0.05)
        assert calls == []
        assert stt.seen == []
    finally:
        p._metal.release()

    result = await task
    assert calls == [3200]
    assert len(stt.seen) == 1
    assert result.fast_path is True
    assert counter.n == 1


@pytest.mark.asyncio
async def test_llm_failure_falls_back_to_normalized_raw():
    llm = ExplodingLLM()
    p = Pipeline(
        ScriptedSTT("fast a pi su docker"), llm, load_config(CONFIG_PATH)
    )

    r = await p.process(make_silence(0.5), profile="code")

    assert r.raw_text == "fast a pi su docker"
    assert r.final_text == "fast a pi su Docker"
    assert r.fast_path is False
    assert r.warnings == ["llm_failed"]
    assert len(llm.calls) == 1
    assert r.metrics.llm_ms > 0


@pytest.mark.asyncio
async def test_llm_deadline_falls_back_to_normalized_raw():
    class DeadlineLLM(FakeLLM):
        def clean(self, raw_text, profile, *, deadline=None):
            self.calls.append((raw_text, profile))
            raise LLMDeadlineExceeded("deadline exceeded")

    llm = DeadlineLLM()
    p = Pipeline(
        ScriptedSTT("fast a pi su docker"), llm, load_config(CONFIG_PATH)
    )

    r = await p.process(make_silence(0.5), profile="code")

    assert r.raw_text == "fast a pi su docker"
    assert r.final_text == "fast a pi su Docker"
    assert r.fast_path is False
    assert r.warnings == ["llm_timeout"]
    assert len(llm.calls) == 1


@pytest.mark.asyncio
async def test_think_guardrail_failure_never_returns_think_or_regenerates():
    class ThinkingLLM(FakeLLM):
        def clean(self, raw_text, profile, *, deadline=None):
            self.calls.append((raw_text, profile))
            output = "<think>ragiono</think> fast a pi su docker"
            if "<think>" in output:
                raise RuntimeError("LLM output contains thinking tags")
            return output

    llm = ThinkingLLM()
    p = Pipeline(
        ScriptedSTT("fast a pi su docker"), llm, load_config(CONFIG_PATH)
    )

    r = await p.process(make_silence(0.5), profile="code")

    assert "<think>" not in r.final_text
    assert r.final_text == "fast a pi su Docker"
    assert r.warnings == ["llm_failed"]
    assert len(llm.calls) == 1


@pytest.mark.asyncio
async def test_stt_failure_still_raises_and_never_uses_fallback():
    class BrokenSTT(FakeSTT):
        def transcribe(self, audio):
            raise RuntimeError("stt broke")

    llm = FakeLLM()
    p = Pipeline(BrokenSTT(), llm, load_config(CONFIG_PATH))

    with pytest.raises(RuntimeError, match="stt broke"):
        await p.process(make_silence(0.5), profile="code")
    assert llm.calls == []


@pytest.mark.asyncio
async def test_cancelled_error_propagates_instead_of_falling_back():
    class CancelledLLM(FakeLLM):
        def clean(self, raw_text, profile, *, deadline=None):
            self.calls.append((raw_text, profile))
            raise asyncio.CancelledError()

    llm = CancelledLLM()
    p = Pipeline(FakeSTT(), llm, load_config(CONFIG_PATH))

    with pytest.raises(asyncio.CancelledError):
        await p.process(make_silence(0.5), profile="code")
    assert len(llm.calls) == 1


@pytest.mark.asyncio
async def test_llm_path_passes_absolute_monotonic_deadline():
    class DeadlineRecordingLLM(FakeLLM):
        def __init__(self):
            super().__init__()
            self.deadlines = []

        def clean(self, raw_text, profile, *, deadline=None):
            self.calls.append((raw_text, profile))
            self.deadlines.append(deadline)
            return "pulito"

    cfg = load_config(CONFIG_PATH)
    llm = DeadlineRecordingLLM()
    p = Pipeline(FakeSTT(), llm, cfg)

    before = time.monotonic()
    r = await p.process(make_silence(0.5), profile="code")
    after = time.monotonic()

    assert r.final_text == "pulito"
    assert r.fast_path is False
    assert r.warnings == []
    assert len(llm.deadlines) == 1
    deadline = llm.deadlines[0]
    assert deadline is not None
    assert before + cfg.timeouts.llm_seconds <= deadline
    assert deadline <= after + cfg.timeouts.llm_seconds


@pytest.mark.asyncio
async def test_short_canonical_term_uses_fast_path_with_canonical_casing():
    llm = FakeLLM()
    p = Pipeline(
        ScriptedSTT("usa docker ora"), llm, load_config(CONFIG_PATH)
    )

    r = await p.process(make_silence(0.5), profile="chat")

    assert r.raw_text == "usa docker ora"
    assert r.final_text == "usa Docker ora"
    assert r.fast_path is True
    assert r.metrics.llm_ms == 0
    assert r.warnings == []
    assert llm.calls == []


@pytest.mark.asyncio
async def test_bypass_llm_returns_exact_raw_without_normalization():
    llm = FakeLLM()
    text = "usa docker ora e poi domani"
    p = Pipeline(ScriptedSTT(text), llm, load_config(CONFIG_PATH))

    r = await p.process(
        make_silence(0.5), profile="chat", bypass_llm=True
    )

    assert r.raw_text == text
    assert r.final_text == text
    assert r.fast_path is True
    assert r.metrics.llm_ms == 0
    assert r.warnings == []
    assert llm.calls == []
