class NullLLM:
    """Passthrough cleaner for STT engines that already punctuate.

    Parakeet v3 emits punctuation and casing, so the Windows endpoint skips
    llama.cpp/Qwen entirely. Implementing the ``LLM`` protocol keeps the
    pipeline, routing and metrics identical to the macOS endpoint.
    """

    def __init__(self, model_id: str | None = None):
        self.model_id = model_id
        self.system_prompt = ""

    def load(self) -> None:
        pass

    def freeze(self, system_prompt: str) -> None:
        self.system_prompt = system_prompt

    def clean(
        self,
        text: str,
        profile: str = "default",
        *,
        deadline: float | None = None,
    ) -> str:
        return text
