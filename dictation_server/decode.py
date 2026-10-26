"""Bounded audio decoding for transcription uploads.

Explicit raw PCM is validated in place and reuses the quota already held by
the caller. Compliant WAV (PCM16 mono 16 kHz) is parsed and read in bounded
chunks without invoking ffmpeg. Every other format goes through one ffmpeg
process at a time; stdout is reserved on the admission lease chunk by chunk
before it is retained, while stderr is drained but capped.

Decoding has a hard deadline that includes waiting for the conversion
semaphore. On timeout or caller cancellation the ffmpeg process is killed,
awaited and its pipe tasks are joined before the error leaves this module.
"""

from __future__ import annotations

import asyncio
import shutil
import struct
import weakref
from dataclasses import dataclass

from .admission import AudioLease
from .config import LimitsConfig
from .errors import DictationFailure

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

_IO_CHUNK = 1024 * 1024
_STDERR_LIMIT = 64 * 1024

_WAV_SUFFIXES = (".wav", ".wave")
_WAV_CONTENT_TYPES = {
    "audio/wav",
    "audio/x-wav",
    "audio/wave",
    "audio/vnd.wave",
}

_WAVE_FORMAT_PCM = 1
_WAVE_FORMAT_EXTENSIBLE = 0xFFFE


class AudioDecodeError(RuntimeError):
    """The uploaded audio could not be decoded to PCM16 mono 16 kHz."""


class FFmpegUnavailable(RuntimeError):
    """The uploaded format needs ffmpeg, but it is not installed."""


@dataclass(frozen=True)
class _WavLayout:
    offset: int
    size: int


_semaphores: weakref.WeakKeyDictionary[
    asyncio.AbstractEventLoop, asyncio.Semaphore
] = weakref.WeakKeyDictionary()


def _conversion_semaphore() -> asyncio.Semaphore:
    """Return the one-conversion semaphore for the running event loop."""
    loop = asyncio.get_running_loop()
    semaphore = _semaphores.get(loop)
    if semaphore is None:
        semaphore = asyncio.Semaphore(1)
        _semaphores[loop] = semaphore
    return semaphore


def validate_pcm(pcm: bytes, max_bytes: int) -> None:
    """Reject PCM that cannot be PCM16 or that exceeds the byte budget."""
    if len(pcm) > max_bytes:
        raise DictationFailure(
            "payload_too_large",
            f"decoded audio exceeds {max_bytes} bytes",
        )
    if len(pcm) % 2:
        raise DictationFailure(
            "invalid_audio", "decoded audio is not PCM16"
        )


def _looks_like_wav(
    filename: str | None, content_type: str | None
) -> bool:
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


def _inspect_wav(
    data: bytes, looks_like_wav: bool
) -> _WavLayout | None:
    """Return the PCM data layout, or None when ffmpeg is needed.

    A malformed RIFF/WAVE container raises :class:`AudioDecodeError`. A valid
    container with another encoding returns None and is handed to ffmpeg.
    """
    if not _has_riff_wave_header(data):
        if looks_like_wav:
            raise AudioDecodeError("invalid WAV payload")
        return None

    fmt: tuple[int, int, int, int] | None = None
    data_offset: int | None = None
    data_size = 0
    offset = 12
    while offset + 8 <= len(data):
        chunk_id = data[offset : offset + 4]
        chunk_size = struct.unpack_from("<I", data, offset + 4)[0]
        payload = offset + 8
        end = payload + chunk_size
        if end > len(data):
            raise AudioDecodeError("truncated WAV chunk")

        if chunk_id == b"fmt ":
            if chunk_size < 16:
                raise AudioDecodeError("invalid WAV fmt chunk")
            (
                format_tag,
                channels,
                sample_rate,
                _byte_rate,
                _block_align,
                bits,
            ) = struct.unpack_from("<HHIIHH", data, payload)
            if format_tag == _WAVE_FORMAT_EXTENSIBLE:
                if chunk_size < 40:
                    raise AudioDecodeError(
                        "invalid WAV extensible fmt chunk"
                    )
                format_tag = struct.unpack_from(
                    "<H", data, payload + 24
                )[0]
            fmt = (format_tag, channels, sample_rate, bits)
        elif chunk_id == b"data":
            data_offset = payload
            data_size = chunk_size
            break

        offset = end + (chunk_size & 1)

    if fmt is None or data_offset is None:
        raise AudioDecodeError("WAV is missing required chunks")

    format_tag, channels, sample_rate, bits = fmt
    if format_tag != _WAVE_FORMAT_PCM:
        return None
    if (channels, bits, sample_rate) != (1, 16, 16000):
        return None
    if data_size % 2:
        raise AudioDecodeError("invalid PCM frame length")
    return _WavLayout(data_offset, data_size)


