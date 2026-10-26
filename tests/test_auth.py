import json
import logging
import secrets

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from dictation_server.admission import (
    AdmissionController,
    AdmissionSnapshot,
)
from dictation_server.auth import ApiKeyPolicy, BearerAuthMiddleware
from dictation_server.config import load_config
from dictation_server.errors import DictationFailure
from dictation_server.pipeline import Pipeline
from dictation_server.server import create_app
from tests.fakes import FakeLLM, FakeSTT
from tests.helpers import make_silence

API_KEY_ENV = "TEST_DICTATION_API_KEY"
TOKEN = "expected-secret-token"
UNAUTHORIZED_HEADERS = [
    pytest.param(None, id="missing"),
    pytest.param(
        {"Authorization": "Bearer wrong-secret-token"}, id="wrong"
    ),
    pytest.param(
        {"Authorization": "Basic expected-secret-token"}, id="bad-scheme"
    ),
    pytest.param({"Authorization": "Bearer"}, id="no-token"),
    pytest.param(
        {"Authorization": "Bearer one two"}, id="multiple-tokens"
    ),
]


def make_policy(token: str = TOKEN) -> ApiKeyPolicy:
    return ApiKeyPolicy.from_environment(
        API_KEY_ENV, {API_KEY_ENV: token}
    )


def make_client(
    cfg,
    admission=None,
    stt=None,
    llm=None,
):
    stt = stt if stt is not None else FakeSTT()
    llm = llm if llm is not None else FakeLLM()
    app = create_app(
        cfg,
        Pipeline(stt, llm, cfg),
        admission=admission,
        auth=make_policy(),
    )
    return TestClient(app), stt, llm


def auth_headers(token: str = TOKEN) -> dict[str, str]:
    return {"Authorization": f"Bearer {token}"}


def post_pcm(client, headers=None):
    return client.post(
        "/v1/audio/transcriptions?format=pcm",
        files={
            "file": (
                "d.pcm",
                make_silence(0.2),
                "application/octet-stream",
            )
        },
        headers=headers or {},
    )


def send_start(ws, **overrides):
    payload = {"type": "start", "app_name": "x", "window_title": "y"}
    payload.update(overrides)
    ws.send_json(payload)


# --- ApiKeyPolicy ---------------------------------------------------------


def test_auth_disabled_when_variable_is_not_configured():
    policy = ApiKeyPolicy.from_environment(None, {API_KEY_ENV: TOKEN})

    assert policy.enabled is False
    policy.authorize(None)
    policy.authorize("not even a bearer header")


@pytest.mark.parametrize(
    "environ",
    [{}, {API_KEY_ENV: ""}],
    ids=["missing", "empty"],
)
def test_configured_variable_must_have_a_value(environ):
    with pytest.raises(ValueError) as info:
        ApiKeyPolicy.from_environment(API_KEY_ENV, environ)

    message = str(info.value)
    assert API_KEY_ENV in message
    assert TOKEN not in message


@pytest.mark.parametrize(
    "header",
    [
        f"Bearer {TOKEN}",
        f"bearer {TOKEN}",
        f"BEARER {TOKEN}",
        f"BeArEr {TOKEN}",
    ],
)
def test_bearer_scheme_is_case_insensitive(header):
    make_policy().authorize(header)


@pytest.mark.parametrize(
    "header",
    [
        None,
        "",
        TOKEN,
        "Bearer",
        "Bearer ",
        "Bearer wrong-token",
        f"Basic {TOKEN}",
        f"Bearer {TOKEN} extra",
        f"Bearer  {TOKEN}",
        f"Bearer {TOKEN} ",
        f" Bearer {TOKEN}",
        f"Bearer {TOKEN}-non-ascii-\u00e9",
    ],
)
def test_missing_malformed_or_wrong_header_is_unauthorized(header):
    with pytest.raises(DictationFailure) as info:
        make_policy().authorize(header)

    failure = info.value
    assert failure.code == "unauthorized"
    assert failure.message == "unauthorized"
    assert failure.retryable is False
    assert TOKEN not in str(failure)
    assert TOKEN not in repr(failure)


def test_authorize_uses_secrets_compare_digest(monkeypatch):
    calls = []
    real_compare_digest = secrets.compare_digest

    def spy(left, right):
        calls.append((left, right))
        return real_compare_digest(left, right)

    monkeypatch.setattr(
        "dictation_server.auth.secrets.compare_digest", spy
    )
    policy = make_policy()

    policy.authorize(f"Bearer {TOKEN}")
    with pytest.raises(DictationFailure):
        policy.authorize("Bearer wrong-token")

    assert len(calls) == 2
    assert calls[0] == (TOKEN, TOKEN)
    assert calls[1] == ("wrong-token", TOKEN)


