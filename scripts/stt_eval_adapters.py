"""STT adapters for the reproducible local evaluation harness.

Only the standard library is imported at module import time.  Optional
runtimes (``mlx``, ``mlx_audio``, ``parakeet_mlx``, ...) are imported lazily
inside ``load()`` so the default test-suite never touches them.

The four Task 9 candidates are registered with metadata but are intentionally
marked ``not_verified``: this task must not guess an unverified ``mlx-audio``
API.  Task 9 will implement and verify those adapters on real models.
"""

from __future__ import annotations

import importlib
from dataclasses import dataclass
from typing import Protocol, runtime_checkable

DEFAULT_PARAKEET_MODEL = "mlx-community/parakeet-tdt-0.6b-v3"


class AdapterError(RuntimeError):
    """Base class for explicit, user-facing adapter failures."""


class AdapterUnavailable(AdapterError):
    """The optional runtime or a verified adapter is not available."""


class UnknownCandidate(AdapterError):
    """The requested candidate id is not in the registry."""


@dataclass(frozen=True)
class CandidateSpec:
    id: str
    model_id: str
    runtime: str
    revision: str | None
    precision: str | None
    status: str
    notes: str


CANDIDATES: dict[str, CandidateSpec] = {
    "qwen3_asr_1_7b_8bit": CandidateSpec(
        id="qwen3_asr_1_7b_8bit",
        model_id="mlx-community/Qwen3-ASR-1.7B-8bit",
        runtime="mlx_audio",
        revision=None,
        precision="8bit",
        status="not_verified",
        notes="Task 9 must implement and verify the mlx-audio API.",
    ),
    "cohere_transcribe_03_2026": CandidateSpec(
        id="cohere_transcribe_03_2026",
        model_id="mlx-community/cohere-transcribe-03-2026",
        runtime="mlx_audio",
        revision=None,
        precision=None,
        status="not_verified",
        notes="Task 9 must implement and verify the mlx-audio API.",
    ),
    "nemotron_3_5_asr_streaming_0_6b": CandidateSpec(
        id="nemotron_3_5_asr_streaming_0_6b",
        model_id="mlx-community/nemotron-3.5-asr-streaming-0.6b",
        runtime="mlx_audio",
        revision=None,
        precision=None,
        status="not_verified",
        notes="Task 9 must implement and verify the mlx-audio API.",
    ),
    "whisper_large_v3_turbo": CandidateSpec(
        id="whisper_large_v3_turbo",
        model_id="mlx-community/whisper-large-v3-turbo",
        runtime="mlx_audio",
        revision=None,
        precision=None,
        status="not_verified",
        notes="Task 9 must implement and verify the mlx-audio API.",
    ),
}

CANDIDATE_IDS: tuple[str, ...] = (
    "parakeet_baseline",
    *CANDIDATES.keys(),
)


@runtime_checkable
class SttAdapter(Protocol):
    """Minimal adapter contract: load once, then transcribe PCM16 bytes."""

    candidate_id: str
    model_id: str
    revision: str | None
    precision: str | None

    def load(self) -> None: ...

    def transcribe(self, pcm: bytes) -> str: ...


class ParakeetBaselineAdapter:
    """Existing server STT path (``ParakeetSTT`` + PCM conversion)."""

    candidate_id = "parakeet_baseline"
    revision: str | None = None
    precision = "bfloat16"
    status = "baseline"
    has_finalizer = False

    def __init__(self, model_id: str = DEFAULT_PARAKEET_MODEL):
        self.model_id = model_id
        self._stt = None
        self._to_array = None

    def load(self) -> None:
        try:
            from dictation_server.audio import pcm_bytes_to_mlx_array
            from dictation_server.stt import ParakeetSTT
        except ImportError as exc:
            raise AdapterUnavailable(
                "baseline runtime unavailable for candidate "
                f"'{self.candidate_id}' (model {self.model_id}): {exc}. "
                "No implicit install or model download is performed."
            ) from exc

        self._to_array = pcm_bytes_to_mlx_array
        stt = ParakeetSTT(self.model_id)
        stt.load()
        self._stt = stt

    def transcribe(self, pcm: bytes) -> str:
        if self._stt is None or self._to_array is None:
            raise AdapterUnavailable(
                "parakeet_baseline adapter must be loaded before transcribing"
            )
        return self._stt.transcribe(self._to_array(pcm))


class UnverifiedCandidateAdapter:
    """Registry placeholder that fails explicitly until Task 9 verifies it."""

    revision: str | None = None

    def __init__(self, spec: CandidateSpec):
        self.spec = spec
        self.candidate_id = spec.id
        self.model_id = spec.model_id
        self.revision = spec.revision
        self.precision = spec.precision
        self.status = spec.status
        self.has_finalizer = False

    def load(self) -> None:
        try:
            importlib.import_module(self.spec.runtime)
        except ModuleNotFoundError as exc:
            raise AdapterUnavailable(
                f"runtime '{self.spec.runtime}' unavailable for candidate "
                f"'{self.candidate_id}' (model {self.model_id}): {exc}. "
                "Install it only in an isolated eval environment; the harness "
                "never installs packages or downloads models implicitly."
            ) from exc

        raise AdapterUnavailable(
            f"adapter not yet verified for candidate '{self.candidate_id}' "
            f"(model {self.model_id}): Task 9 must implement and verify the "
            f"{self.spec.runtime} API. No unverified API is guessed by this "
            "harness."
        )

    def transcribe(self, pcm: bytes) -> str:
        raise AdapterUnavailable(
            f"adapter not yet verified for candidate '{self.candidate_id}'"
        )


def create_adapter(
    candidate_id: str,
    *,
    parakeet_model: str | None = None,
) -> SttAdapter:
    """Build an adapter without importing any optional runtime."""

    if candidate_id == "parakeet_baseline":
        return ParakeetBaselineAdapter(
            model_id=parakeet_model or DEFAULT_PARAKEET_MODEL
        )

    spec = CANDIDATES.get(candidate_id)
    if spec is None:
        available = ", ".join(CANDIDATE_IDS)
        raise UnknownCandidate(
            f"unknown candidate '{candidate_id}'; available candidates: "
            f"{available}"
        )
    return UnverifiedCandidateAdapter(spec)
