import asyncio
import io
import logging
import struct
import wave

import pytest

from dictation_server import decode as decode_module
from dictation_server.admission import (
    AdmissionController,
    AdmissionSnapshot,
)
from dictation_server.config import LimitsConfig
from dictation_server.decode import (
    AudioDecodeError,
    _FFMPEG_COMMAND,
    _drain_stderr,
    decode_upload,
    validate_pcm,
)
from dictation_server.errors import DictationFailure, http_status
from tests.helpers import make_silence

MIB = 1024 * 1024


class FakeWriter:
    def __init__(self):
        self.data = bytearray()
        self.closed = False
        self.drains = 0

    def write(self, chunk):
        self.data.extend(chunk)

    async def drain(self):
        self.drains += 1
        await asyncio.sleep(0)

    def close(self):
        self.closed = True


class FakeReader:
    def __init__(self, chunks=(), block_after=False, gate=None):
        self.chunks = list(chunks)
        self.block_after = block_after
        self.gate = gate
        self.active = 0
        self.reads = 0

    async def read(self, _n=-1):
        self.active += 1
        self.reads += 1
        try:
            await asyncio.sleep(0)
            if self.chunks:
                return self.chunks.pop(0)
            if self.block_after:
                await self.gate.wait()
            return b""
        finally:
            self.active -= 1


class FakeProcess:
    def __init__(
        self,
        stdout_chunks=(),
        stderr_chunks=(),
        returncode=0,
        block=False,
        gate=None,
    ):
        self.stdin = FakeWriter()
        self.stdout = FakeReader(
            stdout_chunks, block_after=block, gate=gate
        )
        self.stderr = FakeReader(
            stderr_chunks, block_after=block, gate=gate
        )
        self._returncode = returncode
        self._gate = gate
        self._exited = not block
        self.killed = False
        self.waited = False

    @property
    def returncode(self):
        return self._returncode if self._exited else None

    async def wait(self):
        self.waited = True
        if self._gate is not None and not self._exited:
            await self._gate.wait()
        self._exited = True
        return self._returncode

    def kill(self):
        self.killed = True
        self._returncode = -9
        self._exited = True
        if self._gate is not None:
            self._gate.set()


def wav_bytes(
    pcm: bytes,
    *,
    channels: int = 1,
    sample_width: int = 2,
    rate: int = 16000,
) -> bytes:
    output = io.BytesIO()
    with wave.open(output, "wb") as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(sample_width)
        wav.setframerate(rate)
        wav.writeframes(pcm)
    return output.getvalue()


def riff_chunk(kind: bytes, payload: bytes) -> bytes:
    padding = b"\x00" if len(payload) % 2 else b""
    return kind + struct.pack("<I", len(payload)) + payload + padding


def float_wav(pcm: bytes = b"\x00" * 64) -> bytes:
    fmt = struct.pack(
        "<HHIIHH", 3, 1, 16000, 64000, 4, 32
    )
    body = (
        b"WAVE"
        + riff_chunk(b"fmt ", fmt)
        + riff_chunk(b"data", pcm)
    )
    return b"RIFF" + struct.pack("<I", len(body)) + body


def patch_spawn(monkeypatch, process):
    async def fake_spawn():
        return process

    monkeypatch.setattr(decode_module, "_spawn_ffmpeg", fake_spawn)
    monkeypatch.setattr(
        decode_module.shutil, "which", lambda _name: "/usr/bin/ffmpeg"
    )


def new_lease(admission, reserved=0):
    lease = admission.acquire()
    if reserved:
        lease.reserve(reserved)
    return lease


def assert_failure(failure, code, status):
    assert isinstance(failure, DictationFailure)
    assert failure.code == code
    assert http_status(failure.code) == status


async def test_raw_pcm_reuses_lease_without_double_reserving():
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    data = b"\x01\x02\x03\x04"
    lease = new_lease(admission, len(data))

    pcm = await decode_upload(
        data,
        "d.pcm",
        "application/octet-stream",
        True,
        lease,
        LimitsConfig(),
        1.0,
    )

    assert pcm == data
    assert lease.reserved_bytes == len(data)
    assert admission.snapshot() == AdmissionSnapshot(1, len(data))
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_raw_pcm_empty_is_valid():
    admission = AdmissionController(max_sessions=1, max_bytes=10)
    lease = new_lease(admission)

    assert (
        await decode_upload(
            b"",
            "d.pcm",
            "application/octet-stream",
            True,
            lease,
            LimitsConfig(),
            1.0,
        )
        == b""
    )
    assert lease.reserved_bytes == 0
    lease.close()


