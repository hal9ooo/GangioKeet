import mlx.core as mx
import pytest

from dictation_server.audio import pcm_bytes_to_mlx_array
from dictation_server.stt import ParakeetSTT
from tests.helpers import make_silence


class FakeEncoder:
    def __init__(self):
        self.attention = None
        self.pos_enc = None

    def set_attention_model(self, name, context_size):
        self.attention = (name, context_size)
        self.pos_enc = type(
            "FakePosEnc", (), {"left_context": context_size[0],
                               "right_context": context_size[1]}
        )()


class FakeModel:
    def __init__(self, weight_dtype=mx.bfloat16):
        self.encoder = FakeEncoder()
        self.preprocessor_config = object()
        self._weight_dtype = weight_dtype

    def parameters(self):
        return {
            "weight": mx.zeros((2,), dtype=self._weight_dtype),
            "step": mx.array([1], dtype=mx.int32),
        }


def test_protocol_shapes():
    stt = ParakeetSTT(model_id="mlx-community/parakeet-tdt-0.6b-v3")
    assert callable(stt.load)
    assert callable(stt.transcribe)


def test_make_silence_size():
    assert len(make_silence(1.0)) == 16000 * 2  # 1 s, 16 kHz, 16-bit


def test_make_silence_is_pcm16_zero_and_honors_sample_rate():
    data = make_silence(0.5, sample_rate=8000)
    assert len(data) == 8000
    assert data == b"\x00" * 8000


def test_load_uses_bfloat16_and_local_attention(monkeypatch):
    import parakeet_mlx

    model = FakeModel()
    calls = {}

    def fake_from_pretrained(model_id, *, dtype):
        calls["model_id"] = model_id
        calls["dtype"] = dtype
        return model

    monkeypatch.setattr(parakeet_mlx, "from_pretrained", fake_from_pretrained)

    stt = ParakeetSTT("fake-model")
    stt.load()

    assert calls == {"model_id": "fake-model", "dtype": mx.bfloat16}
    assert model.encoder.attention == ("rel_pos_local_attn", (256, 256))
    assert stt.model is model


def test_load_rejects_non_bfloat16_float_weights(monkeypatch):
    import parakeet_mlx

    model = FakeModel(weight_dtype=mx.float32)
    monkeypatch.setattr(parakeet_mlx, "from_pretrained",
                        lambda model_id, *, dtype: model)

    stt = ParakeetSTT("fake-model")
    with pytest.raises(RuntimeError, match="bfloat16"):
        stt.load()


def test_transcribe_uses_low_level_get_logmel_and_generate(monkeypatch):
    import parakeet_mlx
    from parakeet_mlx import DecodingConfig

    model = FakeModel()
    stt = ParakeetSTT("fake-model")
    stt.model = model
    audio = pcm_bytes_to_mlx_array(make_silence(0.1))
    seen = {}

    def fake_get_logmel(x, config):
        seen["audio"] = x
        seen["config"] = config
        return "mel"

    class FakeResult:
        text = "  ciao mondo  "

    def fake_generate(mel, *, decoding_config):
        seen["mel"] = mel
        seen["decoding"] = decoding_config
        return [FakeResult()]

    monkeypatch.setattr("parakeet_mlx.parakeet.get_logmel", fake_get_logmel)
    model.generate = fake_generate

    assert stt.transcribe(audio) == "ciao mondo"
    assert seen["mel"] == "mel"
    assert seen["config"] is model.preprocessor_config
    assert isinstance(seen["decoding"], DecodingConfig)
    assert seen["audio"].shape == (1600,)


def test_transcribe_before_load_raises():
    stt = ParakeetSTT("fake-model")
    with pytest.raises(RuntimeError, match="load"):
        stt.transcribe(pcm_bytes_to_mlx_array(make_silence(0.1)))
