import time

from dictation_server.llm_null import NullLLM


def test_null_llm_returns_text_unchanged():
    llm = NullLLM()
    llm.load()
    llm.freeze("system prompt")

    assert llm.clean("Ciao mondo.", "default") == "Ciao mondo."


def test_null_llm_ignores_profile_and_deadline():
    llm = NullLLM()

    assert (
        llm.clean("Test.", "email", deadline=time.monotonic() - 5.0)
        == "Test."
    )
