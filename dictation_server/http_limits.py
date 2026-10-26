"""ASGI upload limits applied before FastAPI parses multipart bodies."""

from __future__ import annotations

import asyncio
from typing import Any, Awaitable, Callable

from starlette.responses import JSONResponse

from .admission import AdmissionController
from .errors import DictationFailure, http_status

_TRANSCRIPTIONS_PATH = "/v1/audio/transcriptions"

_Receive = Callable[[], Awaitable[dict[str, Any]]]
_Send = Callable[[dict[str, Any]], Awaitable[None]]


def _content_length(scope: dict[str, Any]) -> int | None:
    for name, value in scope.get("headers", []):
        if name.lower() != b"content-length":
            continue
        try:
            return int(value)
        except (TypeError, ValueError):
            return None
    return None


def _replay_receive(body: bytes) -> _Receive:
    sent = False

    async def receive() -> dict[str, Any]:
        nonlocal sent
        if sent:
            return {"type": "http.disconnect"}
        sent = True
        return {
            "type": "http.request",
            "body": body,
            "more_body": False,
        }

    return receive


class UploadLimitMiddleware:
    """Bound transcription uploads before the multipart parser sees them.

    The middleware only matches ``POST /v1/audio/transcriptions``. It rejects
    an oversized ``Content-Length`` immediately, then reads and counts the
    real body chunks under an upload deadline so chunked or lying requests
    cannot bypass the cap. Accepted bodies are replayed to the application.

    An admission lease is acquired before the body is read and exposed as
    ``scope["state"]["audio_lease"]`` (``request.state.audio_lease`` in the
    route). The middleware closes the lease unless the route explicitly
    transferred ownership to a later task.
    """

    def __init__(
        self,
        app: Callable[..., Awaitable[None]],
        admission: AdmissionController,
        max_body_bytes: int,
        upload_timeout: float,
    ):
        self.app = app
        self.admission = admission
        self.max_body_bytes = max_body_bytes
        self.upload_timeout = upload_timeout

    async def __call__(
        self,
        scope: dict[str, Any],
        receive: _Receive,
        send: _Send,
    ) -> None:
        if not self._matches(scope):
            await self.app(scope, receive, send)
            return

        content_length = _content_length(scope)
        if (
            content_length is not None
            and content_length > self.max_body_bytes
        ):
            await self._reject(
                scope,
                receive,
                send,
                DictationFailure(
                    "payload_too_large",
                    f"request body exceeds {self.max_body_bytes} bytes",
                ),
            )
            return

        try:
            lease = self.admission.acquire()
        except DictationFailure as exc:
            await self._reject(scope, receive, send, exc)
            return

        scope.setdefault("state", {})["audio_lease"] = lease
        # Keep the raw receive callable for disconnect monitoring after the
        # body has been fully buffered and replaced by a replay callable.
        scope["state"]["client_receive"] = receive
        try:
            body = await self._read_body(scope, receive, send)
            if body is None:
                return
            await self.app(scope, _replay_receive(body), send)
        finally:
            if not lease.transferred:
                lease.close()

    @staticmethod
    def _matches(scope: dict[str, Any]) -> bool:
        return (
            scope.get("type") == "http"
            and scope.get("method") == "POST"
            and scope.get("path") == _TRANSCRIPTIONS_PATH
        )

    async def _read_body(
        self,
        scope: dict[str, Any],
        receive: _Receive,
        send: _Send,
    ) -> bytes | None:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + self.upload_timeout
        received = 0
        chunks: list[bytes] = []
        declared_length = _content_length(scope)

        while True:
            remaining = deadline - loop.time()
            if remaining <= 0:
                await self._reject(
                    scope,
                    receive,
                    send,
                    DictationFailure(
                        "upload_timeout",
                        "upload deadline exceeded",
                        retryable=True,
                    ),
                )
                return None

            try:
                message = await asyncio.wait_for(
                    receive(), timeout=remaining
                )
            except TimeoutError:
                await self._reject(
                    scope,
                    receive,
                    send,
                    DictationFailure(
                        "upload_timeout",
                        "upload deadline exceeded",
                        retryable=True,
                    ),
                )
                return None

            if message["type"] == "http.disconnect":
                return None
            if message["type"] != "http.request":
                continue

            chunk = message.get("body", b"")
            if chunk:
                received += len(chunk)
                if received > self.max_body_bytes:
                    await self._reject(
                        scope,
                        receive,
                        send,
                        DictationFailure(
                            "payload_too_large",
                            (
                                "request body exceeds "
                                f"{self.max_body_bytes} bytes"
                            ),
                        ),
                    )
                    return None
                chunks.append(bytes(chunk))

            if not message.get("more_body", False):
                break

        if declared_length is not None and received < declared_length:
            await self._reject(
                scope,
                receive,
                send,
                DictationFailure(
                    "upload_timeout",
                    "request body incomplete",
                    retryable=True,
                ),
            )
            return None

        return b"".join(chunks)

    async def _reject(
        self,
        scope: dict[str, Any],
        receive: _Receive,
        send: _Send,
        failure: DictationFailure,
    ) -> None:
        headers = None
        if failure.code in {"busy", "queue_timeout"}:
            headers = {"Retry-After": "1"}
        response = JSONResponse(
            status_code=http_status(failure.code),
            content={
                "detail": {
                    "code": failure.code,
                    "message": failure.message,
                    "retryable": failure.retryable,
                }
            },
            headers=headers,
        )
        await response(scope, receive, send)
