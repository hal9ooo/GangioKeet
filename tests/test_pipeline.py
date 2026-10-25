import asyncio
from pathlib import Path

import pytest

from dictation_server.config import load_config
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
