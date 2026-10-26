from __future__ import annotations

_HTTP_STATUS: dict[str, int] = {
    "unauthorized": 401,
    "busy": 503,
    "queue_timeout": 503,
    "decode_timeout": 504,
    "upload_timeout": 408,
    "processing_timeout": 504,
    "invalid_audio": 422,
    "payload_too_large": 413,
    "unsupported_parameter": 400,
    "decoder_unavailable": 503,
    "internal": 500,
}


class DictationFailure(RuntimeError):
    """Public failure type raised by the dictation pipeline.

    Carries a stable machine-readable ``code`` understood by
    :func:`http_status`, a human-readable ``message`` and a ``retryable``
    flag that clients use to decide whether retrying makes sense.
    """

    def __init__(self, code: str, message: str, retryable: bool = False):
        super().__init__(message)
        self.code = code
        self.message = message
        self.retryable = retryable


def http_status(code: str) -> int:
    """Map a failure code to its HTTP status; unknown codes map to 500."""
    return _HTTP_STATUS.get(code, 500)
