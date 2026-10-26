from pathlib import Path

import yaml
from pydantic import BaseModel, ConfigDict, Field, model_validator


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


class LimitsConfig(_StrictModel):
    max_audio_bytes: int = 67108864
    max_http_body_bytes: int = 68157440
    max_buffered_audio_bytes: int = 201326592
    max_sessions: int = 4
    max_pending_jobs: int = 2

    @model_validator(mode="after")
    def _check_positive_and_budgets(self) -> "LimitsConfig":
        positive = [
            "max_audio_bytes", "max_http_body_bytes",
            "max_buffered_audio_bytes", "max_sessions", "max_pending_jobs",
        ]
        for name in positive:
            if getattr(self, name) <= 0:
                raise ValueError(f"limits.{name} must be > 0")
        if self.max_audio_bytes > self.max_buffered_audio_bytes:
            raise ValueError(
                "limits.max_audio_bytes must not exceed "
                "limits.max_buffered_audio_bytes"
            )
        if self.max_audio_bytes > self.max_http_body_bytes:
            raise ValueError(
                "limits.max_audio_bytes must not exceed "
                "limits.max_http_body_bytes"
            )
        return self


class TimeoutsConfig(_StrictModel):
    upload_seconds: float = 10.0
    decode_seconds: float = 5.0
    queue_seconds: float = 2.0
    processing_seconds: float = 10.0
    llm_seconds: float = 5.0

    @model_validator(mode="after")
    def _check_positive(self) -> "TimeoutsConfig":
        for name in (
            "upload_seconds", "decode_seconds", "queue_seconds",
            "processing_seconds", "llm_seconds",
        ):
            if getattr(self, name) <= 0:
                raise ValueError(f"timeouts.{name} must be > 0")
        return self


class SecurityConfig(_StrictModel):
    api_key_env: str | None = None


class AppConfig(_StrictModel):
    server: ServerConfig
    routing: RoutingConfig
    glossary: GlossaryConfig
    limits: LimitsConfig = Field(default_factory=LimitsConfig)
    timeouts: TimeoutsConfig = Field(default_factory=TimeoutsConfig)
    security: SecurityConfig = Field(default_factory=SecurityConfig)


def load_config(path: str | Path) -> AppConfig:
    with open(path, encoding="utf-8") as fh:
        data = yaml.safe_load(fh)
    return AppConfig.model_validate(data)
