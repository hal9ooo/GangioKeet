"""Word-error-rate scoring primitives for the local STT evaluation harness.

The normalizer is deliberately shared by references and hypotheses so that
casing, punctuation and Unicode presentation differences never masquerade as
recognition errors.  Empty references are treated as silence/negative samples:
they produce ``wer=None`` and a ``false_positive`` flag instead of an invented
WER of zero.
"""

from __future__ import annotations

import unicodedata

NORMALIZER_REVISION = "stt-eval-normalizer-v1"

_APOSTROPHES = {"'", "\u2019", "\u2018", "\u02bc"}


def normalize_for_wer(text: str) -> str:
    """Normalize text for word-level WER comparison.

    Applies Unicode NFKC, casefolds, replaces punctuation/symbols with spaces
    except for internal apostrophes, and collapses whitespace.  The same
    function must be used for references and hypotheses.
    """

    text = unicodedata.normalize("NFKC", text).casefold()
    text = text.replace("\u2019", "'").replace("\u2018", "'")

    characters: list[str] = []
    for index, character in enumerate(text):
        previous = text[index - 1] if index > 0 else ""
        following = text[index + 1] if index + 1 < len(text) else ""
        if character in _APOSTROPHES:
            if previous.isalnum() and following.isalnum():
                characters.append("'")
            else:
                characters.append(" ")
            continue
        category = unicodedata.category(character)
        if character.isalnum() or category.startswith("M"):
            characters.append(character)
        else:
            characters.append(" ")

    return " ".join("".join(characters).split())


def _edit_operations(reference: list[str], hypothesis: list[str]) -> dict:
    """Return substitution/deletion/insertion counts for two token lists."""

    rows = len(reference) + 1
    columns = len(hypothesis) + 1
    cost = [[0] * columns for _ in range(rows)]

    for row in range(rows):
        cost[row][0] = row
    for column in range(columns):
        cost[0][column] = column

    for row in range(1, rows):
        for column in range(1, columns):
            if reference[row - 1] == hypothesis[column - 1]:
                cost[row][column] = cost[row - 1][column - 1]
            else:
                cost[row][column] = 1 + min(
                    cost[row - 1][column - 1],
                    cost[row - 1][column],
                    cost[row][column - 1],
                )

    substitutions = 0
    deletions = 0
    insertions = 0
    row = len(reference)
    column = len(hypothesis)

    while row > 0 or column > 0:
        if (
            row > 0
            and column > 0
            and reference[row - 1] == hypothesis[column - 1]
            and cost[row][column] == cost[row - 1][column - 1]
        ):
            row -= 1
            column -= 1
        elif (
            row > 0
            and column > 0
            and cost[row][column] == cost[row - 1][column - 1] + 1
        ):
            substitutions += 1
            row -= 1
            column -= 1
        elif row > 0 and cost[row][column] == cost[row - 1][column] + 1:
            deletions += 1
            row -= 1
        elif (
            column > 0
            and cost[row][column] == cost[row][column - 1] + 1
        ):
            insertions += 1
            column -= 1
        else:  # pragma: no cover - defensive against malformed DP state
            raise AssertionError("unable to backtrack edit-distance matrix")

    return {
        "substitutions": substitutions,
        "deletions": deletions,
        "insertions": insertions,
    }


def score_transcripts(reference: str, hypothesis: str) -> dict:
    """Score one raw hypothesis against one reference.

    ``wer`` is ``(S + D + I) / N`` where ``N`` is the number of reference
    words.  An empty reference is not a perfect transcript: it returns
    ``wer=None`` and sets ``false_positive`` when the hypothesis contains any
    words.  Such clips are aggregated separately as silence false positives.
    """

    reference_tokens = normalize_for_wer(reference).split()
    hypothesis_tokens = normalize_for_wer(hypothesis).split()

    if not reference_tokens:
        return {
            "substitutions": 0,
            "deletions": 0,
            "insertions": len(hypothesis_tokens),
            "reference_words": 0,
            "hypothesis_words": len(hypothesis_tokens),
            "wer": None,
            "false_positive": bool(hypothesis_tokens),
        }

    operations = _edit_operations(reference_tokens, hypothesis_tokens)
    errors = (
        operations["substitutions"]
        + operations["deletions"]
        + operations["insertions"]
    )
    return {
        **operations,
        "reference_words": len(reference_tokens),
        "hypothesis_words": len(hypothesis_tokens),
        "wer": errors / len(reference_tokens),
        "false_positive": False,
    }


def aggregate_scores(per_clip: list[dict]) -> dict:
    """Aggregate per-clip scores with a weighted WER.

    Each item may be either a score dictionary or a clip dictionary containing
    ``score`` and ``is_silence`` keys.  WER is ``sum(S+D+I) / sum(N)`` over
    non-silence clips, never a naive mean of per-clip percentages.  Silence
    clips are excluded from WER and reported by false-positive count/rate.
    """

    substitutions = 0
    deletions = 0
    insertions = 0
    reference_words = 0
    scored_clips = 0
    empty_reference_clips = 0
    empty_reference_false_positives = 0
    silence_clips = 0
    silence_false_positives = 0

    for item in per_clip:
        score = item.get("score", item)
        is_silence = bool(item.get("is_silence", False))
        false_positive = bool(score.get("false_positive", False))

        if is_silence:
            silence_clips += 1
            if false_positive:
                silence_false_positives += 1
            continue

        if int(score.get("reference_words", 0)) == 0:
            empty_reference_clips += 1
            if false_positive:
                empty_reference_false_positives += 1
            continue

        substitutions += int(score.get("substitutions", 0))
        deletions += int(score.get("deletions", 0))
        insertions += int(score.get("insertions", 0))
        reference_words += int(score["reference_words"])
        scored_clips += 1

    errors = substitutions + deletions + insertions
    return {
        "wer": errors / reference_words if reference_words else None,
        "substitutions": substitutions,
        "deletions": deletions,
        "insertions": insertions,
        "errors": errors,
        "reference_words": reference_words,
        "scored_clips": scored_clips,
        "empty_reference_clips": empty_reference_clips,
        "empty_reference_false_positives": empty_reference_false_positives,
        "silence_clips": silence_clips,
        "silence_false_positives": silence_false_positives,
        "silence_false_positive_rate": (
            silence_false_positives / silence_clips if silence_clips else None
        ),
        "total_clips": len(per_clip),
    }
