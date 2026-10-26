"""Optional Bearer authentication for HTTP and WebSocket traffic.

The server stays on the legacy open mode when ``security.api_key_env`` is not
configured. When it is configured, the environment variable must contain a
non-empty token or startup fails. Tokens are never included in errors, reprs
or logs.
"""

from __future__ import annotations

import secrets
from collections.abc import Mapping
from typing import Any, Awaitable, Callable

from starlette.responses import JSONResponse

from .errors import DictationFailure, http_status

PUBLIC_HTTP_PATHS = frozenset({"/health"})

_Receive = Callable[[], Awaitable[dict[str, Any]]]
_Send = Callable[[dict[str, Any]], Awaitable[None]]


def _authorization_header(scope: dict[str, Any]) -> str | None:
    for name, value in scope.get("headers", []):
        if name.lower() == b"authorization":
            return value.decode("latin-1")
    return None


def _bearer_token(authorization: str | None) -> str | None:
    if authorization is None:
        return None
    parts = authorization.split(" ")
    if len(parts) != 2:
        return None
    scheme, token = parts
    if scheme.lower() != "bearer" or not token:
        return None
    return token


class ApiKeyPolicy:
    """Expected API token; disabled when no env variable is configured."""

    def __init__(self, token: str | None = None) -> None:
        self._token = token

    @classmethod
    def from_environment(
        cls,
        variable: str | None,
        environ: Mapping[str, str],
    ) -> "ApiKeyPolicy":
        if variable is None:
            return cls()
        token = environ.get(variable)
        if not token:
            raise ValueError(
                f"security.api_key_env={variable!r} but environment "
                f"variable {variable!r} is missing or empty"
            )
        return cls(token)

    @property
    def enabled(self) -> bool:
        return self._token is not None

    def authorize(self, authorization: str | None) -> None:
        if self._token is None:
            return

        token = _bearer_token(authorization)
        valid = False
        if token is not None:
            try:
                valid = secrets.compare_digest(token, self._token)
            except TypeError:
                # Non-ASCII values cannot be valid HTTP bearer tokens.
                valid = False
        if not valid:
            raise DictationFailure(
                "unauthorized", "unauthorized", retryable=False
            )

    def __repr__(self) -> str:
        return f"{type(self).__name__}(enabled={self.enabled})"


class BearerAuthMiddleware:
    """Pure ASGI authorization applied before request bodies are read.

    ``/health`` stays public so local probes work without a token. Every
    other HTTP path is protected whenever authentication is enabled.
    """

    def __init__(
        self,
        app: Callable[..., Awaitable[None]],
        policy: ApiKeyPolicy,
    ) -> None:
        self.app = app
        self.policy = policy

    async def __call__(
        self,
        scope: dict[str, Any],
        receive: _Receive,
        send: _Send,
    ) -> None:
        if (
            scope.get("type") != "http"
            or not self.policy.enabled
            or scope.get("path") in PUBLIC_HTTP_PATHS
        ):
            await self.app(scope, receive, send)
            return

        try:
            self.policy.authorize(_authorization_header(scope))
        except DictationFailure as failure:
            await self._reject(scope, receive, send, failure)
            return

        await self.app(scope, receive, send)

    @staticmethod
    async def _reject(
        scope: dict[str, Any],
        receive: _Receive,
        send: _Send,
        failure: DictationFailure,
    ) -> None:
        response = JSONResponse(
            status_code=http_status(failure.code),
            content={
                "detail": {
                    "code": failure.code,
                    "message": failure.message,
                    "retryable": failure.retryable,
                }
            },
            headers={"WWW-Authenticate": "Bearer"},
        )
        await response(scope, receive, send)
