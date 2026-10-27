"""Punctuation scoring, independent from normalized word error rate."""

from collections import Counter, defaultdict
import re
import unicodedata

from stt_eval_metrics import normalize_for_wer, score_transcripts

MARKS = "?.,!:;"
_TOKENS = re.compile(r"\w+(?:['’‘ʼ]\w+)*|[?.,!:;]")


def _parts(text: str):
    words, marks = [], defaultdict(Counter)
    for match in _TOKENS.finditer(unicodedata.normalize("NFKC", text)):
        token = match.group()
        if token in MARKS:
            marks[len(words) - 1][token] += 1
        else:
            words.extend(normalize_for_wer(token).split())
    return words, marks


def _matches(reference, hypothesis):
    """Levenshtein exact anchors: diagonal first, then deletion, insertion.

    Backtracking from the end matches rightmost repeated words first.
    Substitutions are never exact anchors for punctuation boundaries.
    """
    n, m = len(reference), len(hypothesis)
    cost = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n + 1):
        cost[i][0] = i
    for j in range(m + 1):
        cost[0][j] = j
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            cost[i][j] = min(cost[i - 1][j] + 1, cost[i][j - 1] + 1,
                             cost[i - 1][j - 1] + (reference[i - 1] != hypothesis[j - 1]))
    anchors = {}
    i, j = n, m
    while i or j:
        if i and j and cost[i][j] == cost[i - 1][j - 1] + (reference[i - 1] != hypothesis[j - 1]):
            if reference[i - 1] == hypothesis[j - 1]:
                anchors[i - 1] = j - 1
            i, j = i - 1, j - 1
        elif i and cost[i][j] == cost[i - 1][j] + 1:
            i -= 1
        else:
            j -= 1
    return anchors


def _metrics(tp: int, fp: int, fn: int) -> dict:
    return {"true_positive": tp, "false_positive": fp, "false_negative": fn,
            "precision": tp / (tp + fp) if tp + fp else None,
            "recall": tp / (tp + fn) if tp + fn else None,
            "f1": 2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else None}


def _summary(counts, unaligned: int) -> dict:
    by_mark = {mark: _metrics(*counts[mark]) for mark in MARKS}
    totals = [sum(counts[mark][i] for mark in MARKS) for i in range(3)]
    return {**_metrics(*totals), "by_mark": by_mark,
            "missed_questions": counts["?"][2], "false_questions": counts["?"][1],
            "unaligned_boundaries": unaligned}


def score_punctuation(reference: str, hypothesis: str) -> dict:
    ref, ref_marks = _parts(reference)
    hyp, hyp_marks = _parts(hypothesis)
    anchors = _matches(ref, hyp)
    # Both sides of a boundary must be adjacent exact anchors. At an edge,
    # both transcripts must have the same edge; a missing word is not a match.
    boundaries = {}
    if anchors.get(0) == 0:
        boundaries[-1] = -1
    for i, j in anchors.items():
        if (i == len(ref) - 1 and j == len(hyp) - 1) or anchors.get(i + 1) == j + 1:
            boundaries[i] = j
    counts = {mark: [0, 0, 0] for mark in MARKS}
    for i, j in boundaries.items():
        for mark in MARKS:
            a, b = ref_marks[i][mark], hyp_marks[j][mark]
            counts[mark][0] += min(a, b)
            counts[mark][1] += max(b - a, 0)
            counts[mark][2] += max(a - b, 0)
    unaligned = len(set(ref_marks) - boundaries.keys()) + len(set(hyp_marks) - set(boundaries.values()))
    return _summary(counts, unaligned)


def aggregate_punctuation(scores: list[dict | None]) -> dict | None:
    scores = [score for score in scores if score is not None]
    if not scores:
        return None
    counts = {mark: [sum(score["by_mark"][mark][field] for score in scores)
                     for field in ("true_positive", "false_positive", "false_negative")]
              for mark in MARKS}
    return _summary(counts, sum(score["unaligned_boundaries"] for score in scores))


def score_record(entry: dict, hypothesis: str) -> dict:
    raw, final = entry.get("reference_raw", ""), entry.get("reference_final", "")
    silence = entry.get("is_silence", False)
    if entry.get("reference_status") == "unreviewed" or (not silence and not (raw.strip() or final.strip())):
        return {"scored": False, "reason": "unreviewed", "raw": None, "final": None, "punctuation": None}
    raw_score = score_transcripts(raw, hypothesis) if silence or raw.strip() else None
    final_score = score_transcripts(final, hypothesis) if silence or final.strip() else None
    punctuation = score_punctuation(final, hypothesis) if final.strip() else None
    return {"scored": True, "reason": None, "raw": raw_score, "final": final_score, "punctuation": punctuation}