async def test_raw_pcm_rejects_odd_length():
    admission = AdmissionController(max_sessions=1, max_bytes=10)
    lease = new_lease(admission, 3)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"\x00\x00\x00",
            "d.pcm",
            "application/octet-stream",
            True,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "invalid_audio", 422)
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_raw_pcm_over_cap_is_payload_too_large():
    limits = LimitsConfig(max_audio_bytes=4)
    admission = AdmissionController(max_sessions=1, max_bytes=100)
    lease = new_lease(admission, 5)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"\x00\x00\x00\x00\x00",
            "d.pcm",
            "application/octet-stream",
            True,
            lease,
            limits,
            1.0,
        )

    assert_failure(excinfo.value, "payload_too_large", 413)
    lease.close()


async def test_input_over_64_mib_is_rejected_before_spawning(monkeypatch):
    called = []

    async def forbidden_spawn():
        called.append(True)
        raise AssertionError("decoder must not spawn")

    monkeypatch.setattr(
        decode_module, "_spawn_ffmpeg", forbidden_spawn
    )
    admission = AdmissionController(
        max_sessions=1, max_bytes=256 * MIB
    )
    data = b"\x00" * (64 * MIB + 2)
    lease = new_lease(admission)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            data,
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "payload_too_large", 413)
    assert called == []
    lease.close()


def test_body_cap_remains_65_mib():
    limits = LimitsConfig()
    assert limits.max_audio_bytes == 64 * MIB
    assert limits.max_http_body_bytes == 65 * MIB


async def test_compliant_wav_avoids_ffmpeg_and_transfers_quota(
    monkeypatch,
):
    def forbidden(*_args, **_kwargs):
        raise AssertionError("ffmpeg must not be used")

    monkeypatch.setattr(
        decode_module.shutil, "which", forbidden
    )
    monkeypatch.setattr(
        decode_module, "_spawn_ffmpeg", forbidden
    )
    pcm = make_silence(0.2)
    data = wav_bytes(pcm)
    admission = AdmissionController(
        max_sessions=1, max_bytes=1000 * 1000
    )
    lease = new_lease(admission, len(data))

    result = await decode_upload(
        data,
        "d.wav",
        "audio/wav",
        False,
        lease,
        LimitsConfig(),
        1.0,
    )

    assert result == pcm
    assert lease.reserved_bytes == len(pcm)
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_compliant_wav_with_extra_chunks_is_read_in_chunks(
    monkeypatch,
):
    def forbidden(*_args, **_kwargs):
        raise AssertionError("ffmpeg must not be used")

    monkeypatch.setattr(
        decode_module.shutil, "which", forbidden
    )
    pcm = make_silence(1.5)
    fmt = struct.pack("<HHIIHH", 1, 1, 16000, 32000, 2, 16)
    body = (
        b"WAVE"
        + riff_chunk(b"fmt ", fmt)
        + riff_chunk(b"LIST", b"INFOISFT\x05\x00\x00\x00test\x00")
        + riff_chunk(b"data", pcm)
    )
    data = b"RIFF" + struct.pack("<I", len(body)) + body
    admission = AdmissionController(
        max_sessions=1, max_bytes=4 * MIB
    )
    lease = new_lease(admission, len(data))

    result = await decode_upload(
        data,
        None,
        "application/octet-stream",
        False,
        lease,
        LimitsConfig(),
        1.0,
    )

    assert result == pcm
    lease.close()


async def test_empty_data_chunk_is_a_valid_empty_result(monkeypatch):
    def forbidden(*_args, **_kwargs):
        raise AssertionError("ffmpeg must not be used")

    monkeypatch.setattr(
        decode_module.shutil, "which", forbidden
    )
    data = wav_bytes(b"")
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, len(data))

    assert (
        await decode_upload(
            data,
            "d.wav",
            "audio/wav",
            False,
            lease,
            LimitsConfig(),
            1.0,
        )
        == b""
    )
    lease.close()


