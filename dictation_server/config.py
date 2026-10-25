from pathlib import Path

import yaml
from pydantic import BaseModel, ConfigDict


class _StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class ServerConfig(_StrictModel):
    host: str = "0.0.0.0"
    port: int = 8000
    stt_model: str
    llm_model: str


class RoutingConfig(_StrictModel):
    fast_path_max_words: int = 5
    self_correction_triggers: list[str]


class GlossaryConfig(_StrictModel):
    canonical_terms: list[str]
    phonetic_overrides: dict[str, str]


class AppConfig(_StrictModel):
    server: ServerConfig
    routing: RoutingConfig
    glossary: GlossaryConfig


def load_config(path: str | Path) -> AppConfig:
    with open(path, encoding="utf-8") as fh:
        data = yaml.safe_load(fh)
    return AppConfig.model_validate(data)
