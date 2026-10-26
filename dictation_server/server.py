import asyncio
import json
import logging
from typing import Literal

from fastapi import (
    FastAPI,
    File,
    Form,
    HTTPException,
    Query,
    Request,
    UploadFile,
    WebSocket,
    WebSocketDisconnect,
)
from fastapi.responses import JSONResponse, PlainTextResponse
from pydantic import ValidationError

from .admission import AdmissionController, AudioLease
from .auth import ApiKeyPolicy, BearerAuthMiddleware
from .config import AppConfig
from .decode import (
    AudioDecodeError,
    FFmpegUnavailable,
    decode_upload,
)
from .errors import DictationFailure, http_status
from .http_limits import UploadLimitMiddleware
from .pipeline import DictationResult, Pipeline
from .protocol import (
    AbortMsg,
    ErrorMsg,
    Profile,
    ResultEnvelope,
    StartMsg,
    StopMsg,
)
from .scheduler import DictationScheduler

logger = logging.getLogger(__name__)

# AudioDecodeError and FFmpegUnavailable are re-exported for backwards
# compatibility with callers that imported the decoder error types here.

def _model_aliases(model: str) -> set[str]:
    return {model, model.rsplit("/", 1)[-1]}


class _DictationConnection:
    """One WebSocket session: idle -> recording -> processing -> idle."""

    def __init__(
        self,
        websocket: WebSocket,
        pipeline: Pipeline,
        admission: AdmissionController,
        max_audio_bytes: int,
        auth: ApiKeyPolicy | None = None,
        scheduler: DictationScheduler | None = None,
    ):
        self.websocket = websocket
        self.pipeline = pipeline
        self.admission = admission
        self.max_audio_bytes = max_audio_bytes
        self.auth = auth if auth is not None else ApiKeyPolicy()
        self.scheduler = scheduler
        self.state = "idle"
        self.audio = bytearray()
        self.profile: Profile = "default"
        self.bypass_llm = False
        self.metadata: dict[str, str] = {}
        self.process_task: asyncio.Task | None = None
        self.lease: AudioLease | None = None

    def _dictation_scheduler(self) -> DictationScheduler:
        if self.scheduler is None:
            cfg = getattr(self.pipeline, "cfg", None)
            if cfg is None:
                raise DictationFailure(
                    "internal", "dictation scheduler is missing"
                )
            self.scheduler = DictationScheduler(
                self.pipeline,
                max_pending_jobs=cfg.limits.max_pending_jobs,
                queue_timeout=cfg.timeouts.queue_seconds,
                processing_timeout=cfg.timeouts.processing_seconds,
            )
        return self.scheduler

    async def run(self) -> None:
        await self.websocket.accept()
        if not await self._authorized():
            return
        try:
            while True:
                if self.process_task is None:
                    try:
                        message = await self.websocket.receive()
                    except WebSocketDisconnect:
                        break
                    if not await self._handle(message):
                        break
                elif not await self._wait_for_input_or_result():
                    break
        finally:
            await self._cancel_processing()

    async def _authorized(self) -> bool:
        try:
            self.auth.authorize(
                self.websocket.headers.get("authorization")
            )
        except DictationFailure as exc:
            await self._send_error(
                exc.code, exc.message, retryable=exc.retryable
            )
            await self._close(1008)
            return False
        return True

    async def _wait_for_input_or_result(self) -> bool:
        process_task = self.process_task
        assert process_task is not None
        receive_task = asyncio.create_task(self.websocket.receive())
        try:
            done, _ = await asyncio.wait(
                {receive_task, process_task},
                return_when=asyncio.FIRST_COMPLETED,
            )
        except asyncio.CancelledError:
            receive_task.cancel()
            await asyncio.gather(receive_task, return_exceptions=True)
            raise

        if receive_task in done:
            try:
                message = receive_task.result()
            except WebSocketDisconnect:
                await self._cancel_processing()
                return False
            if not await self._handle(message):
                return False
        else:
            receive_task.cancel()
            await asyncio.gather(receive_task, return_exceptions=True)

        if self.process_task is not None and self.process_task.done():
            if self.state == "processing":
                return await self._finish_processing()
        return True

    async def _handle(self, message: dict) -> bool:
        if message["type"] == "websocket.disconnect":
            return False
        text = message.get("text")
        if text is None:
            return await self._handle_audio(message.get("bytes") or b"")
        return await self._handle_text(text)

    async def _handle_audio(self, chunk: bytes) -> bool:
        if self.state != "recording":
            return await self._protocol_error(
                "binary audio is only accepted while recording"
            )
        if self.lease is None:
            await self._send_error(
                "internal", "audio admission lease is missing"
            )
            await self._close(1011)
            return False
        if len(self.audio) + len(chunk) > self.max_audio_bytes:
            await self._send_error(
                "payload_too_large",
                f"audio exceeds {self.max_audio_bytes} bytes",
            )
            self._release_lease()
            await self._close(1009)
            return False

        try:
            self.lease.reserve(len(chunk))
        except DictationFailure as exc:
            await self._send_error(
                exc.code, exc.message, retryable=exc.retryable
            )
            self._release_lease()
            await self._close(1013 if exc.code == "busy" else 1011)
            return False

        self.audio.extend(chunk)
        return True

    async def _handle_text(self, text: str) -> bool:
        try:
            payload = json.loads(text)
        except (json.JSONDecodeError, TypeError):
            return await self._protocol_error("invalid JSON payload")
        if not isinstance(payload, dict):
            return await self._protocol_error("message must be a JSON object")

        kind = payload.get("type")
        if kind == "start":
            return await self._start(payload)
        if kind == "stop":
            return await self._stop(payload)
        if kind == "abort":
            return await self._abort(payload)
        return await self._protocol_error("unrecognized message type")

    async def _start(self, payload: dict) -> bool:
        if self.state != "idle":
            return await self._protocol_error(
                "start is only valid while idle"
            )
        try:
            message = StartMsg.model_validate(payload)
        except ValidationError:
            return await self._protocol_error("invalid start message")

        try:
            lease = self.admission.acquire()
        except DictationFailure as exc:
            await self._send_error(
                exc.code, exc.message, retryable=exc.retryable
            )
            await self._close(1013)
            return False

        self.lease = lease
        self.profile = message.profile
        self.bypass_llm = message.bypass_llm
        self.metadata = {
            "app_name": message.app_name,
            "window_title": message.window_title,
            "selected_text": message.selected_text,
        }
        self.audio.clear()
        self.state = "recording"
        return True

    async def _stop(self, payload: dict) -> bool:
        try:
            StopMsg.model_validate(payload)
        except ValidationError:
            return await self._protocol_error("invalid stop message")
        if self.state != "recording":
            return await self._protocol_error(
                "stop is only valid while recording"
            )

        lease = self.lease
        self.lease = None
        pcm = bytes(self.audio)
        self.audio.clear()
        self.state = "processing"
        self.process_task = asyncio.create_task(
            self._run_processing(
                pcm,
                lease,
                profile=self.profile,
                bypass_llm=self.bypass_llm,
            ),
            name="dictation-process",
        )
        return True

    async def _run_processing(
        self,
        pcm: bytes,
        lease: AudioLease | None,
        profile: Profile,
        bypass_llm: bool,
    ) -> DictationResult:
        if lease is None:
            raise DictationFailure(
                "internal", "audio admission lease is missing"
            )
        try:
            return await self._dictation_scheduler().submit(
                pcm,
                profile=profile,
                bypass_llm=bypass_llm,
                lease=lease,
            )
        finally:
            if not lease.transferred:
                lease.close()

    async def _abort(self, payload: dict) -> bool:
        try:
            AbortMsg.model_validate(payload)
        except ValidationError:
            return await self._protocol_error("invalid abort message")
        if self.state == "idle":
            return await self._protocol_error(
                "abort is only valid while recording or processing"
            )
        await self._cancel_processing()
        return True

    async def _finish_processing(self) -> bool:
        task = self.process_task
        assert task is not None
        self.process_task = None
        try:
            result = task.result()
        except asyncio.CancelledError:
            self.state = "idle"
            self._release_lease()
            return True
        except DictationFailure as exc:
            self.state = "idle"
            self._release_lease()
            if exc.code == "queue_timeout":
                await self._send_error(
                    exc.code, exc.message, retryable=exc.retryable
                )
                return True
            if exc.code in {"busy", "processing_timeout"}:
                await self._send_error(
                    exc.code, exc.message, retryable=exc.retryable
                )
                await self._close(1013)
                return False
            logger.error("dictation processing failed: %s", exc)
            await self._send_error("internal", "processing failed")
            return True
        except Exception:
            logger.exception("dictation pipeline failed")
            self.state = "idle"
            self._release_lease()
            await self._send_error("internal", "processing failed")
            return True

        envelope = ResultEnvelope(
            raw_text=result.raw_text,
            final_text=result.final_text,
            fast_path=result.fast_path,
            metrics=result.metrics,
        )
        try:
            await self.websocket.send_text(envelope.model_dump_json())
        except (WebSocketDisconnect, RuntimeError):
            pass
        finally:
            self.state = "idle"
        return True

    async def _cancel_processing(self) -> None:
        task = self.process_task
        self.process_task = None
        self.audio.clear()
        self.state = "idle"
        self._release_lease()
        if task is None:
            return

        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        except DictationFailure:
            pass
        except Exception:
            logger.exception("dictation pipeline failed during cancellation")

    def _release_lease(self) -> None:
        lease = self.lease
        self.lease = None
        if lease is not None:
            lease.close()

    async def _close(self, code: int) -> None:
        try:
            await self.websocket.close(code=code)
        except (RuntimeError, WebSocketDisconnect):
            pass

    async def _protocol_error(self, message: str) -> bool:
        return await self._send_error("protocol", message)

    async def _send_error(
        self, code: str, message: str, retryable: bool = False
    ) -> bool:
        payload = ErrorMsg(
            code=code, message=message, retryable=retryable
        )
        try:
            await self.websocket.send_text(payload.model_dump_json())
            return True
        except (WebSocketDisconnect, RuntimeError):
            return False