def test_policy_repr_and_errors_do_not_leak_tokens(caplog):
    received = "received-secret-token"
    policy = make_policy()

    assert TOKEN not in repr(policy)
    assert received not in repr(policy)

    with caplog.at_level(logging.DEBUG):
        with pytest.raises(DictationFailure) as info:
            policy.authorize(f"Bearer {received}")

    rendered = (
        f"{info.value!s} {info.value!r} {info.value.args!r} "
        f"{info.value.code} {info.value.message}"
    )
    assert TOKEN not in rendered
    assert received not in rendered
    assert TOKEN not in caplog.text
    assert received not in caplog.text


# --- ASGI middleware ------------------------------------------------------


class SendCollector:
    def __init__(self):
        self.messages = []

    async def __call__(self, message):
        self.messages.append(message)

    def response(self):
        start = next(
            message
            for message in self.messages
            if message["type"] == "http.response.start"
        )
        body = b"".join(
            message.get("body", b"")
            for message in self.messages
            if message["type"] == "http.response.body"
        )
        headers = {
            key.decode().lower(): value.decode()
            for key, value in start.get("headers", [])
        }
        content_type = headers.get("content-type", "")
        parsed = (
            json.loads(body)
            if body and "application/json" in content_type
            else body or None
        )
        return start["status"], headers, parsed


class RecordingApp:
    def __init__(self):
        self.calls = []

    async def __call__(self, scope, receive, send):
        self.calls.append(scope)
        message = await receive()
        self.body = message.get("body", b"")
        await send(
            {"type": "http.response.start", "status": 200, "headers": []}
        )
        await send({"type": "http.response.body", "body": b"ok"})


def make_scope(path="/v1/audio/transcriptions", headers=None):
    return {
        "type": "http",
        "asgi": {"version": "3.0"},
        "http_version": "1.1",
        "method": "POST",
        "scheme": "http",
        "path": path,
        "raw_path": path.encode(),
        "query_string": b"",
        "root_path": "",
        "headers": list(headers or []),
        "client": ("127.0.0.1", 12345),
        "server": ("testserver", 80),
        "state": {},
    }


def fixed_receive(body=b"body"):
    calls = []

    async def receive():
        calls.append(True)
        return {
            "type": "http.request",
            "body": body,
            "more_body": False,
        }

    receive.calls = calls
    return receive


@pytest.mark.asyncio
async def test_http_middleware_rejects_before_body_or_inner_app():
    inner = RecordingApp()
    middleware = BearerAuthMiddleware(inner, make_policy())
    receive = fixed_receive()
    send = SendCollector()

    await middleware(
        make_scope(headers=[(b"authorization", b"Bearer wrong")]),
        receive,
        send,
    )

    status, headers, body = send.response()
    assert status == 401
    assert headers["www-authenticate"] == "Bearer"
    assert body == {
        "detail": {
            "code": "unauthorized",
            "message": "unauthorized",
            "retryable": False,
        }
    }
    assert inner.calls == []
    assert receive.calls == []
    assert TOKEN not in json.dumps(body)


@pytest.mark.asyncio
async def test_http_middleware_allows_health_without_a_token():
    inner = RecordingApp()
    middleware = BearerAuthMiddleware(inner, make_policy())
    send = SendCollector()

    await middleware(
        make_scope(path="/health"), fixed_receive(), send
    )

    assert send.response()[0] == 200
    assert len(inner.calls) == 1


@pytest.mark.asyncio
async def test_http_middleware_passes_authorized_and_disabled_requests():
    inner = RecordingApp()
    middleware = BearerAuthMiddleware(inner, make_policy())
    await middleware(
        make_scope(
            headers=[(b"authorization", f"Bearer {TOKEN}".encode())]
        ),
        fixed_receive(b"payload"),
        SendCollector(),
    )
    assert len(inner.calls) == 1
    assert inner.body == b"payload"

    disabled_inner = RecordingApp()
    disabled = BearerAuthMiddleware(disabled_inner, ApiKeyPolicy())
    await disabled(make_scope(), fixed_receive(), SendCollector())
    assert len(disabled_inner.calls) == 1


# --- app integration -----------------------------------------------------


@pytest.fixture
def cfg():
    return load_config("config.yaml")


def test_create_app_exposes_disabled_auth_by_default(cfg):
    app = create_app(cfg, Pipeline(FakeSTT(), FakeLLM(), cfg))

    assert isinstance(app.state.auth, ApiKeyPolicy)
    assert app.state.auth.enabled is False


def test_auth_middleware_wraps_the_upload_limiter(cfg):
    app = create_app(
        cfg,
        Pipeline(FakeSTT(), FakeLLM(), cfg),
        auth=make_policy(),
    )

    middleware_names = [
        middleware.cls.__name__ for middleware in app.user_middleware
    ]
    assert middleware_names[0] == "BearerAuthMiddleware"
    assert middleware_names[1] == "UploadLimitMiddleware"


