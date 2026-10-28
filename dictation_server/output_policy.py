"""Output guardrails shared with the Windows cleaner.

The marker list and expansion threshold mirror
``client-windows/src/GangioKeet.Inference/Cleaning/OutputPolicy.cs``.
"""

from __future__ import annotations

_MARKERS = (
    "<think",
    "</think",
    "<raw_dictation",
    "</raw_dictation",
    "<profile",
    "<glossary",
    "<|im_",
    "<|channel",
    "<channel|>",
    "<|turn>",
    "<turn|>",
    "[start_header_id]",
    "[start_of_turn]",
)


def validate_output(raw: str, output: str) -> list[str]:
    """Return the Windows-compatible rejection reasons for ``output``."""
    errors: list[str] = []
    if not output.strip():
        errors.append("empty_output")
    folded = output.casefold()
    if any(marker.casefold() in folded for marker in _MARKERS):
        errors.append("prompt_or_thinking_tags")
    if "\0" in output or "\ufffd" in output:
        errors.append("invalid_text")
    if len(output) > max(500, len(raw) * 4):
        errors.append("expanded_output")
    return errors
