import asyncio
import json
import time

import httpx
import pytest
from fastapi import FastAPI, File, UploadFile
from starlette.requests import Request

from dictation_server.admission import (
    AdmissionController,
    AdmissionSnapshot,
)
from dictation_server.http_limits import UploadLimitMiddleware

PATH = "/v1/audio/transcriptions"
MIB = 1024 * 1024


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


class DummyApp:
    def __init__(
        self,
        *,
        reserve: int = 0,
        transfer: bool = False,
        raise_error: bool = False,
    ):
        self.calls = []
        self.bodies = []
        self.lease = None
        self.reserve = reserve
        self.transfer = transfer
        self.raise_error = raise_error

    async def __call__(self, scope, receive, send):
        self.calls.append(scope)
        lease = scope["state"].get("audio_lease")
        self.lease = lease
        if lease is not None and self.reserve:
            lease.reserve(self.reserve)
        if lease is not None and self.transfer:
            lease.transfer()
        if self.raise_error:
            raise RuntimeError("handler exploded")
        message = await receive()
        self.bodies.append(message.get("body", b""))
        await send(
            {"type": "http.response.start", "status": 200, "headers": []}
        )
        await send({"type": "http.response.body", "body": b"ok"})


def make_scope(
    *,
    content_length=None,
    method="POST",
    path=PATH,
    headers=None,
):
    raw_headers = list(headers or [])
    if content_length is not None:
        raw_headers.append(
            (b"content-length", str(content_length).encode())
        )
    return {
        "type": "http",
        "asgi": {"version": "3.0"},
        "http_version": "1.1",
        "method": method,
        "scheme": "http",
        "path": path,
        "raw_path": path.encode(),
        "query_string": b"",
        "root_path": "",
        "headers": raw_headers,
        "client": ("127.0.0.1", 12345),
        "server": ("testserver", 80),
        "state": {},
    }


def fixed_receive(messages):
    pending = list(messages)
    seen = []

    async def receive():
        seen.append(True)
        if pending:
            return pending.pop(0)
        return {"type": "http.disconnect"}

    receive.seen = seen
    return receive


@pytest.mark.asyncio
async def test_content_length_over_cap_rejected_before_handler():
    admission = AdmissionController(max_sessions=4, max_bytes=1000)
    app = DummyApp()
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=65 * MIB,
        upload_timeout=1.0,
    )
    scope = make_scope(content_length=65 * MIB + 1)
    send = SendCollector()
    receive = fixed_receive([])

    await middleware(scope, receive, send)

    status, headers, body = send.response()
    assert status == 413
    assert headers["content-type"].startswith("application/json")
    assert body == {
        "detail": {
            "code": "payload_too_large",
            "message": "request body exceeds 68157440 bytes",
            "retryable": False,
        }
    }
    assert app.calls == []
    assert receive.seen == []
    assert admission.snapshot() == AdmissionSnapshot(0, 0)
    assert "audio_lease" not in scope["state"]


