import pytest

from dictation_server.output_policy import validate_output


@pytest.mark.parametrize(
    "output",
    [
        "<think>ragiono</think> risposta",
        "</think> risposta",
        "<raw_dictation>x</raw_dictation>",
        "<profile>chat</profile>",
        "<glossary>x</glossary>",
        "<|im_start|>x",
        "<|channel>thought",
        "<channel|>x",
        "<|turn>model",
        "<turn|>x",
        "[start_header_id]x",
        "[start_of_turn]x",
    ],
)
def test_prompt_and_thinking_markers_are_rejected(output):
    assert "prompt_or_thinking_tags" in validate_output("ciao", output)


def test_empty_output_is_rejected():
    assert "empty_output" in validate_output("ciao", "   \n")


def test_invalid_characters_are_rejected():
    assert "invalid_text" in validate_output("ciao", "ok\x00")
    assert "invalid_text" in validate_output("ciao", "ok\ufffd")


def test_expanded_output_is_rejected():
    assert "expanded_output" in validate_output("ciao", "x" * 501)


def test_normal_output_is_accepted():
    assert validate_output("come funziona", "Come funziona?") == []