@pytest.mark.parametrize(
    "data",
    [
        b"not-a-wave-file",
        b"RIFF\x10\x00\x00\x00WAVEfmt \x10\x00\x00\x00",
    ],
)
async def test_invalid_wav_is_invalid_audio(monkeypatch, data):
    def forbidden(*_args, **_kwargs):
        raise AssertionError("ffmpeg must not be used")

    monkeypatch.setattr(
        decode_module, "_spawn_ffmpeg", forbidden
    )
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, len(data))

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            data,
            "d.wav",
            "audio/wav",
            False,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "invalid_audio", 422)
    lease.close()


async def test_truncated_wav_data_chunk_is_invalid_audio(monkeypatch):
    def forbidden(*_args, **_kwargs):
        raise AssertionError("ffmpeg must not be used")

    monkeypatch.setattr(
        decode_module, "_spawn_ffmpeg", forbidden
    )
    fmt = struct.pack("<HHIIHH", 1, 1, 16000, 32000, 2, 16)
    body = (
        b"WAVE"
        + riff_chunk(b"fmt ", fmt)
        + b"data"
        + struct.pack("<I", 100)
        + b"\x00" * 10
    )
    data = b"RIFF" + struct.pack("<I", len(body)) + body
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, len(data))

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            data,
            "d.wav",
            "audio/wav",
            False,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "invalid_audio", 422)
    lease.close()


async def test_other_wav_encoding_uses_ffmpeg(monkeypatch):
    pcm = make_silence(0.1)
    data = float_wav()
    process = FakeProcess(stdout_chunks=[pcm])
    patch_spawn(monkeypatch, process)
    admission = AdmissionController(
        max_sessions=1, max_bytes=1000 * 1000
    )
    lease = new_lease(admission, len(data))

    result = await decode_upload(
        data,
        "d.wav",
        "audio/wav",
        False,
        lease,
        LimitsConfig(),
        1.0,
    )

    assert result == pcm
    assert bytes(process.stdin.data) == data
    assert lease.reserved_bytes == len(pcm)
    lease.close()


async def test_octet_stream_is_not_implicit_pcm(monkeypatch):
    pcm = make_silence(0.1)
    data = b"compressed-bytes"
    process = FakeProcess(stdout_chunks=[pcm])
    patch_spawn(monkeypatch, process)
    admission = AdmissionController(
        max_sessions=1, max_bytes=1000 * 1000
    )
    lease = new_lease(admission, len(data))

    result = await decode_upload(
        data,
        "d.bin",
        "application/octet-stream",
        False,
        lease,
        LimitsConfig(),
        1.0,
    )

    assert result == pcm
    assert bytes(process.stdin.data) == data
    lease.close()


@pytest.mark.parametrize(
    ("filename", "content_type", "data"),
    [
        ("d.wav", "audio/wav", float_wav()),
        ("d.mp3", "audio/mpeg", b"compressed"),
    ],
)
async def test_non_compliant_formats_without_ffmpeg_are_unavailable(
    monkeypatch, filename, content_type, data
):
    monkeypatch.setattr(
        decode_module.shutil, "which", lambda _name: None
    )
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, len(data))

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            data,
            filename,
            content_type,
            False,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "decoder_unavailable", 503)
    assert excinfo.value.retryable is False
    lease.close()


async def test_decoder_failure_is_invalid_audio(monkeypatch):
    process = FakeProcess(returncode=1)
    patch_spawn(monkeypatch, process)
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, 4)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"junk",
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "invalid_audio", 422)
    assert process.killed is False
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_decoder_failure_leaves_cleanup_to_lease_owner(
    monkeypatch,
):
    process = FakeProcess(
        stdout_chunks=[b"\x00\x00"], returncode=1
    )
    patch_spawn(monkeypatch, process)
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, 4)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"junk",
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "invalid_audio", 422)
    assert lease.reserved_bytes == 6
    assert admission.snapshot() == AdmissionSnapshot(1, 6)
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_spawn_failure_is_decoder_unavailable(monkeypatch):
    async def broken_spawn():
        raise OSError("no executable")

    monkeypatch.setattr(
        decode_module, "_spawn_ffmpeg", broken_spawn
    )
    monkeypatch.setattr(
        decode_module.shutil, "which", lambda _name: "/usr/bin/ffmpeg"
    )
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, 4)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"junk",
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            1.0,
        )

    assert_failure(excinfo.value, "decoder_unavailable", 503)
    lease.close()