def _read_wav_pcm(
    data: bytes,
    layout: _WavLayout,
    lease: AudioLease,
    max_bytes: int,
) -> bytes:
    """Read compliant WAV frames in chunks, reserving before retaining."""
    if layout.size > max_bytes:
        raise DictationFailure(
            "payload_too_large",
            f"decoded audio exceeds {max_bytes} bytes",
        )

    chunks: list[bytes] = []
    offset = layout.offset
    end = layout.offset + layout.size
    retained = 0
    while offset < end:
        chunk = data[offset : min(offset + _IO_CHUNK, end)]
        if retained + len(chunk) > max_bytes:
            raise DictationFailure(
                "payload_too_large",
                f"decoded audio exceeds {max_bytes} bytes",
            )
        lease.reserve(len(chunk))
        chunks.append(chunk)
        retained += len(chunk)
        offset += len(chunk)
    return b"".join(chunks)


async def _spawn_ffmpeg() -> asyncio.subprocess.Process:
    return await asyncio.create_subprocess_exec(
        *_FFMPEG_COMMAND,
        stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )


async def _feed_stdin(
    process: asyncio.subprocess.Process, data: bytes
) -> None:
    assert process.stdin is not None
    stdin = process.stdin
    try:
        for offset in range(0, len(data), _IO_CHUNK):
            stdin.write(data[offset : offset + _IO_CHUNK])
            await stdin.drain()
    except (BrokenPipeError, ConnectionResetError):
        pass
    finally:
        try:
            stdin.close()
        except (BrokenPipeError, ConnectionResetError):
            pass


async def _drain_stdout(
    process: asyncio.subprocess.Process,
    lease: AudioLease,
    max_bytes: int,
    chunks: list[bytes],
    reserved: list[int],
) -> None:
    assert process.stdout is not None
    total = 0
    while True:
        chunk = await process.stdout.read(_IO_CHUNK)
        if not chunk:
            return
        if total + len(chunk) > max_bytes:
            raise DictationFailure(
                "payload_too_large",
                f"decoded audio exceeds {max_bytes} bytes",
            )
        # Reserve before retaining so an over-budget output never grows past
        # the admission budget.
        lease.reserve(len(chunk))
        reserved[0] += len(chunk)
        chunks.append(chunk)
        total += len(chunk)


async def _drain_stderr(
    process: asyncio.subprocess.Process, diagnostics: bytearray
) -> None:
    assert process.stderr is not None
    while True:
        chunk = await process.stderr.read(_IO_CHUNK)
        if not chunk:
            return
        room = _STDERR_LIMIT - len(diagnostics)
        if room > 0:
            diagnostics.extend(chunk[:room])


async def _stop_process(
    process: asyncio.subprocess.Process,
    tasks: list[asyncio.Task],
) -> None:
    if process.returncode is None:
        try:
            process.kill()
        except ProcessLookupError:
            pass
    try:
        await process.wait()
    except ProcessLookupError:
        pass
    for task in tasks:
        if not task.done():
            task.cancel()
    await asyncio.gather(*tasks, return_exceptions=True)


