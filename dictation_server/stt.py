class ParakeetSTT:
    """Thin in-memory wrapper around parakeet-mlx (macOS engine)."""

    def __init__(self, model_id: str):
        self.model_id = model_id
        self.model = None

    def load(self) -> None:
        import mlx.core as mx
        from mlx.utils import tree_flatten
        from parakeet_mlx import from_pretrained

        model = from_pretrained(self.model_id, dtype=mx.bfloat16)
        model.encoder.set_attention_model("rel_pos_local_attn", (256, 256))

        weights = tree_flatten(model.parameters())
        mx.eval([array for _, array in weights])
        non_bfloat16 = [
            (name, array.dtype)
            for name, array in weights
            if array.dtype in (mx.float16, mx.float32)
        ]
        if non_bfloat16:
            raise RuntimeError(
                "STT weights must be bfloat16, found non-bfloat16 floating "
                f"weights: {non_bfloat16}"
            )

        pos_enc = getattr(model.encoder, "pos_enc", None)
        if (
            getattr(pos_enc, "left_context", None),
            getattr(pos_enc, "right_context", None),
        ) != (256, 256):
            raise RuntimeError("STT encoder is not using 256x256 local attention")

        self.model = model

    def transcribe(self, pcm: bytes) -> str:
        if self.model is None:
            raise RuntimeError("load() must be called before transcribe()")

        from parakeet_mlx import DecodingConfig
        from parakeet_mlx.parakeet import get_logmel

        from .audio import pcm_bytes_to_mlx_array

        audio = pcm_bytes_to_mlx_array(pcm)
        mel = get_logmel(audio, self.model.preprocessor_config)
        results = self.model.generate(
            mel, decoding_config=DecodingConfig()
        )
        return results[0].text.strip() if results else ""
