import numpy as np

SAMPLE_RATE = 16_000
INT16_SCALE = 32_768.0


def pcm_bytes_to_float32(data: bytes) -> np.ndarray:
    """Convert little-endian PCM16 mono bytes to normalized float32 samples."""
    if len(data) % 2:
        raise ValueError("PCM16 data must contain an even number of bytes")
    pcm = np.frombuffer(data, dtype="<i2")
    return pcm.astype(np.float32) / INT16_SCALE


def pcm_bytes_to_mlx_array(data: bytes) -> "mx.array":
    """Convert little-endian PCM16 mono bytes to normalized float32 MLX array."""
    import mlx.core as mx

    return mx.array(pcm_bytes_to_float32(data))