async def _wait_for_disconnect(request: Request) -> None:
    receive = getattr(request.state, "client_receive", None)
    if receive is None:
        while not await request.is_disconnected():
            await asyncio.sleep(0.05)
        return

    while True:
        try:
            message = await receive()
        except Exception:
            return
        if message.get("type") == "http.disconnect":
            return


async def _run_until_disconnect(request: Request, work):
    """Run one REST transcription job, cancelling it on client disconnect.

    ``work`` is a coroutine that already owns any lease it acquires. The
    cancelled coroutine is awaited before returning so a queued job or a
    running engine thread cannot outlive the request without cleanup.
    """
    work_task = asyncio.create_task(work, name="dictation-rest-job")
    disconnect_task = asyncio.create_task(
        _wait_for_disconnect(request), name="dictation-rest-disconnect"
    )
    try:
        done, _ = await asyncio.wait(
            {work_task, disconnect_task},
            return_when=asyncio.FIRST_COMPLETED,
        )
        if work_task in done:
            return work_task.result()

        work_task.cancel()
        try:
            await work_task
        except asyncio.CancelledError:
            pass
        raise HTTPException(
            status_code=499, detail="client disconnected"
        )
    finally:
        disconnect_task.cancel()
        await asyncio.gather(disconnect_task, return_exceptions=True)
        if not work_task.done():
            work_task.cancel()
            await asyncio.gather(work_task, return_exceptions=True)


