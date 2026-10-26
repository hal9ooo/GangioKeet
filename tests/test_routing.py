from dictation_server.config import RoutingConfig
from dictation_server.routing import (
    TEMPERATURE,
    compute_max_tokens,
    contains_override,
    count_words,
    should_run_llm,
)

OVERRIDES = {"fast a pi": "FastAPI", "prossimo ve": "Proxmox VE"}


def test_temperature_is_deterministic():
    assert TEMPERATURE == 0.0


def test_compute_max_tokens():
    assert compute_max_tokens(10) == 24     # floor(10 * 1.25) + 12
    assert compute_max_tokens(100) == 137
    assert compute_max_tokens(0) == 12


def test_count_words_splits_on_whitespace():
    assert count_words("  ciao   come   stai ") == 3
    assert count_words("") == 0


def test_short_text_with_no_risk_skips_llm():
    cfg = RoutingConfig(fast_path_max_words=5,
                        self_correction_triggers=["anzi"])
    assert should_run_llm("ciao come stai", cfg, OVERRIDES) is False


def test_five_words_use_llm():
    cfg = RoutingConfig(fast_path_max_words=5,
                        self_correction_triggers=[])
    assert should_run_llm("ciao come stai tu oggi", cfg, {}) is True


def test_override_match_is_case_and_punctuation_insensitive():  # Review Focus #3
    cfg = RoutingConfig(fast_path_max_words=5,
                        self_correction_triggers=[])
    assert should_run_llm("FAST A PI.", cfg, OVERRIDES) is True
    assert should_run_llm("Prossimo ve,", cfg, OVERRIDES) is True


def test_contains_override_direct():
    assert contains_override("Ehm, FAST A PI!", OVERRIDES) is True
    assert contains_override("fast a pin", OVERRIDES) is False
    assert contains_override("niente da segnalare", OVERRIDES) is False


def test_trigger_word_forces_llm():
    cfg = RoutingConfig(fast_path_max_words=5,
                        self_correction_triggers=["o meglio"])
    assert should_run_llm("vado o meglio torno", cfg, {}) is True
    assert should_run_llm("vado, o meglio, torno", cfg, {}) is True


def test_trigger_does_not_match_inside_longer_words():
    cfg = RoutingConfig(fast_path_max_words=10,
                        self_correction_triggers=["anzi", "scusa"])
    assert should_run_llm("sei anziano", cfg, {}) is False
    assert should_run_llm("scusami tanto", cfg, {}) is False
    assert should_run_llm("(Anzi) ottimo", cfg, {}) is True


def test_override_does_not_match_inside_longer_phrase():
    cfg = RoutingConfig(fast_path_max_words=10,
                        self_correction_triggers=[])
    assert should_run_llm("fast a pine", cfg, OVERRIDES) is False


def test_short_text_with_canonical_term_still_skips_llm():
    cfg = RoutingConfig(fast_path_max_words=5,
                        self_correction_triggers=[])
    assert should_run_llm("usa docker ora", cfg, {}) is False


def test_short_override_still_forces_llm():
    cfg = RoutingConfig(fast_path_max_words=5,
                        self_correction_triggers=[])
    assert should_run_llm("fast a pi", cfg, OVERRIDES) is True
