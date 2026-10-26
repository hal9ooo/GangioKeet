import pytest

from dictation_server.errors import DictationFailure, http_status

EXPECTED_HTTP_STATUS = {
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


def test_dictation_failure_exposes_code_message_retryable():
    err = DictationFailure("busy", "at capacity", retryable=True)
    assert isinstance(err, RuntimeError)
    assert err.code == "busy"
    assert err.message == "at capacity"
    assert err.retryable is True


def test_dictation_failure_retryable_defaults_false():
    err = DictationFailure("internal", "boom")
    assert err.retryable is False


@pytest.mark.parametrize("code,expected", sorted(EXPECTED_HTTP_STATUS.items()))
def test_error_http_mapping(code, expected):
    assert http_status(code) == expected


def test_unknown_error_code_maps_to_500():
    assert http_status("not-a-real-code") == 500
