import os
import resource
import subprocess
import sys

import mlx.core as mx
import pytest
from mlx.utils import tree_flatten

from dictation_server.audio import pcm_bytes_to_mlx_array
from dictation_server.stt import ParakeetSTT
from tests.helpers import make_silence

pytestmark = [
    pytest.mark.integration,
    pytest.mark.skipif(
        os.environ.get("RUN_REAL_MODELS") != "1", reason="real models"
    ),
]

MODEL_ID = "mlx-community/parakeet-tdt-0.6b-v3"


def _peak_rss_mb() -> float:
    peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    if sys.platform != "darwin":
        peak *= 1024
    return peak / (1024 * 1024)


def _current_rss_mb() -> float:
    output = subprocess.check_output(
        ["ps", "-o", "rss=", "-p", str(os.getpid())], text=True
    )
    return int(output.strip()) / 1024


@pytest.fixture(scope="module")
def stt() -> ParakeetSTT:
    instance = ParakeetSTT(MODEL_ID)
    before_peak = _peak_rss_mb()
    before_current = _current_rss_mb()
    instance.load()
    print(f"\nRSS before load: current {before_current:.1f} MB, "
          f"peak {before_peak:.1f} MB")
    print(f"RSS after load: current {_current_rss_mb():.1f} MB, "
          f"peak {_peak_rss_mb():.1f} MB")
    return instance


def test_loaded_weights_are_bfloat16_with_local_attention(stt):
    weights = tree_flatten(stt.model.parameters())
    floating = [
        (name, array.dtype)
        for name, array in weights
        if array.dtype in (mx.float16, mx.float32)
    ]
    assert not floating
    assert any(array.dtype == mx.bfloat16 for _, array in weights)
    assert stt.model.encoder.pos_enc.left_context == 256
    assert stt.model.encoder.pos_enc.right_context == 256


def test_transcribe_silence_returns_string(stt):
    text = stt.transcribe(pcm_bytes_to_mlx_array(make_silence(1.0)))
    assert isinstance(text, str)
