import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import stt_eval_punctuation as punctuation


def test_reports_a_missing_question_mark():
    result = punctuation.score_punctuation("Come funziona?", "Come funziona.")
    assert result["missed_questions"] == 1
    assert result["false_questions"] == 0
    assert result["by_mark"]["?"]["recall"] == 0


def test_reports_a_question_invented_from_an_assertion():
    result = punctuation.score_punctuation("Arrivi domani.", "Arrivi domani?")
    assert result["false_questions"] == 1
    assert result["missed_questions"] == 0


def test_omitted_boundary_word_is_not_an_invented_punctuation_match():
    result = punctuation.score_punctuation("Puoi fare deploy?", "Puoi fare?")
    assert result["unaligned_boundaries"] == 2
    assert result["by_mark"]["?"]["true_positive"] == 0
    assert result["by_mark"]["?"]["recall"] is None


def test_repeated_words_have_deterministic_rightmost_alignment():
    result = punctuation.score_punctuation("sì sì?", "sì?")
    assert result["by_mark"]["?"]["true_positive"] == 1
    assert result["precision"] == 1


def test_multiple_marks_and_unicode_apostrophes():
    result = punctuation.score_punctuation("L’API, funziona?!", "l'api funziona?")
    assert result["by_mark"]["?"]["true_positive"] == 1
    assert result["by_mark"][","]["false_negative"] == 1
    assert result["by_mark"]["!"]["false_negative"] == 1
    assert result["recall"] == pytest.approx(1 / 3)


def test_no_punctuation_does_not_report_perfect_punctuation_quality():
    result = punctuation.score_punctuation("ciao", "ciao")
    assert result["precision"] is None
    assert result["recall"] is None
    assert result["f1"] is None


def test_unreviewed_capture_is_distinct_from_labelled_silence():
    capture = punctuation.score_record({"reference_status": "unreviewed",
        "reference_raw": "", "reference_final": "", "is_silence": False}, "ciao")
    assert capture["scored"] is False
    assert capture["raw"] is None
    silence = punctuation.score_record({"reference_raw": "", "reference_final": "",
        "is_silence": True}, "ciao")
    assert silence["raw"]["false_positive"] is True
    assert silence["scored"] is True


def test_raw_only_reference_is_not_final_text_ground_truth():
    result = punctuation.score_record({"reference_status": "raw_only",
        "reference_raw": "come funziona", "reference_final": "", "is_silence": False}, "Come funziona?")
    assert result["raw"]["wer"] == 0
    assert result["final"] is None
    assert result["punctuation"] is None


def test_legacy_nonempty_references_remain_scorable():
    result = punctuation.score_record({"reference_raw": "come funziona",
        "reference_final": "Come funziona?", "is_silence": False}, "Come funziona.")
    assert result["raw"]["wer"] == 0
    assert result["punctuation"]["missed_questions"] == 1


def test_aggregate_sums_counts_instead_of_averaging_f1():
    a = punctuation.score_punctuation("Ciao? Bene?", "Ciao? Bene?")
    b = punctuation.score_punctuation("Come funziona?", "Come funziona.")
    result = punctuation.aggregate_punctuation([a, b])
    assert result["by_mark"]["?"]["true_positive"] == 2
    assert result["by_mark"]["?"]["recall"] == pytest.approx(2 / 3)
