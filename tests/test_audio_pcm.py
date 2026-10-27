import numpy as np
import pytest

from dictation_server.audio import pcm_bytes_to_float32


def test_int16_to_float32_normalization():
    raw = np.array([-32768, 0, 32767], dtype=np.int16).tobytes()
    np.testing.assert_allclose(
        pcm_bytes_to_float32(raw), [-1.0, 0.0, 32767 / 32768.0], atol=1e-7
    )


def test_output_is_float32_and_own_buffer():
    raw = np.array([1, -1], dtype=np.int16).tobytes()
    out = pcm_bytes_to_float32(raw)
    assert isinstance(out, np.ndarray)
    assert out.dtype == np.float32
    assert out.flags.writeable


def test_little_endian_byte_order_is_explicit():
    out = pcm_bytes_to_float32(b"\x01\x00\x02\x00")
    np.testing.assert_allclose(out, [1 / 32768.0, 2 / 32768.0], atol=1e-7)


def test_empty_bytes_returns_empty_array():
    out = pcm_bytes_to_float32(b"")
    assert out.shape == (0,)
    assert out.dtype == np.float32


def test_odd_byte_length_raises():
    with pytest.raises(ValueError, match="even number of bytes"):
        pcm_bytes_to_float32(b"\x01\x02\x03")