def create_app(
    cfg: AppConfig,
    pipeline: Pipeline,
    admission: AdmissionController | None = None,
    auth: ApiKeyPolicy | None = None,
) -> FastAPI:
    if admission is None:
        admission = AdmissionController(
            max_sessions=cfg.limits.max_sessions,
            max_bytes=cfg.limits.max_buffered_audio_bytes,
        )
    if auth is None:
        auth = ApiKeyPolicy()

    app = FastAPI(title="MLX Dictation Server")
    app.state.config = cfg
    app.state.pipeline = pipeline
    app.state.admission = admission
    app.state.auth = auth
    scheduler = DictationScheduler(
        pipeline,
        max_pending_jobs=cfg.limits.max_pending_jobs,
        queue_timeout=cfg.timeouts.queue_seconds,
        processing_timeout=cfg.timeouts.processing_seconds,
    )
    app.state.scheduler = scheduler
    app.add_middleware(
        UploadLimitMiddleware,
        admission=admission,
        max_body_bytes=cfg.limits.max_http_body_bytes,
        upload_timeout=cfg.timeouts.upload_seconds,
    )
    # Added after the upload limiter so Starlette places this middleware
    # outermost: unauthorized requests never acquire a lease or read a body.
    app.add_middleware(BearerAuthMiddleware, policy=auth)

    @app.exception_handler(DictationFailure)
    async def _handle_dictation_failure(
        request: Request, exc: DictationFailure
    ) -> JSONResponse:
        headers = None
        if exc.code in {"busy", "queue_timeout"}:
            headers = {"Retry-After": "1"}
        return JSONResponse(
            status_code=http_status(exc.code),
            content={
                "detail": {
                    "code": exc.code,
                    "message": exc.message,
                    "retryable": exc.retryable,
                }
            },
            headers=headers,
        )

    @app.get("/health")
    async def health() -> dict[str, str]:
        return {"status": "ok"}

    @app.websocket("/ws/dictate")
    async def dictate(websocket: WebSocket) -> None:
        await _DictationConnection(
            websocket,
            pipeline,
            admission,
            cfg.limits.max_audio_bytes,
            auth=auth,
            scheduler=scheduler,
        ).run()

    @app.post("/v1/audio/transcriptions")
    async def transcriptions(
        request: Request,
        file: UploadFile = File(...),
        model: str | None = Form(default=None),
        response_format: Literal["json", "text"] = Form(default="json"),
        profile: Profile = Form(default="default"),
        format: Literal["pcm"] | None = Query(default=None),
    ):
        if model is not None and model not in _model_aliases(
            cfg.server.stt_model
        ):
            raise HTTPException(
                status_code=400,
                detail=(
                    "unsupported model; expected one of "
                    f"{sorted(_model_aliases(cfg.server.stt_model))}"
                ),
            )

        lease = getattr(request.state, "audio_lease", None)
        if lease is None:
            raise DictationFailure(
                "internal", "audio admission lease is missing"
            )

        file_size = file.size
        if file_size is None:
            data = await file.read()
            if len(data) > cfg.limits.max_audio_bytes:
                raise DictationFailure(
                    "payload_too_large",
                    (
                        "audio exceeds "
                        f"{cfg.limits.max_audio_bytes} bytes"
                    ),
                )
            lease.reserve(len(data))
        else:
            if file_size > cfg.limits.max_audio_bytes:
                raise DictationFailure(
                    "payload_too_large",
                    (
                        "audio exceeds "
                        f"{cfg.limits.max_audio_bytes} bytes"
                    ),
                )
            lease.reserve(file_size)
            data = await file.read()

        async def _decode_and_transcribe():
            pcm = await decode_upload(
                data,
                file.filename,
                file.content_type,
                raw_pcm=format == "pcm",
                lease=lease,
                limits=cfg.limits,
                timeout=cfg.timeouts.decode_seconds,
            )
            return await scheduler.submit(
                pcm,
                profile=profile,
                bypass_llm=False,
                lease=lease,
            )

        result = await _run_until_disconnect(
            request, _decode_and_transcribe()
        )
        if response_format == "text":
            return PlainTextResponse(result.final_text)

        return {
            "text": result.final_text,
            "raw_text": result.raw_text,
            "fast_path": result.fast_path,
            "metrics": result.metrics.model_dump(),
        }

    return app
