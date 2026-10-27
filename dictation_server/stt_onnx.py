import logging
from pathlib import Path

from .audio import SAMPLE_RATE, pcm_bytes_to_float32

logger = logging.getLogger(__name__)

_MODEL_FILES = (
    "encoder.int8.onnx",
    "decoder.int8.onnx",
    "joiner.int8.onnx",
    "tokens.txt",
)


class OnnxParakeetSTT:
    """Parakeet transducer (int8) running on CPU through sherpa-onnx.

    Same model family as the macOS ``parakeet-mlx`` engine, so transcripts
    stay comparable between endpoints.
    """

    def __init__(self, model_dir: str | Path, num_threads: int = 4):
        self.model_dir = Path(model_dir)
        self.num_threads = num_threads
        self.recognizer = None

    def _file(self, name: str) -> str:
        path = self.model_dir / name
        if not path.is_file():
            present = sorted(p.name for p in self.model_dir.glob("*"))
            raise RuntimeError(
                f"ONNX STT model file missing: {path} (present: {present})"
            )
        return str(path)

    def load(self) -> None:
        import sherpa_onnx

        self.recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
            encoder=self._file("encoder.int8.onnx"),
            decoder=self._file("decoder.int8.onnx"),
            joiner=self._file("joiner.int8.onnx"),
            tokens=self._file("tokens.txt"),
            num_threads=self.num_threads,
            decoding_method="greedy_search",
            model_type="nemo_transducer",
        )
        logger.info(
            "onnx stt loaded model_dir=%s threads=%d",
            self.model_dir,
            self.num_threads,
        )

    def transcribe(self, pcm: bytes) -> str:
        if self.recognizer is None:
            raise RuntimeError("load() must be called before transcribe()")

        stream = self.recognizer.create_stream()
        stream.accept_waveform(SAMPLE_RATE, pcm_bytes_to_float32(pcm))
        self.recognizer.decode_stream(stream)
        return stream.result.text.strip()