async def test_expanded_output_over_cap_is_killed_while_growing(
    monkeypatch,
):
    chunks = [b"\x00" * MIB for _ in range(70)]
    process = FakeProcess(
        stdout_chunks=chunks,
        block=True,
        gate=asyncio.Event(),
    )
    patch_spawn(monkeypatch, process)
    admission = AdmissionController(
        max_sessions=1, max_bytes=192 * MIB
    )
    lease = new_lease(admission, 4)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"junk",
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            5.0,
        )

    assert_failure(excinfo.value, "payload_too_large", 413)
    assert process.killed is True
    assert process.waited is True
    assert process.stdout.reads < 70
    assert process.stdin.closed is True
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_shared_budget_exhaustion_kills_and_raises_busy(
    monkeypatch,
):
    admission = AdmissionController(
        max_sessions=1, max_bytes=4 + 3 * MIB
    )
    chunks = [b"\x00" * MIB for _ in range(10)]
    process = FakeProcess(
        stdout_chunks=chunks,
        block=True,
        gate=asyncio.Event(),
    )
    patch_spawn(monkeypatch, process)
    lease = new_lease(admission, 4)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"junk",
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            5.0,
        )

    assert_failure(excinfo.value, "busy", 503)
    assert excinfo.value.retryable is True
    assert process.killed is True
    assert process.waited is True
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_blocked_ffmpeg_times_out_with_cleanup(monkeypatch):
    process = FakeProcess(
        block=True, gate=asyncio.Event()
    )
    patch_spawn(monkeypatch, process)
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, 4)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"junk",
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            0.05,
        )

    assert_failure(excinfo.value, "decode_timeout", 504)
    assert excinfo.value.retryable is True
    assert process.killed is True
    assert process.waited is True
    assert process.stdout.active == 0
    assert process.stderr.active == 0
    assert process.stdin.closed is True
    assert lease.reserved_bytes == 4
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_cancellation_cleans_up_and_releases_partial_pcm(
    monkeypatch,
):
    process = FakeProcess(
        stdout_chunks=[b"\x00\x00"],
        block=True,
        gate=asyncio.Event(),
    )
    patch_spawn(monkeypatch, process)
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, 4)

    task = asyncio.create_task(
        decode_upload(
            b"junk",
            "d.mp3",
            "audio/mpeg",
            False,
            lease,
            LimitsConfig(),
            5.0,
        )
    )
    for _ in range(200):
        if lease.reserved_bytes > 4:
            break
        await asyncio.sleep(0.005)
    assert lease.reserved_bytes == 6

    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task

    assert process.killed is True
    assert process.waited is True
    assert process.stdout.active == 0
    assert process.stderr.active == 0
    assert lease.reserved_bytes == 4
    lease.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_semaphore_serializes_conversions(monkeypatch):
    gate = asyncio.Event()
    first = FakeProcess(
        stdout_chunks=[b"\x00\x00"],
        block=True,
        gate=gate,
    )
    second = FakeProcess(stdout_chunks=[b"\x00\x00"])
    spawned = []

    async def fake_spawn():
        spawned.append(True)
        return first if len(spawned) == 1 else second

    monkeypatch.setattr(decode_module, "_spawn_ffmpeg", fake_spawn)
    monkeypatch.setattr(
        decode_module.shutil, "which", lambda _name: "/usr/bin/ffmpeg"
    )
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    lease_one = new_lease(admission, 4)
    lease_two = new_lease(admission, 4)

    task_one = asyncio.create_task(
        decode_upload(
            b"junk", "a.mp3", "audio/mpeg", False,
            lease_one, LimitsConfig(), 5.0,
        )
    )
    for _ in range(200):
        if spawned:
            break
        await asyncio.sleep(0.005)
    task_two = asyncio.create_task(
        decode_upload(
            b"junk", "b.mp3", "audio/mpeg", False,
            lease_two, LimitsConfig(), 5.0,
        )
    )
    await asyncio.sleep(0.05)
    assert len(spawned) == 1

    gate.set()
    assert await task_one == b"\x00\x00"
    assert await task_two == b"\x00\x00"
    assert len(spawned) == 2

    lease_one.close()
    lease_two.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_semaphore_wait_counts_against_decode_deadline(
    monkeypatch,
):
    gate = asyncio.Event()
    first = FakeProcess(
        block=True, gate=gate
    )
    spawned = []

    async def fake_spawn():
        spawned.append(True)
        return first

    monkeypatch.setattr(decode_module, "_spawn_ffmpeg", fake_spawn)
    monkeypatch.setattr(
        decode_module.shutil, "which", lambda _name: "/usr/bin/ffmpeg"
    )
    admission = AdmissionController(max_sessions=2, max_bytes=1000)
    lease_one = new_lease(admission, 4)
    lease_two = new_lease(admission, 4)

    task_one = asyncio.create_task(
        decode_upload(
            b"junk", "a.mp3", "audio/mpeg", False,
            lease_one, LimitsConfig(), 5.0,
        )
    )
    for _ in range(200):
        if spawned:
            break
        await asyncio.sleep(0.005)

    with pytest.raises(DictationFailure) as excinfo:
        await decode_upload(
            b"junk", "b.mp3", "audio/mpeg", False,
            lease_two, LimitsConfig(), 0.05,
        )

    assert_failure(excinfo.value, "decode_timeout", 504)
    assert len(spawned) == 1

    task_one.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task_one

    lease_one.close()
    lease_two.close()
    assert admission.snapshot() == AdmissionSnapshot(0, 0)