async def _convert_with_ffmpeg(
    data: bytes, lease: AudioLease, max_bytes: int
) -> bytes:
    try:
        process = await _spawn_ffmpeg()
    except OSError as exc:
        raise FFmpegUnavailable("ffmpeg could not be started") from exc

    chunks: list[bytes] = []
    diagnostics = bytearray()
    reserved = [0]
    tasks = [
        asyncio.create_task(
            _feed_stdin(process, data), name="decode-stdin"
        ),
        asyncio.create_task(
            _drain_stdout(
                process, lease, max_bytes, chunks, reserved
            ),
            name="decode-stdout",
        ),
        asyncio.create_task(
            _drain_stderr(process, diagnostics), name="decode-stderr"
        ),
    ]

    failure: BaseException | None = None
    try:
        pending = set(tasks)
        while pending:
            done, pending = await asyncio.wait(
                pending, return_when=asyncio.FIRST_EXCEPTION
            )
            for task in done:
                exc = task.exception()
                if exc is not None:
                    failure = exc
                    break
            if failure is not None:
                break

        if failure is None:
            returncode = await process.wait()
            if returncode != 0:
                failure = AudioDecodeError(
                    "ffmpeg could not decode the audio"
                )
        if failure is not None:
            raise failure

        pcm = b"".join(chunks)
        validate_pcm(pcm, max_bytes)
        return pcm
    except asyncio.CancelledError:
        # Timeout or client disconnect: release only the PCM this decoder
        # reserved; the lease owner still owns the encoded bytes.
        lease.release_bytes(reserved[0])
        raise
    finally:
        await _stop_process(process, tasks)


async def _convert_locked(
    data: bytes,
    lease: AudioLease,
    max_bytes: int,
    deadline: float,
) -> bytes:
    semaphore = _conversion_semaphore()
    await semaphore.acquire()
    try:
        remaining = deadline - asyncio.get_running_loop().time()
        if remaining <= 0:
            raise TimeoutError
        return await _convert_with_ffmpeg(data, lease, max_bytes)
    finally:
        semaphore.release()


def _release_encoded(
    lease: AudioLease, encoded_length: int, encoded_reserved: int
) -> None:
    lease.release_bytes(min(encoded_length, encoded_reserved))


async def decode_upload(
    data: bytes,
    filename: str | None,
    content_type: str | None,
    raw_pcm: bool,
    lease: AudioLease,
    limits: LimitsConfig,
    timeout: float,
) -> bytes:
    """Decode an upload to PCM16 mono 16 kHz under byte and time budgets.

    ``lease`` must already own the encoded bytes when the caller reserved
    them (the REST route does). On success those encoded bytes are released
    after the decoded PCM has been reserved. On failure the caller keeps
    ownership and must close the lease.
    """
    max_bytes = limits.max_audio_bytes
    if len(data) > max_bytes:
        raise DictationFailure(
            "payload_too_large", f"audio exceeds {max_bytes} bytes"
        )

    encoded_reserved = lease.reserved_bytes

    if raw_pcm:
        # The bytes are already PCM and already reserved by the caller.
        validate_pcm(data, max_bytes)
        return data

    try:
        layout = _inspect_wav(
            data, _looks_like_wav(filename, content_type)
        )
        if layout is not None:
            pcm = _read_wav_pcm(data, layout, lease, max_bytes)
            _release_encoded(lease, len(data), encoded_reserved)
            return pcm
    except AudioDecodeError as exc:
        raise DictationFailure(
            "invalid_audio", "unable to decode audio"
        ) from exc

    if shutil.which("ffmpeg") is None:
        raise DictationFailure(
            "decoder_unavailable",
            "ffmpeg is required to decode this format",
        )

    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout
    try:
        pcm = await asyncio.wait_for(
            _convert_locked(data, lease, max_bytes, deadline),
            timeout=timeout,
        )
    except TimeoutError as exc:
        raise DictationFailure(
            "decode_timeout",
            "audio decoding timed out",
            retryable=True,
        ) from exc
    except FFmpegUnavailable as exc:
        raise DictationFailure(
            "decoder_unavailable",
            "ffmpeg is required to decode this format",
        ) from exc
    except AudioDecodeError as exc:
        raise DictationFailure(
            "invalid_audio", "unable to decode audio"
        ) from exc

    _release_encoded(lease, len(data), encoded_reserved)
    return pcm
