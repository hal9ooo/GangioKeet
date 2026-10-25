import asyncio
import io
import json
import logging
import shutil
import struct
import subprocess
import wave
from typing import Literal

from fastapi import (
    FastAPI,
    File,
    Form,
    HTTPException,
    Query,
    UploadFile,
    WebSocket,
    WebSocketDisconnect,
)
from fastapi.responses import PlainTextResponse
from pydantic import ValidationError

from .config import AppConfig
from .pipeline import Pipeline
from .protocol import (
    MAX_AUDIO_BYTES,
    AbortMsg,
    ErrorMsg,
    Profile,
    ResultEnvelope,
    StartMsg,
    StopMsg,
)

logger = logging.getLogger(__name__)

_FFMPEG_COMMAND = [
    "ffmpeg",
    "-i",
    "pipe:0",
    "-f",
    "s16le",
    "-ar",
    "16000",
    "-ac",
    "1",
    "pipe:1",
]

_WAV_SUFFIXES = (".wav", ".wave")
_WAV_CONTENT_TYPES = {
    "audio/wav",
    "audio/x-wav",
    "audio/wave",
    "audio/vnd.wave",
}


class AudioDecodeError(RuntimeError):
    """The uploaded audio could not be decoded to PCM16 mono 16 kHz."""


class FFmpegUnavailable(RuntimeError):
    """The uploaded format needs ffmpeg, but it is not installed."""


def ffmpeg_to_pcm(data: bytes) -> bytes:
    """Decode arbitrary audio bytes to PCM16 mono 16 kHz via a pipe."""
    try:
        process = subprocess.run(
            _FFMPEG_COMMAND,
            input=data,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except OSError as exc:
        raise AudioDecodeError(f"could not run ffmpeg: {exc}") from exc

    if getattr(process, "returncode", 0) != 0:
        raise AudioDecodeError("ffmpeg could not decode the audio")
    return process.stdout


def _looks_like_wav(filename: str | None, content_type: str | None) -> bool:
    if filename and filename.lower().endswith(_WAV_SUFFIXES):
        return True
    media_type = (content_type or "").split(";", 1)[0].strip().lower()
    return media_type in _WAV_CONTENT_TYPES


def _has_riff_wave_header(data: bytes) -> bool:
    return (
        len(data) >= 12
        and data[0:4] == b"RIFF"
        and data[8:12] == b"WAVE"
    )


def _wav_to_pcm(data: bytes) -> bytes | None:
    """Return PCM frames for compliant WAV input, else None.

    Compliant means PCM16, mono, 16 kHz. A valid RIFF/WAVE container with
    another encoding is handed to ffmpeg; bytes without a RIFF/WAVE header
    are rejected immediately as malformed.
    """
    if not _has_riff_wave_header(data):
        raise AudioDecodeError("invalid WAV payload")

    try:
        with wave.open(io.BytesIO(data), "rb") as wav:
            if (
                wav.getnchannels(),
                wav.getsampwidth(),
                wav.getframerate(),
            ) != (1, 2, 16000):
                return None
            return wav.readframes(wav.getnframes())
    except (wave.Error, EOFError, struct.error):
        # The container is WAV but the stdlib cannot parse this encoding
        # (float32, 24-bit, WAVE_FORMAT_EXTENSIBLE, ...). ffmpeg may.
        return None


def _decode_upload(
    data: bytes,
    filename: str | None,
    content_type: str | None,
    raw_pcm: bool,
) -> bytes:
    if raw_pcm:
        return data

    if _looks_like_wav(filename, content_type):
        pcm = _wav_to_pcm(data)
        if pcm is not None:
            return pcm

    if shutil.which("ffmpeg") is None:
        raise FFmpegUnavailable("ffmpeg is required to decode this format")
    return ffmpeg_to_pcm(data)


def _model_aliases(model: str) -> set[str]:
    return {model, model.rsplit("/", 1)[-1]}


class _DictationConnection:
    """One WebSocket session: idle -> recording -> processing -> idle."""

    def __init__(self, websocket: WebSocket, pipeline: Pipeline):
        self.websocket = websocket
        self.pipeline = pipeline
        self.state = "idle"
        self.audio = bytearray()
        self.profile: Profile = "default"
        self.bypass_llm = False
        self.metadata: dict[str, str] = {}
        self.process_task: asyncio.Task | None = None

    async def run(self) -> None:
        await self.websocket.accept()
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
                await self._finish_processing()
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
        if len(self.audio) + len(chunk) > MAX_AUDIO_BYTES:
            await self._send_error(
                "payload_too_large",
                f"audio exceeds {MAX_AUDIO_BYTES} bytes",
            )
            try:
                await self.websocket.close(code=1009)
            except RuntimeError:
                pass
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

        pcm = bytes(self.audio)
        self.audio.clear()
        self.state = "processing"
        self.process_task = asyncio.create_task(
            self.pipeline.process(
                pcm,
                profile=self.profile,
                bypass_llm=self.bypass_llm,
            ),
            name="dictation-process",
        )
        return True

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

    async def _finish_processing(self) -> None:
        task = self.process_task
        assert task is not None
        self.process_task = None
        try:
            result = task.result()
        except asyncio.CancelledError:
            self.state = "idle"
            return
        except Exception:
            logger.exception("dictation pipeline failed")
            self.state = "idle"
            await self._send_error("internal", "processing failed")
            return

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

    async def _cancel_processing(self) -> None:
        task = self.process_task
        self.process_task = None
        self.audio.clear()
        self.state = "idle"
        if task is None:
            return

        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        except Exception:
            logger.exception("dictation pipeline failed during cancellation")

    async def _protocol_error(self, message: str) -> bool:
        return await self._send_error("protocol", message)

    async def _send_error(self, code: str, message: str) -> bool:
        payload = ErrorMsg(code=code, message=message)
        try:
            await self.websocket.send_text(payload.model_dump_json())
            return True
        except (WebSocketDisconnect, RuntimeError):
            return False


def create_app(cfg: AppConfig, pipeline: Pipeline) -> FastAPI:
    app = FastAPI(title="MLX Dictation Server")
    app.state.config = cfg
    app.state.pipeline = pipeline

    @app.get("/health")
    async def health() -> dict[str, str]:
        return {"status": "ok"}

    @app.websocket("/ws/dictate")
    async def dictate(websocket: WebSocket) -> None:
        await _DictationConnection(websocket, pipeline).run()

    @app.post("/v1/audio/transcriptions")
    async def transcriptions(
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

        data = await file.read()
        try:
            pcm = await asyncio.to_thread(
                _decode_upload,
                data,
                file.filename,
                file.content_type,
                raw_pcm=format == "pcm",
            )
        except FFmpegUnavailable as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc
        except AudioDecodeError as exc:
            raise HTTPException(
                status_code=422, detail="unable to decode audio"
            ) from exc

        result = await pipeline.process(pcm, profile=profile)
        if response_format == "text":
            return PlainTextResponse(result.final_text)

        return {
            "text": result.final_text,
            "raw_text": result.raw_text,
            "fast_path": result.fast_path,
            "metrics": result.metrics.model_dump(),
        }

    return app
