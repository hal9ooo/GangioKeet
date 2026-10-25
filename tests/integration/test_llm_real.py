import os
from pathlib import Path

import pytest

from dictation_server.config import load_config
from dictation_server.llm import LLMCleaner
from dictation_server.prompt import build_system_prompt

pytestmark = [
    pytest.mark.integration,
    pytest.mark.skipif(
        os.environ.get("RUN_REAL_MODELS") != "1", reason="real models"
    ),
]

CONFIG_PATH = Path(__file__).resolve().parents[2] / "config.yaml"


def test_deterministic_and_freeze_reuse():
    cfg = load_config(CONFIG_PATH)
    cleaner = LLMCleaner(cfg.server.llm_model)
    cleaner.load()
    cleaner.freeze(build_system_prompt(cfg.glossary))

    assert cleaner.frozen
    assert cleaner.cache_prefix_len > 0
    assert cleaner.cache_prefix_len == len(cleaner._sys_tokens)
    assert cleaner.prefill_calls == 1
    for entry in cleaner._frozen_cache:
        offset = getattr(entry, "offset", None)
        if offset is not None:
            assert offset == cleaner.cache_prefix_len

    first = cleaner.clean("ehm scrivimi una funzione in python", "code")
    second = cleaner.clean("ehm scrivimi una funzione in python", "code")

    assert first == second == first.strip()
    assert first
    assert "<think>" not in first and "</think>" not in first
    assert cleaner.prefill_calls == 1
    for entry in cleaner._frozen_cache:
        offset = getattr(entry, "offset", None)
        if offset is not None:
            assert offset == cleaner.cache_prefix_len
