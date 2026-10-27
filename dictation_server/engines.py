"""Platform-neutral engine protocols shared by the dictation pipeline.

The pipeline must not know which runtime produces the transcript: it hands
raw PCM to an :class:`STT` and text to an :class:`LLM`. MLX (macOS) and
sherpa-onnx (Windows/Linux) both satisfy these protocols.
"""

from typing import Protocol


class LLMDeadlineExceeded(Exception):
    """The cooperative LLM deadline expired before generation completed."""


class STT(Protocol):
    """Speech-to-text engine accepting PCM16 little-endian mono at 16 kHz."""

    def load(self) -> None:
        """Load (or warm) the underlying model. Must precede transcribe."""

    def transcribe(self, pcm: bytes) -> str:
        """Return the transcript for ``pcm`` with surrounding space removed."""


class LLM(Protocol):
    """Text cleaner applied after STT when routing asks for it."""

    def load(self) -> None: ...

    def freeze(self, system_prompt: str) -> None: ...

    def clean(
        self,
        text: str,
        profile: str = "default",
        *,
        deadline: float | None = None,
    ) -> str: ...
