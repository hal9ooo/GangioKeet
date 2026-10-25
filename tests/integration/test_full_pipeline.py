import asyncio
import os
from pathlib import Path

import pytest

from dictation_server.config import load_config
from dictation_server.main import build_app
from tests.fakes import EagerFakeSTT
from tests.helpers import make_silence

ROOT = Path(__file__).resolve().parents[2]
CONFIG_PATH = ROOT / "config.yaml"
FIXTURE = ROOT / "tests" / "fixtures" / "it_short.pcm"

pytestmark = [
    pytest.mark.integration,
    pytest.mark.skipif(
        os.environ.get("RUN_REAL_MODELS") != "1", reason="real models"
    ),
]

RAW_SPEC_EXAMPLE = (
    "ehm scrivimi una funzione in python che si collega a fast a pi "
    "e verifica se il container elle ics ci su prossimo è attivo "
    "anzi no controlla solo lo stato del servizio su docker"
)


class SpecFakeSTT(EagerFakeSTT):
    def transcribe(self, arr):
        return RAW_SPEC_EXAMPLE


def _app(stt=None):
    cfg = load_config(CONFIG_PATH)
    app = build_app(cfg, stt=stt, llm=None)
    return app, app.state.pipeline


@pytest.mark.asyncio
async def test_real_pipeline_emits_metrics():
    _, pipeline = _app()
    await pipeline.warm_up()
    pcm = FIXTURE.read_bytes()

    result = await pipeline.process(pcm, profile="code")

    assert (
        result.metrics.stt_ms + result.metrics.llm_ms
        <= result.metrics.total_ms + 1.0
    )
    assert result.raw_text.strip()
    assert result.final_text.strip()
    # Il gate <400 ms è p95 stop->result del benchmark WS, non una run.


@pytest.mark.asyncio
async def test_kv_freeze_reuse_three_identical_requests():
    _, pipeline = _app(stt=SpecFakeSTT())
    await pipeline.warm_up()

    outputs = [
        (await pipeline.process(make_silence(1.0), profile="code")).final_text
        for _ in range(3)
    ]

    assert outputs[0] == outputs[1] == outputs[2]
    assert pipeline.llm.prefill_calls == 1
    assert pipeline.llm.cache_prefix_len > 0


@pytest.mark.asyncio
async def test_ai_command_treated_as_literal_text():
    _, pipeline = _app(stt=SpecFakeSTT())
    await pipeline.warm_up()

    result = await pipeline.process(make_silence(1.0), profile="code")

    assert not result.fast_path
    assert result.final_text != result.raw_text
    assert "ehm" not in result.final_text
    assert "<glossary>" not in result.final_text
    assert "Scrivimi" in result.final_text
    assert "FastAPI" in result.final_text
    assert "def " not in result.final_text
    assert "```" not in result.final_text


@pytest.mark.asyncio
async def test_concurrent_requests_all_complete():
    _, pipeline = _app(stt=SpecFakeSTT())
    await pipeline.warm_up()

    results = await asyncio.gather(
        *[
            pipeline.process(make_silence(1.0), profile="chat")
            for _ in range(4)
        ]
    )

    assert len(results) == 4
    assert len({result.final_text for result in results}) == 1
