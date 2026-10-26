"""Production-equivalent cleaner finalizers for the STT benchmark.

Phase B loads only a cleaner.  Production mode reuses the server's Pipeline
with an EchoSTT so routing/glossary/deadline/fallback stay identical without
loading an STT model.  Forced mode applies the cleaner to every clip.
"""

from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


class UnknownCleaner(ValueError):
    """Requested cleaner id is not registered."""


class CleanerUnavailable(RuntimeError):
    """The cleaner runtime/model cannot be loaded."""


@dataclass(frozen=True)
class CleanerSpec:
    id: str
    model_id: str
    precision: str
    status: str = "not_verified"


CLEANERS: dict[str, CleanerSpec] = {
    "qwen3_1_7b_4bit": CleanerSpec(
        id="qwen3_1_7b_4bit",
        model_id="mlx-community/Qwen3-1.7B-4bit",
        precision="4bit",
        status="production",
    ),
    "qwen3_5_0_8b_4bit": CleanerSpec(
        id="qwen3_5_0_8b_4bit",
        model_id="mlx-community/Qwen3.5-0.8B-4bit",
        precision="4bit",
    ),
    "qwen2_5_1_5b_instruct_4bit": CleanerSpec(
        id="qwen2_5_1_5b_instruct_4bit",
        model_id="mlx-community/Qwen2.5-1.5B-Instruct-4bit",
        precision="4bit",
    ),
}
CLEANER_IDS: tuple[str, ...] = tuple(CLEANERS)


class EchoSTT:
    """Pipeline STT seam that returns a precomputed transcript."""

    def __init__(self) -> None:
        self.text = ""

    def transcribe(self, _audio) -> str:
        return self.text


class PipelineFinalizer:
    """Load one cleaner and apply production or forced post-processing."""

    def __init__(
        self,
        cleaner_id: str,
        config_path: str | Path = ROOT / "config.yaml",
    ) -> None:
        spec = CLEANERS.get(cleaner_id)
        if spec is None:
            raise UnknownCleaner(
                f"unknown cleaner '{cleaner_id}'; available cleaners: "
                f"{', '.join(CLEANER_IDS)}"
            )
        from dictation_server.config import load_config

        self.spec = spec
        self.cleaner_id = spec.id
        self.model_id = spec.model_id
        self.precision = spec.precision
        self.status = spec.status
        self.revision: str | None = None
        self.cfg = load_config(config_path)
        self.warnings: list[str] = []
        self.fast_path = True
        self._llm = None
        self._pipeline = None
        self._loop = None
        self._echo = EchoSTT()

    def load(self) -> None:
        try:
            from dictation_server.llm import LLMCleaner
            from dictation_server.pipeline import Pipeline
            from dictation_server.prompt import build_system_prompt

            llm = LLMCleaner(self.model_id)
            llm.load()
            llm.freeze(build_system_prompt(self.cfg.glossary))
            self._llm = llm
            self._loop = asyncio.new_event_loop()
            self._pipeline = Pipeline(self._echo, llm, self.cfg)
        except Exception as exc:
            raise CleanerUnavailable(
                f"cleaner '{self.cleaner_id}' ({self.model_id}) failed to "
                f"load: {exc}"
            ) from exc

    def finalize(
        self,
        raw_text: str,
        profile: str,
        *,
        force_cleaner: bool = False,
    ) -> str:
        if self._llm is None or self._loop is None:
            raise CleanerUnavailable(
                f"cleaner '{self.cleaner_id}' must be loaded before finalize"
            )

        if force_cleaner:
            from dictation_server.glossary import normalize_canonical_terms
            from dictation_server.llm import LLMDeadlineExceeded

            deadline = time.monotonic() + self.cfg.timeouts.llm_seconds
            try:
                self.warnings = []
                self.fast_path = False
                return self._llm.clean(
                    raw_text, profile, deadline=deadline
                )
            except LLMDeadlineExceeded:
                self.warnings = ["llm_timeout"]
            except Exception:
                self.warnings = ["llm_failed"]
            self.fast_path = False
            return normalize_canonical_terms(
                raw_text, self.cfg.glossary.canonical_terms
            )

        self._echo.text = raw_text
        result = self._loop.run_until_complete(
            self._pipeline.process(
                b"\x00\x00", profile=profile, bypass_llm=False
            )
        )
        self.warnings = list(result.warnings)
        self.fast_path = result.fast_path
        return result.final_text

    def close(self) -> None:
        loop = self._loop
        self._loop = None
        self._pipeline = None
        if loop is not None and not loop.is_closed():
            loop.close()
