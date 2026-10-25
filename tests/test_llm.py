import importlib

import mlx.core as mx
import pytest

from dictation_server.llm import LLMCleaner
from dictation_server.routing import compute_max_tokens


class FakeLLM(LLMCleaner):
    def __init__(self):
        super().__init__("fake")
        self.calls = []
        self.prefill_calls = 0
        self.cache_prefix_len = 0
        self.frozen = False

    def _encode(self, text):
        return [0] * len(text.split())

    def _system_tokens(self, system_prompt):
        return self._encode(system_prompt)

    def _prefill(self, sys_tokens):
        self.prefill_calls += 1
        self.cache_prefix_len = len(sys_tokens)

    def _generate(self, user_prompt, max_tokens):
        self.calls.append((user_prompt, max_tokens))
        return "  pulito "


def test_clean_uses_guardrails_and_strips():
    llm = FakeLLM()
    llm.freeze("system rules here")     # 3 parole
    out = llm.clean("ehm ciao buona sera", "code")
    assert out == "pulito"
    user_prompt, max_tokens = llm.calls[0]
    assert "<raw_dictation>ehm ciao buona sera</raw_dictation>" in user_prompt
    assert max_tokens == compute_max_tokens(
        len(llm._encode("ehm ciao buona sera"))
    )


def test_freeze_runs_once_across_requests():
    llm = FakeLLM()
    llm.freeze("system rules here")
    llm.clean("uno due tre quattro", "code")
    llm.clean("uno due tre", "code")
    assert llm.prefill_calls == 1
    assert llm.frozen
    assert llm.cache_prefix_len == 3


def test_clean_before_freeze_raises():
    llm = FakeLLM()
    with pytest.raises(RuntimeError, match="freeze"):
        llm.clean("ciao", "default")


def test_clean_rejects_thinking_output():
    class ThinkingLLM(FakeLLM):
        def _generate(self, user_prompt, max_tokens):
            return "  <think>ragiono</think> risposta  "

    llm = ThinkingLLM()
    llm.freeze("system rules")
    with pytest.raises(RuntimeError, match="think"):
        llm.clean("ciao", "chat")


class FakeTokenizer:
    eos_token_ids = {99}

    def __init__(self, full_tokens=(1, 2, 101, 102)):
        self.full_tokens = list(full_tokens)
        self.template_calls = []

    def apply_chat_template(self, messages, tokenize=True,
                            add_generation_prompt=True, enable_thinking=False):
        self.template_calls.append((messages, add_generation_prompt,
                                    enable_thinking))
        return list(self.full_tokens)

    def decode(self, tokens):
        return " " + " ".join(str(token) for token in tokens) + " "


def _prepared_llm():
    llm = LLMCleaner("fake")
    llm.model = object()
    llm.tokenizer = FakeTokenizer()
    llm._system_prompt = "sys"
    llm._sys_tokens = [1, 2]
    llm._frozen_cache = [{"tokens": [1, 2], "offset": 2}]
    llm.frozen = True
    llm.cache_prefix_len = 2
    return llm


def test_generate_copies_frozen_cache_per_request(monkeypatch):
    llm = _prepared_llm()
    seen = []

    def fake_generate_step(prompt, model, *, max_tokens, sampler, prompt_cache):
        seen.append((prompt.tolist(), prompt_cache))
        prompt_cache[0]["tokens"].append(999)
        prompt_cache[0]["offset"] += 1
        yield 42, None

    generate_module = importlib.import_module("mlx_lm.generate")
    monkeypatch.setattr(generate_module, "generate_step", fake_generate_step)

    assert llm._generate("<profile>chat</profile>", 5) == " 42 "
    assert llm._generate("<profile>chat</profile>", 5) == " 42 "
    assert llm._frozen_cache == [{"tokens": [1, 2], "offset": 2}]
    assert seen[0][1] is not llm._frozen_cache
    assert seen[1][1] is not seen[0][1]
    assert seen[0][0] == [101, 102]      # solo il suffisso variabile
    assert seen[1][0] == [101, 102]


def test_generate_error_does_not_contaminate_frozen_cache(monkeypatch):
    llm = _prepared_llm()
    calls = 0

    def flaky_generate_step(prompt, model, *, max_tokens, sampler, prompt_cache):
        nonlocal calls
        calls += 1
        prompt_cache[0]["tokens"].append(777)
        if calls == 1:
            raise RuntimeError("boom")
        yield 42, None

    generate_module = importlib.import_module("mlx_lm.generate")
    monkeypatch.setattr(generate_module, "generate_step", flaky_generate_step)

    with pytest.raises(RuntimeError, match="boom"):
        llm._generate("<profile>chat</profile>", 5)
    assert llm._frozen_cache == [{"tokens": [1, 2], "offset": 2}]
    assert llm._generate("<profile>chat</profile>", 5) == " 42 "


def test_generate_rejects_separated_prefix_mismatch(monkeypatch):
    llm = _prepared_llm()
    llm.tokenizer = FakeTokenizer(full_tokens=(9, 9, 101, 102))

    with pytest.raises(RuntimeError, match="prefix"):
        llm._generate("<profile>chat</profile>", 5)
