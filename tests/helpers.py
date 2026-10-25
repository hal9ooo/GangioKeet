import numpy as np


def make_silence(duration_s: float = 1.0, sample_rate: int = 16000) -> bytes:
    """PCM16 mono little-endian silence."""
    samples = int(duration_s * sample_rate)
    return np.zeros(samples, dtype="<i2").tobytes()
