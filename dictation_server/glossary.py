import re

# A canonical term is matched as a whole word or phrase. Lookaround boundaries
# are used instead of ``\b`` so terms starting or ending with punctuation
# (e.g. ``llama.cpp``) behave predictably and never match inside a larger word.
_LEFT_BOUNDARY = r"(?<!\w)"
_RIGHT_BOUNDARY = r"(?!\w)"


def _term_pattern(term: str) -> str:
    parts = [re.escape(part) for part in term.split()]
    return _LEFT_BOUNDARY + r"\s+".join(parts) + _RIGHT_BOUNDARY


def normalize_canonical_terms(
    text: str, canonical_terms: list[str], profile: str | None = None
) -> str:
    """Apply deterministic canonical casing to whole words and phrases.

    Matching is case-insensitive and the longest terms are replaced first so a
    short term cannot pre-empt an overlapping multi-word term. Text outside a
    match (punctuation, spacing, unrelated words) is left untouched. This does
    not resolve phonetic spellings; those remain an LLM responsibility.
    """
    if not text or not canonical_terms:
        return text
    if profile in {"code", "terminal"}:
        return text

    terms = list(
        dict.fromkeys(
            term.strip()
            for term in canonical_terms
            if term and term.strip()
        )
    )
    if not terms:
        return text

    terms.sort(key=len, reverse=True)
    result = text
    for term in terms:
        result = re.sub(
            _term_pattern(term),
            lambda _match, canonical=term: canonical,
            result,
            flags=re.IGNORECASE,
        )
    return result