@pytest.mark.asyncio
async def test_chunked_body_over_cap_rejected_while_receiving():
    admission = AdmissionController(max_sessions=4, max_bytes=1000)
    app = DummyApp()
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=10,
        upload_timeout=1.0,
    )
    scope = make_scope()
    send = SendCollector()
    receive = fixed_receive(
        [
            {"type": "http.request", "body": b"123456", "more_body": True},
            {"type": "http.request", "body": b"123456", "more_body": True},
            {"type": "http.request", "body": b"123456", "more_body": False},
        ]
    )

    await middleware(scope, receive, send)

    status, _, body = send.response()
    assert status == 413
    assert body["detail"]["code"] == "payload_too_large"
    assert app.calls == []
    assert len(receive.seen) == 2
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_chunked_body_at_cap_is_accepted():
    admission = AdmissionController(max_sessions=4, max_bytes=1000)
    app = DummyApp(reserve=6)
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=10,
        upload_timeout=1.0,
    )
    scope = make_scope()
    send = SendCollector()
    receive = fixed_receive(
        [
            {"type": "http.request", "body": b"12345", "more_body": True},
            {"type": "http.request", "body": b"67890", "more_body": False},
        ]
    )

    await middleware(scope, receive, send)

    status, _, _ = send.response()
    assert status == 200
    assert app.bodies == [b"1234567890"]
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_incomplete_body_returns_upload_timeout():
    admission = AdmissionController(max_sessions=4, max_bytes=1000)
    app = DummyApp()
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=100,
        upload_timeout=1.0,
    )
    scope = make_scope(content_length=10)
    send = SendCollector()
    receive = fixed_receive(
        [
            {
                "type": "http.request",
                "body": b"1234",
                "more_body": False,
            }
        ]
    )

    await middleware(scope, receive, send)

    status, _, body = send.response()
    assert status == 408
    assert body["detail"]["code"] == "upload_timeout"
    assert app.calls == []
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_slow_body_returns_upload_timeout_at_deadline():
    admission = AdmissionController(max_sessions=4, max_bytes=1000)
    app = DummyApp()
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=100,
        upload_timeout=0.05,
    )
    scope = make_scope()
    send = SendCollector()
    first = True

    async def receive():
        nonlocal first
        if first:
            first = False
            return {
                "type": "http.request",
                "body": b"1234",
                "more_body": True,
            }
        await asyncio.sleep(5)
        return {"type": "http.request", "body": b"", "more_body": False}

    started = time.monotonic()
    await middleware(scope, receive, send)
    elapsed = time.monotonic() - started

    status, _, body = send.response()
    assert status == 408
    assert body["detail"]["code"] == "upload_timeout"
    assert elapsed < 1.0
    assert app.calls == []
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_busy_admission_returns_503_with_retry_after():
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    held = admission.acquire()
    app = DummyApp()
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=100,
        upload_timeout=1.0,
    )
    scope = make_scope()
    send = SendCollector()

    await middleware(scope, fixed_receive([]), send)

    status, headers, body = send.response()
    assert status == 503
    assert headers["retry-after"] == "1"
    assert body == {
        "detail": {
            "code": "busy",
            "message": "too many active audio sessions",
            "retryable": True,
        }
    }
    assert app.calls == []
    assert admission.snapshot() == AdmissionSnapshot(1, 0)

    held.close()
    send = SendCollector()
    receive = fixed_receive(
        [{"type": "http.request", "body": b"", "more_body": False}]
    )
    await middleware(scope, receive, send)
    assert send.response()[0] == 200
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_connection_loss_releases_lease():
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    app = DummyApp()
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=100,
        upload_timeout=1.0,
    )
    scope = make_scope()
    send = SendCollector()

    async def receive():
        return {"type": "http.disconnect"}

    await middleware(scope, receive, send)

    assert send.messages == []
    assert app.calls == []
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_handler_exception_releases_reserved_bytes():
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    app = DummyApp(reserve=7, raise_error=True)
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=100,
        upload_timeout=1.0,
    )
    scope = make_scope()

    with pytest.raises(RuntimeError, match="handler exploded"):
        await middleware(
            scope,
            fixed_receive(
                [
                    {
                        "type": "http.request",
                        "body": b"",
                        "more_body": False,
                    }
                ]
            ),
            SendCollector(),
        )

    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_transferred_lease_is_not_closed_by_middleware():
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    app = DummyApp(reserve=7, transfer=True)
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=100,
        upload_timeout=1.0,
    )
    scope = make_scope()
    send = SendCollector()

    await middleware(
        scope,
        fixed_receive(
            [{"type": "http.request", "body": b"", "more_body": False}]
        ),
        send,
    )

    assert send.response()[0] == 200
    assert app.lease.transferred is True
    assert app.lease.closed is False
    assert admission.snapshot() == AdmissionSnapshot(1, 7)

    app.lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_request_state_exposes_the_lease():
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    app = FastAPI()

    @app.post(PATH)
    async def route(request: Request):
        lease = request.state.audio_lease
        lease.reserve(4)
        return {"ok": True}

    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=100,
        upload_timeout=1.0,
    )
    transport = httpx.ASGITransport(app=middleware)

    async with httpx.AsyncClient(
        transport=transport, base_url="http://test"
    ) as client:
        response = await client.post(PATH, content=b"1234")

    assert response.status_code == 200
    assert response.json() == {"ok": True}
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_invalid_multipart_releases_lease():
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    app = FastAPI()
    calls = []

    @app.post(PATH)
    async def route(file: UploadFile = File(...)):
        calls.append(file.filename)
        return {"ok": True}

    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=1024,
        upload_timeout=1.0,
    )
    transport = httpx.ASGITransport(app=middleware)

    async with httpx.AsyncClient(
        transport=transport, base_url="http://test"
    ) as client:
        response = await client.post(
            PATH,
            content=b"garbage",
            headers={
                "content-type": "multipart/form-data; boundary=missing"
            },
        )

    assert response.status_code == 400
    assert calls == []
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_parser_exception_releases_lease(monkeypatch):
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    app = FastAPI()
    calls = []

    @app.post(PATH)
    async def route(file: UploadFile = File(...)):
        calls.append(file.filename)
        return {"ok": True}

    async def explode(self):
        raise RuntimeError("parser exploded")

    monkeypatch.setattr(
        "starlette.formparsers.MultiPartParser.parse", explode
    )
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=1024,
        upload_timeout=1.0,
    )
    transport = httpx.ASGITransport(app=middleware)

    async with httpx.AsyncClient(
        transport=transport, base_url="http://test"
    ) as client:
        response = await client.post(
            PATH,
            files={
                "file": ("d.pcm", b"1234", "application/octet-stream")
            },
        )

    assert response.status_code == 400
    assert calls == []
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


@pytest.mark.asyncio
async def test_other_paths_and_methods_are_not_limited():
    admission = AdmissionController(max_sessions=1, max_bytes=10)
    app = DummyApp()
    middleware = UploadLimitMiddleware(
        app,
        admission,
        max_body_bytes=10,
        upload_timeout=0.01,
    )

    for method, path in (
        ("GET", PATH),
        ("POST", "/health"),
        ("POST", "/v1/audio/translations"),
    ):
        send = SendCollector()
        scope = make_scope(
            content_length=1000, method=method, path=path
        )
        await middleware(scope, fixed_receive([]), send)
        assert send.response()[0] == 200
        assert "audio_lease" not in scope["state"]

    assert admission.snapshot() == AdmissionSnapshot(0, 0)
