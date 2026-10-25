from pathlib import Path

import pytest
from pydantic import ValidationError
from dictation_server.config import load_config

CONFIG_PATH = Path(__file__).resolve().parents[1] / "config.yaml"

SPEC_CANONICAL_TERMS = [
    "Proxmox VE", "FastAPI", "CodeIgniter 4", "PostgreSQL", "MariaDB",
    "Home Assistant", "ESPHome", "Tasmota", "Tailscale", "MikroTik",
    "RouterOS", "Authelia", "Nginx Proxy Manager", "MLX", "Parakeet",
    "llama.cpp", "OpenCode", "Cursor", "WebSocket", "GitHub",
]
SPEC_PHONETIC_OVERRIDES = {
    "elle ics ci": "LXC",
    "emme elle ics": "MLX",
    "emme q ti ti": "MQTT",
    "z f s": "ZFS",
    "zeta effe esse": "ZFS",
    "q device": "QDevice",
    "acca a": "HA",
    "acca ti ti pi": "HTTP",
    "v elle a en": "VLAN",
    "fast a pi": "FastAPI",
    "prossimo ve": "Proxmox VE",
    "lama punto ci pi pi": "llama.cpp",
    "superpowers": "superpowers",
}


def test_load_full_config():
    cfg = load_config(CONFIG_PATH)
    assert cfg.server.port == 8000
    assert cfg.server.stt_model == "mlx-community/parakeet-tdt-0.6b-v3"
    assert cfg.server.llm_model == "mlx-community/Qwen3-1.7B-4bit"
    assert cfg.routing.fast_path_max_words == 5
    assert cfg.routing.self_correction_triggers == \
        ["anzi", "scusa", "aspetta", "cioè", "o meglio", "volevo dire"]
    assert cfg.glossary.canonical_terms == SPEC_CANONICAL_TERMS
    assert cfg.glossary.phonetic_overrides == SPEC_PHONETIC_OVERRIDES


def test_missing_required_field_raises(tmp_path: Path):
    p = tmp_path / "bad.yaml"
    p.write_text("server:\n  host: 127.0.0.1\n")
    with pytest.raises(ValidationError):
        load_config(p)


def test_unknown_config_key_raises(tmp_path: Path):
    p = tmp_path / "typo.yaml"
    p.write_text(
        "server:\n"
        "  host: 127.0.0.1\n"
        "  prt: 9999\n"
        "  stt_model: stt\n"
        "  llm_model: llm\n"
        "routing:\n"
        "  self_correction_triggers: []\n"
        "glossary:\n"
        "  canonical_terms: []\n"
        "  phonetic_overrides: {}\n"
    )
    with pytest.raises(ValidationError):
        load_config(p)
