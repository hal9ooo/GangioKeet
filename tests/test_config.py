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


LEGACY_SERVER = (
    "server:\n"
    "  host: 127.0.0.1\n"
    "  stt_model: stt\n"
    "  llm_model: llm\n"
    "routing:\n"
    "  self_correction_triggers: []\n"
    "glossary:\n"
    "  canonical_terms: []\n"
    "  phonetic_overrides: {}\n"
)


def test_legacy_config_gets_reliability_defaults(tmp_path: Path):
    p = tmp_path / "legacy.yaml"
    p.write_text(LEGACY_SERVER)
    cfg = load_config(p)

    assert cfg.limits.max_audio_bytes == 67108864
    assert cfg.limits.max_http_body_bytes == 68157440
    assert cfg.limits.max_buffered_audio_bytes == 201326592
    assert cfg.limits.max_sessions == 4
    assert cfg.limits.max_pending_jobs == 2

    assert cfg.timeouts.upload_seconds == 10.0
    assert cfg.timeouts.decode_seconds == 5.0
    assert cfg.timeouts.queue_seconds == 2.0
    assert cfg.timeouts.processing_seconds == 10.0
    assert cfg.timeouts.llm_seconds == 5.0

    assert cfg.security.api_key_env is None


def _write_limits(tmp_path: Path, limits_yaml: str) -> Path:
    p = tmp_path / "limits.yaml"
    p.write_text(LEGACY_SERVER + "limits:\n" + limits_yaml)
    return p


@pytest.mark.parametrize(
    "limits_yaml",
    [
        "  max_audio_bytes: 0\n",
        "  max_audio_bytes: -1\n",
        "  max_http_body_bytes: 0\n",
        "  max_buffered_audio_bytes: -5\n",
        "  max_sessions: 0\n",
        "  max_pending_jobs: -2\n",
    ],
)
def test_invalid_limits_rejected_non_positive(tmp_path: Path, limits_yaml: str):
    with pytest.raises(ValidationError):
        load_config(_write_limits(tmp_path, limits_yaml))


@pytest.mark.parametrize(
    "limits_yaml",
    [
        "  max_audio_bytes: 201326593\n",  # > max_buffered_audio_bytes
        "  max_audio_bytes: 68157441\n",  # > max_http_body_bytes
    ],
)
def test_invalid_limits_rejected_audio_exceeds_budgets(tmp_path: Path, limits_yaml: str):
    with pytest.raises(ValidationError):
        load_config(_write_limits(tmp_path, limits_yaml))


@pytest.mark.parametrize(
    "timeouts_yaml",
    [
        "  upload_seconds: 0\n",
        "  decode_seconds: -1.0\n",
        "  queue_seconds: 0\n",
        "  processing_seconds: -0.5\n",
        "  llm_seconds: 0\n",
    ],
)
def test_invalid_timeouts_rejected(tmp_path: Path, timeouts_yaml: str):
    p = tmp_path / "timeouts.yaml"
    p.write_text(LEGACY_SERVER + "timeouts:\n" + timeouts_yaml)
    with pytest.raises(ValidationError):
        load_config(p)


def test_unknown_key_still_rejected(tmp_path: Path):
    p = tmp_path / "unknown.yaml"
    p.write_text(LEGACY_SERVER + "limits:\n  max_audio_byte: 1\n")
    with pytest.raises(ValidationError):
        load_config(p)

    p = tmp_path / "unknown-top.yaml"
    p.write_text(LEGACY_SERVER + "limts:\n  max_audio_bytes: 1\n")
    with pytest.raises(ValidationError):
        load_config(p)