def test_auth_disabled_preserves_legacy_rest_and_ws(cfg):
    stt, llm = FakeSTT(), FakeLLM()
    app = create_app(cfg, Pipeline(stt, llm, cfg))
    client = TestClient(app)

    response = post_pcm(client)
    assert response.status_code == 200
    assert len(stt.seen) == 1
    assert len(llm.calls) == 1

    with client.websocket_connect("/ws/dictate") as ws:
        send_start(ws)
        ws.send_bytes(make_silence(0.2))
        ws.send_json({"type": "stop"})
        assert ws.receive_json()["type"] == "result"


def test_rest_accepts_the_correct_key(cfg):
    client, stt, llm = make_client(cfg)

    response = post_pcm(client, auth_headers())

    assert response.status_code == 200
    assert "text" in response.json()
    assert len(stt.seen) == 1
    assert len(llm.calls) == 1


@pytest.mark.parametrize("headers", UNAUTHORIZED_HEADERS)
def test_rest_rejects_unauthorized_headers(cfg, headers):
    client, stt, llm = make_client(cfg)

    response = post_pcm(client, headers)

    assert response.status_code == 401
    assert response.json() == {
        "detail": {
            "code": "unauthorized",
            "message": "unauthorized",
            "retryable": False,
        }
    }
    assert TOKEN not in response.text
    assert stt.seen == []
    assert llm.calls == []
    assert client.app.state.admission.snapshot() == AdmissionSnapshot(
        0, 0
    )


def test_rest_unauthorized_is_rejected_before_body_limits(cfg):
    cfg.limits.max_audio_bytes = 4
    cfg.limits.max_http_body_bytes = 4
    cfg.limits.max_buffered_audio_bytes = 8
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    held = admission.acquire()
    client, stt, llm = make_client(cfg, admission=admission)

    response = client.post(
        "/v1/audio/transcriptions",
        content=b"x" * 16,
        headers={"content-type": "multipart/form-data; boundary=x"},
    )

    assert response.status_code == 401
    assert response.json()["detail"]["code"] == "unauthorized"
    assert admission.snapshot() == AdmissionSnapshot(1, 0)
    assert stt.seen == []
    assert llm.calls == []
    held.close()


def test_rest_unauthorized_skips_decoder_and_engines(cfg, monkeypatch):
    def forbidden_decode(*_args, **_kwargs):
        raise AssertionError("decoder must not run")

    monkeypatch.setattr(
        "dictation_server.server.decode_upload", forbidden_decode
    )
    client, stt, llm = make_client(cfg)

    response = post_pcm(client)

    assert response.status_code == 401
    assert stt.seen == []
    assert llm.calls == []


def test_rest_unauthorized_does_not_parse_multipart(cfg, monkeypatch):
    async def forbidden_parse(*_args, **_kwargs):
        raise AssertionError("multipart parser must not run")

    monkeypatch.setattr(
        "starlette.formparsers.MultiPartParser.parse", forbidden_parse
    )
    client, _, _ = make_client(cfg)

    response = post_pcm(client)

    assert response.status_code == 401


def test_health_is_public_and_future_ready_is_protected(cfg):
    client, _, _ = make_client(cfg)

    health = client.get("/health")
    assert health.status_code == 200
    assert health.json() == {"status": "ok"}

    assert client.get("/ready").status_code == 401
    authorized = client.get("/ready", headers=auth_headers())
    assert authorized.status_code == 404


def test_ws_accepts_the_correct_key(cfg):
    client, stt, llm = make_client(cfg)

    with client.websocket_connect(
        "/ws/dictate", headers=auth_headers()
    ) as ws:
        send_start(ws)
        ws.send_bytes(make_silence(0.2))
        ws.send_json({"type": "stop"})
        result = ws.receive_json()

    assert result["type"] == "result"
    assert len(stt.seen) == 1
    assert len(llm.calls) == 1


@pytest.mark.parametrize("headers", UNAUTHORIZED_HEADERS)
def test_ws_rejects_unauthorized_headers(cfg, headers):
    client, stt, llm = make_client(cfg)
    websocket_headers = dict(headers or {})

    with client.websocket_connect(
        "/ws/dictate", headers=websocket_headers
    ) as ws:
        error = ws.receive_json()
        assert error == {
            "type": "error",
            "code": "unauthorized",
            "message": "unauthorized",
            "retryable": False,
        }
        with pytest.raises(WebSocketDisconnect) as info:
            ws.receive_json()
        assert info.value.code == 1008

    assert TOKEN not in json.dumps(error)
    assert stt.seen == []
    assert llm.calls == []
    assert client.app.state.admission.snapshot() == AdmissionSnapshot(
        0, 0
    )
