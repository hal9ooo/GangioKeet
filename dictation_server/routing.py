import string

from .config import RoutingConfig

TEMPERATURE: float = 0.0

# ASCII punctuation plus common typographic punctuation, stripped only from
# the edges of each whitespace-delimited token before phrase matching.
_EDGE_PUNCTUATION = string.punctuation + "“”‘’«»‹›–—…¿¡"


def compute_max_tokens(n_input_tokens: int) -> int:
    return (n_input_tokens * 5) // 4 + 12


def count_words(text: str) -> int:
    return len(text.split())


def _normalized_words(text: str) -> list[str]:
    words = (word.strip(_EDGE_PUNCTUATION).casefold() for word in text.split())
    return [word for word in words if word]


def _contains_phrase(words: list[str], phrase: str) -> bool:
    needle = _normalized_words(phrase)
    if not needle:
        return False
    width = len(needle)
    return any(
        words[i:i + width] == needle
        for i in range(len(words) - width + 1)
    )


def contains_override(text: str, overrides: dict[str, str]) -> bool:
    words = _normalized_words(text)
    return any(_contains_phrase(words, spoken) for spoken in overrides)


def should_run_llm(
    text: str,
    cfg: RoutingConfig,
    overrides: dict[str, str],
) -> bool:
    if count_words(text) >= cfg.fast_path_max_words:
        return True
    words = _normalized_words(text)
    if any(_contains_phrase(words, spoken) for spoken in overrides):
        return True
    return any(
        _contains_phrase(words, trigger)
        for trigger in cfg.self_correction_triggers
    )
