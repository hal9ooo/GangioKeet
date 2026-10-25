import numpy as np
import pytest

from dictation_server.audio import pcm_bytes_to_mlx_array


def test_int16_to_float32_normalization():
    raw = np.array([-32768, 0, 32767], dtype=np.int16).tobytes()
    out = np.array(pcm_bytes_to_mlx_array(raw))
    np.testing.assert_allclose(out, [-1.0, 0.0, 32767 / 32768.0], atol=1e-7)


def test_output_is_float32():
    raw = np.array([1, -1], dtype=np.int16).tobytes()
    out = pcm_bytes_to_mlx_array(raw)
    assert np.array(out).dtype == np.float32


def test_little_endian_byte_order_is_explicit():
    # Would catch a host-endian regression on a big-endian machine.
    out = np.array(pcm_bytes_to_mlx_array(b"\x01\x00\x02\x00"))
    np.testing.assert_allclose(out, [1 / 32768.0, 2 / 32768.0], atol=1e-7)


def test_empty_bytes_returns_empty_array():
    out = np.array(pcm_bytes_to_mlx_array(b""))
    assert out.shape == (0,)
    assert out.dtype == np.float32


def test_odd_byte_length_raises():
    with pytest.raises(ValueError):
        pcm_bytes_to_mlx_array(b"\x01\x02\x03")
