import mlx.core as mx
import numpy as np

SAMPLE_RATE = 16_000
INT16_SCALE = 32_768.0


def pcm_bytes_to_mlx_array(data: bytes) -> "mx.array":
    """Convert little-endian PCM16 mono bytes to normalized float32 MLX array."""
    if len(data) % 2:
        raise ValueError("PCM16 data must contain an even number of bytes")
    pcm = np.frombuffer(data, dtype="<i2")
    normalized = pcm.astype(np.float32) / INT16_SCALE
    return mx.array(normalized)
