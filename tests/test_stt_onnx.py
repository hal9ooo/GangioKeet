import sys
import types

import numpy as np
import pytest

from dictation_server.stt_onnx import OnnxParakeetSTT
from tests.helpers import make_silence

FILES = (
    "encoder.int8.onnx",
    "decoder.int8.onnx",
    "joiner.int8.onnx",
    "tokens.txt",
)


class FakeStream:
    def __init__(self):
        self.sample_rate = None
        self.waveform = None
        self.result = types.SimpleNamespace(text="  ciao mondo  ")

    def accept_waveform(self, sample_rate, samples):
        self.sample_rate = sample_rate
        self.waveform = samples


class FakeRecognizer:
    def __init__(self, **kwargs):
        self.kwargs = kwargs
        self.streams = []

    def create_stream(self) -> FakeStream:
        stream = FakeStream()
        self.streams.append(stream)
        return stream

    def decode_stream(self, stream: FakeStream) -> None:
        self.decoded = stream


def _fake_sherpa(monkeypatch):
    module = types.ModuleType("sherpa_onnx")
    module.OfflineRecognizer = types.SimpleNamespace(
        from_transducer=FakeRecognizer
    )
    monkeypatch.setitem(sys.modules, "sherpa_onnx", module)
    return module


def _write_model(directory):
    for name in FILES:
        (directory / name).write_bytes(b"stub")


def test_missing_model_file_names_the_path(tmp_path):
    with pytest.raises(RuntimeError, match="encoder.int8.onnx"):
        OnnxParakeetSTT(tmp_path).load()


def test_load_maps_files_and_transducer_options(tmp_path, monkeypatch):
    _write_model(tmp_path)
    _fake_sherpa(monkeypatch)

    stt = OnnxParakeetSTT(tmp_path, num_threads=3)
    stt.load()

    kwargs = stt.recognizer.kwargs
    assert kwargs["encoder"] == str(tmp_path / "encoder.int8.onnx")
    assert kwargs["tokens"] == str(tmp_path / "tokens.txt")
    assert kwargs["num_threads"] == 3
    assert kwargs["model_type"] == "nemo_transducer"
    assert kwargs["decoding_method"] == "greedy_search"


def test_transcribe_feeds_float32_pcm16_and_strips_text(tmp_path, monkeypatch):
    _write_model(tmp_path)
    _fake_sherpa(monkeypatch)
    stt = OnnxParakeetSTT(tmp_path)
    stt.load()

    assert stt.transcribe(make_silence(0.1)) == "ciao mondo"

    stream = stt.recognizer.streams[0]
    assert stream.sample_rate == 16_000
    assert stream.waveform.dtype == np.float32
    assert stream.waveform.shape == (1600,)
    assert stt.recognizer.decoded is stream


def test_transcribe_before_load_raises(tmp_path):
    with pytest.raises(RuntimeError, match="load"):
        OnnxParakeetSTT(tmp_path).transcribe(b"\x00\x00")
