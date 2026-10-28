from pathlib import Path
from typing import Literal

import yaml
from pydantic import BaseModel, ConfigDict, Field, model_validator


class _StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class ServerConfig(_StrictModel):
    backend: Literal["mlx", "onnx"] = "mlx"
    host: str = "0.0.0.0"
    port: int = 8000
    stt_model: str
    llm_model: str | None = None
    stt_onnx_dir: str | None = None

    @model_validator(mode="after")
    def _check_backend_engines(self) -> "ServerConfig":
        if self.backend == "mlx":
            if not self.llm_model:
                raise ValueError(
                    "server.llm_model is required when server.backend is 'mlx'"
                )
            return self
        if not (self.stt_onnx_dir or "").strip():
            raise ValueError(
                "server.stt_onnx_dir is required when server.backend is 'onnx'"
            )
        return self


class RoutingConfig(_StrictModel):
    # Deprecated: the pipeline now always cleans non-empty transcripts. Kept
    # in the schema so existing local configs remain loadable.
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
    processing_seconds: float = 240.0
    llm_seconds: float = 180.0

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


class RecordingConfig(_StrictModel):
    enabled: bool = False
    directory: str = "recordings"
    store_context: bool = False

    @model_validator(mode="after")
    def _check_directory(self) -> "RecordingConfig":
        if not self.directory.strip():
            raise ValueError("recording.directory must not be empty")
        return self


class AppConfig(_StrictModel):
    server: ServerConfig
    routing: RoutingConfig
    glossary: GlossaryConfig
    limits: LimitsConfig = Field(default_factory=LimitsConfig)
    timeouts: TimeoutsConfig = Field(default_factory=TimeoutsConfig)
    security: SecurityConfig = Field(default_factory=SecurityConfig)
    recording: RecordingConfig = Field(default_factory=RecordingConfig)


def load_config(path: str | Path) -> AppConfig:
    with open(path, encoding="utf-8") as fh:
        data = yaml.safe_load(fh)
    return AppConfig.model_validate(data)
