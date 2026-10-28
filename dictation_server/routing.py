"""Decoding constants and Windows-compatible token budget."""

TEMPERATURE: float = 0.0


def estimate_max_tokens(raw_text: str) -> int:
    """Match the Windows cleaner's character-based token budget."""
    return min(max(len(raw_text) * 2 + 64, 128), 4096)