async def test_stderr_is_capped_and_never_logged(
    monkeypatch, caplog
):
    secret = b"SECRET-STDERR-" + b"x" * 100
    admission = AdmissionController(max_sessions=1, max_bytes=1000)
    lease = new_lease(admission, 4)
    process = FakeProcess(
        stderr_chunks=[secret] * 1000,
        returncode=1,
    )
    patch_spawn(monkeypatch, process)

    with caplog.at_level(logging.DEBUG):
        with pytest.raises(DictationFailure) as excinfo:
            await decode_upload(
                b"junk",
                "d.mp3",
                "audio/mpeg",
                False,
                lease,
                LimitsConfig(),
                1.0,
            )

    assert "SECRET-STDERR" not in caplog.text
    assert "SECRET-STDERR" not in excinfo.value.message
    assert "SECRET-STDERR" not in str(excinfo.value)
    lease.close()

    direct = FakeProcess(stderr_chunks=[secret] * 1000)
    diagnostics = bytearray()
    await _drain_stderr(direct, diagnostics)
    assert len(diagnostics) == 64 * 1024


async def test_exact_pipe_args_and_no_temp_files(monkeypatch):
    captured = {}
    pcm = make_silence(0.1)

    async def fake_process(*command, **kwargs):
        captured["command"] = command
        captured["kwargs"] = kwargs
        process = FakeProcess(stdout_chunks=[pcm])
        captured["process"] = process
        return process

    monkeypatch.setattr(
        decode_module.asyncio,
        "create_subprocess_exec",
        fake_process,
    )
    monkeypatch.setattr(
        decode_module.shutil, "which", lambda _name: "/usr/bin/ffmpeg"
    )
    admission = AdmissionController(
        max_sessions=1, max_bytes=1000 * 1000
    )
    data = b"compressed-bytes"
    lease = new_lease(admission, len(data))

    result = await decode_upload(
        data,
        "d.mp3",
        "audio/mpeg",
        False,
        lease,
        LimitsConfig(),
        1.0,
    )

    assert result == pcm
    assert list(captured["command"]) == _FFMPEG_COMMAND
    assert captured["command"][2] == "pipe:0"
    assert captured["command"][-1] == "pipe:1"
    assert "file:" not in " ".join(captured["command"])
    kwargs = captured["kwargs"]
    assert kwargs["stdin"] == asyncio.subprocess.PIPE
    assert kwargs["stdout"] == asyncio.subprocess.PIPE
    assert kwargs["stderr"] == asyncio.subprocess.PIPE
    assert bytes(captured["process"].stdin.data) == data
    lease.close()


def test_validate_pcm_rejects_odd_and_over_cap():
    validate_pcm(b"", 10)
    validate_pcm(b"\x00\x00", 2)

    with pytest.raises(DictationFailure) as odd:
        validate_pcm(b"\x00", 10)
    assert_failure(odd.value, "invalid_audio", 422)

    with pytest.raises(DictationFailure) as large:
        validate_pcm(b"\x00\x00", 1)
    assert_failure(large.value, "payload_too_large", 413)
