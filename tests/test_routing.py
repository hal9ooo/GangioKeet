from dictation_server.routing import TEMPERATURE, estimate_max_tokens


def test_temperature_is_deterministic():
    assert TEMPERATURE == 0.0


def test_estimate_max_tokens_uses_windows_formula():
    assert estimate_max_tokens("come funziona") == 128
    assert estimate_max_tokens("x" * 100) == 264
    assert estimate_max_tokens("x" * 5000) == 4096
