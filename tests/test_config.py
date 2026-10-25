from pathlib import Path

import pytest
from pydantic import ValidationError
from dictation_server.config import load_config


def test_load_full_config():
    cfg = load_config("config.yaml")
    assert cfg.server.port == 8000
    assert cfg.server.stt_model == "mlx-community/parakeet-tdt-0.6b-v3"
    assert cfg.server.llm_model == "mlx-community/Qwen3-1.7B-4bit"
    assert cfg.routing.fast_path_max_words == 5
    assert cfg.routing.self_correction_triggers == \
        ["anzi", "scusa", "aspetta", "cioè", "o meglio", "volevo dire"]
    assert cfg.glossary.phonetic_overrides["fast a pi"] == "FastAPI"
    assert "FastAPI" in cfg.glossary.canonical_terms


def test_missing_required_field_raises(tmp_path: Path):
    p = tmp_path / "bad.yaml"
    p.write_text("server:\n  host: 127.0.0.1\n")
    with pytest.raises(ValidationError):
        load_config(p)
