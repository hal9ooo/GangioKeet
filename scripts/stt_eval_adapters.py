"""STT adapters for the reproducible local evaluation harness.

Only the standard library is imported at module import time.  Optional
runtimes (``mlx``, ``mlx_audio``, ``parakeet_mlx``, ...) are imported lazily
inside ``load()`` so the default test-suite never touches them.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol, runtime_checkable

import stt_eval_provenance as provenance

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
        status="verified",
        notes="mlx-audio 0.6.2 Qwen3ASRModel.generate",
    ),
    "cohere_transcribe_03_2026": CandidateSpec(
        id="cohere_transcribe_03_2026",
        model_id="CohereLabs/cohere-transcribe-03-2026",
        runtime="mlx_audio",
        revision=None,
        precision=None,
        status="not_run",
        notes="gated HF repo; API not exercised without access",
    ),
    "nemotron_3_5_asr_streaming_0_6b": CandidateSpec(
        id="nemotron_3_5_asr_streaming_0_6b",
        model_id="mlx-community/nemotron-3.5-asr-streaming-0.6b",
        runtime="mlx_audio",
        revision=None,
        precision="bf16",
        status="verified",
        notes="mlx-audio 0.6.2 Nemotron batch/final generate",
    ),
    "whisper_large_v3_turbo": CandidateSpec(
        id="whisper_large_v3_turbo",
        model_id="mlx-community/whisper-large-v3-turbo-asr-fp16",
        runtime="mlx_audio",
        revision=None,
        precision="fp16",
        status="verified",
        notes="mlx-audio 0.6.2 Whisper generate",
    ),
}

CANDIDATE_IDS: tuple[str, ...] = (
    "parakeet_baseline",
    *CANDIDATES.keys(),
)

_LANGUAGE_KWARGS = {
    "qwen3_asr_1_7b_8bit": {
        "language": "Italian",
        "temperature": 0.0,
    },
    "cohere_transcribe_03_2026": {"language": "it"},
    "nemotron_3_5_asr_streaming_0_6b": {"language": "it-IT"},
    "whisper_large_v3_turbo": {
        "language": "it",
        "temperature": 0.0,
        "return_timestamps": False,
    },
}


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
        self.revision = provenance.revision_for(
            "stt", "parakeet_baseline"
        )
        self.status = provenance.status_for("stt", "parakeet_baseline")
        self.not_run_reason = provenance.reason_for(
            "stt", "parakeet_baseline"
        )
        self._stt = None
        self._to_array = None

    def load(self) -> None:
        if self.status == "not_run":
            raise AdapterUnavailable(
                f"candidate '{self.candidate_id}' is NOT RUN: "
                f"{self.not_run_reason}"
            )
        try:
            from dictation_server.audio import pcm_bytes_to_mlx_array
            from dictation_server.stt import ParakeetSTT
        except ImportError as exc:
            raise AdapterUnavailable(
                "baseline runtime unavailable for candidate "
                f"'{self.candidate_id}' (model {self.model_id}): {exc}. "
                "No implicit install or model download is performed."
            ) from exc

        try:
            local = provenance.cached_snapshot(
                self.model_id, self.revision
            )
        except Exception as exc:
            raise AdapterUnavailable(
                f"pinned baseline revision unavailable for "
                f"'{self.candidate_id}': {exc}"
            ) from exc
        self._to_array = pcm_bytes_to_mlx_array
        stt = ParakeetSTT(str(local))
        stt.load()
        self._stt = stt

    def transcribe(self, pcm: bytes) -> str:
        if self._stt is None or self._to_array is None:
            raise AdapterUnavailable(
                "parakeet_baseline adapter must be loaded before transcribing"
            )
        return self._stt.transcribe(self._to_array(pcm))


class MlxAudioAdapter:
    """Adapter for mlx-audio STT models verified against 0.6.2."""

    def __init__(self, spec: CandidateSpec):
        self.spec = spec
        self.candidate_id = spec.id
        self.model_id = spec.model_id
        self.revision = provenance.revision_for("stt", spec.id)
        self.precision = spec.precision
        self.status = provenance.status_for("stt", spec.id)
        self.not_run_reason = provenance.reason_for("stt", spec.id)
        self.has_finalizer = False
        self._model = None
        self._kwargs = dict(_LANGUAGE_KWARGS[spec.id])

    def load(self) -> None:
        if self.status == "not_run":
            raise AdapterUnavailable(
                f"candidate '{self.candidate_id}' is NOT RUN: "
                f"{self.not_run_reason}"
            )
        try:
            from mlx_audio.stt import load
        except ImportError as exc:
            raise AdapterUnavailable(
                f"runtime 'mlx_audio' unavailable for candidate "
                f"'{self.candidate_id}' (model {self.model_id}): {exc}. "
                "Install it only in an isolated eval environment; the harness "
                "installs no packages (the model loader may populate the HF "
                "cache on first use)."
            ) from exc
        try:
            local = provenance.cached_snapshot(
                self.model_id, self.revision
            )
            self._model = load(str(local))
        except Exception as exc:
            raise AdapterUnavailable(
                f"model load failed for candidate '{self.candidate_id}' "
                f"({self.model_id}): {exc}"
            ) from exc

    def transcribe(self, pcm: bytes) -> str:
        if self._model is None:
            raise AdapterUnavailable(
                f"adapter '{self.candidate_id}' must be loaded before "
                "transcribing"
            )
        from dictation_server.audio import pcm_bytes_to_mlx_array

        audio = pcm_bytes_to_mlx_array(pcm)
        try:
            result = self._model.generate(audio, **self._kwargs)
        except Exception as exc:
            raise AdapterUnavailable(
                f"transcription failed for candidate '{self.candidate_id}' "
                f"({self.model_id}): {exc}"
            ) from exc

        if isinstance(result, list):
            text = " ".join(
                getattr(item, "text", "") or "" for item in result
            )
        else:
            text = getattr(result, "text", None)
        if not isinstance(text, str):
            raise AdapterUnavailable(
                f"candidate '{self.candidate_id}' returned no text"
            )
        return text


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
    return MlxAudioAdapter(spec)
