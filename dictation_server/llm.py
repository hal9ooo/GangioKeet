import copy
import time

import mlx.core as mx

from .config import GlossaryConfig
from .engines import LLMDeadlineExceeded
from .prompt import Profile, build_user_prompt
from .routing import TEMPERATURE, compute_max_tokens

__all__ = ["LLMCleaner", "LLMDeadlineExceeded"]


class LLMCleaner:
    """mlx-lm cleaner with a frozen system-prompt KV cache."""

    def __init__(
        self, model_id: str, glossary: GlossaryConfig | None = None
    ):
        self.model_id = model_id
        self.glossary = glossary or GlossaryConfig(
            canonical_terms=[], phonetic_overrides={}
        )
        self.model = None
        self.tokenizer = None
        self.frozen = False
        self.cache_prefix_len = 0
        self.prefill_calls = 0
        self.last_ttft_ms = 0.0
        self._frozen_cache = None
        self._system_prompt = ""
        self._sys_tokens: list[int] = []
        self._deadline: float | None = None

    def load(self) -> None:
        from mlx_lm import load

        self.model, self.tokenizer = load(self.model_id)

    def _encode(self, text: str) -> list[int]:
        return list(self.tokenizer.encode(text, add_special_tokens=False))

    def _system_tokens(self, system_prompt: str) -> list[int]:
        return list(
            self.tokenizer.apply_chat_template(
                [{"role": "system", "content": system_prompt}],
                tokenize=True,
                add_generation_prompt=False,
                enable_thinking=False,
            )
        )

    def freeze(self, system_prompt: str) -> None:
        self.frozen = False
        self._frozen_cache = None
        self.cache_prefix_len = 0
        self._system_prompt = system_prompt
        self._sys_tokens = list(self._system_tokens(system_prompt))
        self._prefill(self._sys_tokens)
        self.frozen = True

    def _prefill(self, sys_tokens: list[int]) -> None:
        from mlx_lm.generate import make_prompt_cache

        cache = make_prompt_cache(self.model)
        self.model(mx.array(sys_tokens)[None], cache=cache)
        mx.eval([entry.state for entry in cache])

        for entry in cache:
            offset = getattr(entry, "offset", None)
            if offset is not None and offset != len(sys_tokens):
                raise RuntimeError(
                    "KV cache prefix prefill did not consume the whole "
                    f"system prompt ({offset} != {len(sys_tokens)})"
                )

        self._frozen_cache = cache
        self.cache_prefix_len = len(sys_tokens)
        self.prefill_calls += 1

    def _user_suffix(self, user_prompt: str) -> list[int]:
        full_tokens = list(
            self.tokenizer.apply_chat_template(
                [
                    {"role": "system", "content": self._system_prompt},
                    {"role": "user", "content": user_prompt},
                ],
                tokenize=True,
                add_generation_prompt=True,
                enable_thinking=False,
            )
        )
        prefix = list(self._sys_tokens)
        if full_tokens[:len(prefix)] != prefix:
            raise RuntimeError(
                "system prefix does not match the rendered chat template"
            )
        suffix = full_tokens[len(prefix):]
        if not suffix:
            raise RuntimeError("chat template produced an empty user suffix")
        return suffix

    def _check_deadline(self) -> None:
        if self._deadline is not None and time.monotonic() >= self._deadline:
            raise LLMDeadlineExceeded("LLM generation deadline exceeded")

    def _generate(self, user_prompt: str, max_tokens: int) -> str:
        from mlx_lm.generate import generate_step
        from mlx_lm.sample_utils import make_sampler

        if self._frozen_cache is None:
            raise RuntimeError("freeze() must be called before generation")

        suffix = self._user_suffix(user_prompt)
        cache = copy.deepcopy(self._frozen_cache)
        sampler = make_sampler(temp=TEMPERATURE)

        generated: list[int] = []
        self.last_ttft_ms = 0.0
        # Diagnostic TTFT: excludes the per-request cache clone and chat
        # template rendering above, which remain counted in llm_ms.
        started = time.perf_counter()
        self._check_deadline()
        for token, _logprobs in generate_step(
            mx.array(suffix),
            self.model,
            max_tokens=max_tokens,
            sampler=sampler,
            prompt_cache=cache,
        ):
            self._check_deadline()
            if self.last_ttft_ms == 0.0:
                self.last_ttft_ms = (
                    time.perf_counter() - started
                ) * 1000.0
            if token in self.tokenizer.eos_token_ids:
                break
            if len(generated) >= max_tokens:
                break
            generated.append(token)
        return self.tokenizer.decode(generated)

    def clean(
        self,
        raw_text: str,
        profile: Profile,
        *,
        deadline: float | None = None,
    ) -> str:
        if not self.frozen:
            raise RuntimeError("freeze() must be called before clean()")

        self._deadline = deadline
        try:
            self._check_deadline()
            user_prompt = build_user_prompt(
                raw_text, profile, self.glossary
            )
            n_input_tokens = len(self._encode(raw_text))
            output = self._generate(
                user_prompt, compute_max_tokens(n_input_tokens)
            ).strip()
        finally:
            self._deadline = None

        if "<think>" in output or "</think>" in output:
            raise RuntimeError("LLM output contains thinking tags")
        return output
