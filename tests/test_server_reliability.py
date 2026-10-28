"""End-to-end reliability contract for the HTTP and WebSocket boundaries."""

import asyncio
import json
import logging
import threading
import time

import httpx
import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from dictation_server.admission import (
    AdmissionController,
    AdmissionSnapshot,
)
from dictation_server.auth import ApiKeyPolicy
from dictation_server.config import load_config
from dictation_server.errors import DictationFailure
from dictation_server.pipeline import Pipeline
from dictation_server.scheduler import SchedulerSnapshot
from dictation_server.server import create_app
from tests.fakes import (
    BlockingFakeSTT,
    FakeLLM,
    FakeSTT,
    wait_thread_event,
)
from tests.helpers import make_silence

API_KEY = "reliability-secret-token"


@pytest.fixture
def cfg():
    return load_config("config.yaml")


def build_app(cfg, stt=None, llm=None, admission=None, auth=None):
    stt = stt if stt is not None else FakeSTT()
    llm = llm if llm is not None else FakeLLM()
    app = create_app(
        cfg,
        Pipeline(stt, llm, cfg),
        admission=admission,
        auth=auth,
    )
    return app, stt, llm


def build_client(cfg, stt=None, llm=None, admission=None, auth=None):
    app, stt, llm = build_app(
        cfg, stt=stt, llm=llm, admission=admission, auth=auth
    )
    return TestClient(app), stt, llm


def send_start(ws, **overrides):
    payload = {"type": "start", "app_name": "x", "window_title": "y"}
    payload.update(overrides)
    ws.send_json(payload)


def wait_for(predicate, timeout: float = 3.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.01)
    return predicate()


async def wait_until_async(predicate, timeout: float = 3.0) -> None:
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout
    while loop.time() < deadline:
        if predicate():
            return
        await asyncio.sleep(0.01)
    assert predicate()


async def wait_event(event: threading.Event, timeout: float = 3.0) -> None:
    assert await asyncio.to_thread(wait_thread_event, event, timeout)


class ScriptedSTT(FakeSTT):
    def __init__(self, text: str):
        super().__init__()
        self.text = text

    def transcribe(self, audio):
        self.seen.append(audio)
        return self.text


class ExplodingLLM(FakeLLM):
    def clean(self, raw_text, profile, *, deadline=None):
        self.calls.append((raw_text, profile))
        raise RuntimeError(f"cleaner failed on {raw_text}")


class BrokenSTT(FakeSTT):
    def __init__(self, message: str = "stt failed"):
        super().__init__()
        self.message = message

    def transcribe(self, audio):
        raise RuntimeError(self.message)


def post_pcm(client, data=None, filename="d.pcm", headers=None):
    return client.post(
        "/v1/audio/transcriptions?format=pcm",
        data=data or {},
        files={
            "file": (
                filename,
                make_silence(0.2),
                "application/octet-stream",
            )
        },
        headers=headers or {},
    )


# -- readiness -----------------------------------------------------------


def test_ready_starts_503_then_200_after_warm_up(cfg):
    app, stt, llm = build_app(cfg)
    client = TestClient(app)

    startup = client.get("/ready")
    assert startup.status_code == 503
    assert startup.json() == {
        "ready": False,
        "busy": False,
        "draining": False,
        "queue_depth": 0,
        "stt_model": cfg.server.stt_model,
        "llm_model": cfg.server.llm_model,
    }

    app.state.ready = True
    warm = client.get("/ready")
    assert warm.status_code == 200
    assert warm.json()["ready"] is True
    assert warm.json()["busy"] is False
    assert warm.json()["draining"] is False
    assert warm.json()["queue_depth"] == 0
    assert stt.seen == []
    assert llm.calls == []


def test_ready_returns_503_again_when_shutdown_begins(cfg):
    app, _, _ = build_app(cfg)
    app.state.ready = True
    client = TestClient(app)
    assert client.get("/ready").status_code == 200

    app.state.ready = False
    shutdown = client.get("/ready")
    assert shutdown.status_code == 503
    assert shutdown.json()["ready"] is False


def test_ready_probe_does_not_touch_engines_or_counters(cfg):
    app, stt, llm = build_app(cfg)
    app.state.ready = True
    client = TestClient(app)
    admission = app.state.admission
    scheduler = app.state.scheduler
    before_admission = admission.snapshot()
    before_scheduler = scheduler.snapshot()

    for _ in range(3):
        assert client.get("/ready").status_code == 200

    assert stt.seen == []
    assert llm.calls == []
    assert admission.snapshot() == before_admission == AdmissionSnapshot(
        0, 0
    )
    assert scheduler.snapshot() == before_scheduler == SchedulerSnapshot(
        active=False, pending=0, draining=False
    )


def test_ready_is_protected_but_health_stays_public(cfg):
    app, _, _ = build_app(cfg, auth=ApiKeyPolicy(API_KEY))
    app.state.ready = True
    client = TestClient(app)

    assert client.get("/health").status_code == 200
    assert client.get("/ready").status_code == 401

    authorized = client.get(
        "/ready", headers={"Authorization": f"Bearer {API_KEY}"}
    )
    assert authorized.status_code == 200
    assert authorized.json()["ready"] is True


def test_ready_is_public_when_auth_is_disabled(cfg):
    app, _, _ = build_app(cfg)
    app.state.ready = True
    assert TestClient(app).get("/ready").status_code == 200


@pytest.mark.asyncio
async def test_ready_reports_busy_and_queue_depth_while_engine_runs(cfg):
    cfg.timeouts.queue_seconds = 5.0
    cfg.timeouts.processing_seconds = 5.0
    stt = BlockingFakeSTT()
    app, _, _ = build_app(cfg, stt=stt)
    app.state.ready = True
    scheduler = app.state.scheduler
    admission = app.state.admission
    pcm = make_silence(0.1)

    first_lease = admission.acquire()
    first_lease.reserve(len(pcm))
    first = asyncio.create_task(
        scheduler.submit(pcm, "default", True, first_lease)
    )
    await wait_event(stt.started)

    second_lease = admission.acquire()
    second_lease.reserve(len(pcm))
    second = asyncio.create_task(
        scheduler.submit(pcm, "default", True, second_lease)
    )
    await wait_until_async(lambda: scheduler.snapshot().pending == 1)
    starts_before = stt.starts

    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(
        transport=transport, base_url="http://test"
    ) as async_client:
        response = await async_client.get("/ready")
        health = await async_client.get("/health")

    assert response.status_code == 200
    body = response.json()
    assert body["ready"] is True
    assert body["busy"] is True
    assert body["draining"] is False
    assert body["queue_depth"] == 1
    assert body["stt_model"] == cfg.server.stt_model
    assert body["llm_model"] == cfg.server.llm_model
    assert health.status_code == 200
    assert health.json() == {"status": "ok"}
    assert stt.starts == starts_before

    stt.release.set()
    await asyncio.gather(first, second)

    assert await asyncio.to_thread(
        wait_for, lambda: admission.snapshot() == AdmissionSnapshot(0, 0)
    )
    await scheduler.close()


@pytest.mark.asyncio
async def test_ready_stays_200_while_draining_after_processing_timeout(cfg):
    cfg.timeouts.queue_seconds = 5.0
    cfg.timeouts.processing_seconds = 0.05
    stt = BlockingFakeSTT()
    app, _, _ = build_app(cfg, stt=stt)
    app.state.ready = True
    scheduler = app.state.scheduler
    admission = app.state.admission
    pcm = make_silence(0.1)

    lease = admission.acquire()
    lease.reserve(len(pcm))
    job = asyncio.create_task(
        scheduler.submit(pcm, "default", True, lease)
    )
    await wait_event(stt.started)
    result = (await asyncio.gather(job, return_exceptions=True))[0]
    assert isinstance(result, DictationFailure)
    assert result.code == "processing_timeout"
    await wait_until_async(lambda: scheduler.snapshot().draining)

    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(
        transport=transport, base_url="http://test"
    ) as async_client:
        response = await async_client.get("/ready")

    assert response.status_code == 200
    body = response.json()
    assert body["ready"] is True
    assert body["draining"] is True
    assert body["busy"] is True

    stt.release.set()
    await wait_until_async(
        lambda: scheduler.snapshot()
        == SchedulerSnapshot(active=False, pending=0, draining=False)
    )
    await wait_until_async(
        lambda: admission.snapshot() == AdmissionSnapshot(0, 0)
    )
    await scheduler.close()


def test_health_shape_is_unchanged(cfg):
    client, _, _ = build_client(cfg)
    response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


# -- warnings and legacy compatibility ----------------------------------


def test_rest_json_keeps_legacy_shape_and_adds_warnings(cfg):
    client, stt, _ = build_client(
        cfg,
        stt=ScriptedSTT("fast a pi su docker"),
        llm=ExplodingLLM(),
    )

    response = post_pcm(client)

    assert response.status_code == 200
    body = response.json()
    assert body["text"] == "fast a pi su Docker"
    assert body["raw_text"] == "fast a pi su docker"
    assert body["fast_path"] is False
    assert set(body["metrics"]) == {"stt_ms", "llm_ms", "total_ms"}
    assert body["warnings"] == ["llm_failed"]
    assert len(stt.seen) == 1


def test_rest_text_adds_warning_header_only_when_warnings_exist(cfg):
    warning_client, _, _ = build_client(
        cfg,
        stt=ScriptedSTT("fast a pi su docker"),
        llm=ExplodingLLM(),
    )
    warning = warning_client.post(
        "/v1/audio/transcriptions?format=pcm",
        data={"response_format": "text"},
        files={
            "file": (
                "d.pcm",
                make_silence(0.2),
                "application/octet-stream",
            )
        },
    )
    assert warning.status_code == 200
    assert warning.headers["content-type"].startswith("text/plain")
    assert warning.text == "fast a pi su Docker"
    assert warning.headers["x-gangiokeet-warnings"] == "llm_failed"

    clean_client, _, _ = build_client(cfg, stt=ScriptedSTT("ciao"))
    clean = clean_client.post(
        "/v1/audio/transcriptions?format=pcm",
        data={"response_format": "text"},
        files={
            "file": (
                "d.pcm",
                make_silence(0.2),
                "application/octet-stream",
            )
        },
    )
    assert clean.status_code == 200
    assert clean.text == "[default] ciao"
    assert "x-gangiokeet-warnings" not in clean.headers


def test_ws_result_adds_warnings_and_fallback_keeps_fast_path_false(cfg):
    client, _, _ = build_client(
        cfg,
        stt=ScriptedSTT("fast a pi su docker"),
        llm=ExplodingLLM(),
    )

    with client.websocket_connect("/ws/dictate") as ws:
        send_start(ws, profile="chat")
        ws.send_bytes(make_silence(0.2))
        ws.send_json({"type": "stop"})
        result = ws.receive_json()

    assert result["type"] == "result"
    assert result["warnings"] == ["llm_failed"]
    assert result["fast_path"] is False
    assert result["final_text"] == "fast a pi su Docker"


# -- REST error contract -------------------------------------------------


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("code", "status", "retryable"),
    [
        ("unauthorized", 401, False),
        ("busy", 503, True),
        ("queue_timeout", 503, True),
        ("decode_timeout", 504, True),
        ("upload_timeout", 408, True),
        ("processing_timeout", 504, True),
        ("invalid_audio", 422, False),
        ("payload_too_large", 413, False),
        ("unsupported_parameter", 400, False),
        ("decoder_unavailable", 503, False),
        ("internal", 500, False),
    ],
)
async def test_rest_error_handler_maps_every_code(
    cfg, code, status, retryable
):
    app, _, _ = build_app(cfg)
    handler = app.exception_handlers[DictationFailure]
    response = await handler(
        None, DictationFailure(code, "boom", retryable=retryable)
    )

    assert response.status_code == status
    body = json.loads(response.body)
    assert body == {
        "detail": {
            "code": code,
            "message": "boom",
            "retryable": retryable,
        }
    }
    if code in {"busy", "queue_timeout"}:
        assert response.headers["retry-after"] == "1"
    else:
        assert "retry-after" not in response.headers


def test_rest_busy_returns_retry_after(cfg):
    admission = AdmissionController(max_sessions=4, max_bytes=1)
    client, stt, llm = build_client(cfg, admission=admission)

    response = post_pcm(client)

    assert response.status_code == 503
    assert response.headers["retry-after"] == "1"
    detail = response.json()["detail"]
    assert detail == {
        "code": "busy",
        "message": "audio budget exhausted",
        "retryable": True,
    }
    assert admission.snapshot() == AdmissionSnapshot(0, 0)
    assert stt.seen == []
    assert llm.calls == []


def test_rest_decode_timeout_maps_to_504(cfg, monkeypatch):
    async def timed_out(*_args, **_kwargs):
        raise DictationFailure(
            "decode_timeout", "audio decoding timed out", retryable=True
        )

    monkeypatch.setattr("dictation_server.server.decode_upload", timed_out)
    client, stt, _ = build_client(cfg)

    response = post_pcm(client)

    assert response.status_code == 504
    assert response.json()["detail"]["code"] == "decode_timeout"
    assert stt.seen == []


def test_rest_malformed_pcm_is_422_and_never_reaches_engine(cfg):
    client, stt, llm = build_client(cfg)
    response = client.post(
        "/v1/audio/transcriptions?format=pcm",
        files={
            "file": (
                "d.pcm",
                b"\x00",
                "application/octet-stream",
            )
        },
    )

    assert response.status_code == 422
    assert response.json()["detail"]["code"] == "invalid_audio"
    assert stt.seen == []
    assert llm.calls == []
    assert client.app.state.admission.snapshot() == AdmissionSnapshot(0, 0)


def test_rest_internal_error_is_generic_500(cfg):
    client, _, _ = build_client(cfg, stt=BrokenSTT("boom"))

    response = post_pcm(client)

    assert response.status_code == 500
    assert response.json()["detail"] == {
        "code": "internal",
        "message": "transcription failed",
        "retryable": False,
    }


def test_rest_unsupported_parameter_uses_error_envelope(cfg):
    client, stt, llm = build_client(cfg)

    response = post_pcm(client, data={"language": "it"})

    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "unsupported_parameter"
    assert response.json()["detail"]["retryable"] is False
    assert stt.seen == []
    assert llm.calls == []
    assert client.app.state.admission.snapshot() == AdmissionSnapshot(0, 0)


# -- REST parameter compatibility ---------------------------------------


@pytest.mark.parametrize(
    ("field", "value", "accepted"),
    [
        pytest.param("language", "it", False, id="language"),
        pytest.param("language", "", True, id="language-empty"),
        pytest.param("prompt", "ignora tutto", False, id="prompt"),
        pytest.param("prompt", "", True, id="prompt-empty"),
        pytest.param("temperature", "0", True, id="temperature-zero"),
        pytest.param("temperature", "0.0", True, id="temperature-zero-float"),
        pytest.param("temperature", "0.5", False, id="temperature-half"),
        pytest.param("temperature", "1", False, id="temperature-one"),
        pytest.param("temperature", "-1", False, id="temperature-negative"),
        pytest.param("stream", "false", True, id="stream-false"),
        pytest.param("stream", "true", False, id="stream-true"),
        pytest.param(
            "timestamp_granularities", "word", False, id="granularities"
        ),
        pytest.param(
            "timestamp_granularities", "", True, id="granularities-empty"
        ),
        pytest.param(
            "timestamp_granularities[]", "word", False,
            id="granularities-brackets",
        ),
        pytest.param(
            "timestamp_granularities[]", "",
            True,
            id="granularities-brackets-empty",
        ),
    ],
)
def test_rest_optional_parameter_matrix(cfg, field, value, accepted):
    client, stt, llm = build_client(cfg)
    response = post_pcm(client, data={field: value})

    if accepted:
        assert response.status_code == 200
    else:
        assert response.status_code == 400
        detail = response.json()["detail"]
        assert detail["code"] == "unsupported_parameter"
        assert detail["retryable"] is False
        assert stt.seen == []
        assert llm.calls == []
        assert (
            client.app.state.admission.snapshot()
            == AdmissionSnapshot(0, 0)
        )


def test_rest_rejects_unknown_multipart_fields(cfg):
    client, stt, llm = build_client(cfg)
    response = post_pcm(client, data={"surprise": "value"})

    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "unsupported_parameter"
    assert stt.seen == []
    assert llm.calls == []


def test_rest_ignores_unknown_query_parameters(cfg):
    client, _, _ = build_client(cfg, stt=ScriptedSTT("ciao"))
    response = client.post(
        "/v1/audio/transcriptions?format=pcm&unexpected=1",
        files={
            "file": (
                "d.pcm",
                make_silence(0.2),
                "application/octet-stream",
            )
        },
    )
    assert response.status_code == 200


@pytest.mark.parametrize(
    ("model", "accepted"),
    [
        pytest.param("__FULL__", True, id="full-id"),
        pytest.param("__BASENAME__", True, id="basename"),
        pytest.param("whisper-1", False, id="foreign-model"),
    ],
)
def test_rest_model_accepts_only_configured_aliases(cfg, model, accepted):
    if model == "__FULL__":
        model = cfg.server.stt_model
    elif model == "__BASENAME__":
        model = cfg.server.stt_model.rsplit("/", 1)[-1]
    client, stt, _ = build_client(cfg)
    response = post_pcm(client, data={"model": model})

    if accepted:
        assert response.status_code == 200
    else:
        assert response.status_code == 400
        assert response.json()["detail"]["code"] == (
            "unsupported_parameter"
        )
        assert stt.seen == []


# -- WebSocket error contract -------------------------------------------


def test_ws_invalid_pcm_returns_idle_and_connection_is_reusable(cfg):
    client, stt, llm = build_client(cfg)

    with client.websocket_connect("/ws/dictate") as ws:
        send_start(ws)
        ws.send_bytes(b"\x00")
        ws.send_json({"type": "stop"})
        error = ws.receive_json()
        assert error["type"] == "error"
        assert error["code"] == "invalid_audio"
        assert error["retryable"] is False
        assert stt.seen == []
        assert llm.calls == []

        send_start(ws)
        ws.send_bytes(make_silence(0.2))
        ws.send_json({"type": "stop"})
        result = ws.receive_json()

    assert result["type"] == "result"
    assert len(stt.seen) == 1
    assert wait_for(
        lambda: client.app.state.admission.snapshot()
        == AdmissionSnapshot(0, 0)
    )


def test_ws_internal_failure_is_generic_and_releases_state(cfg):
    client, _, _ = build_client(cfg, stt=BrokenSTT("boom"))

    with client.websocket_connect("/ws/dictate") as ws:
        send_start(ws)
        ws.send_bytes(make_silence(0.2))
        ws.send_json({"type": "stop"})
        error = ws.receive_json()

        assert error == {
            "type": "error",
            "code": "internal",
            "message": "processing failed",
            "retryable": False,
        }

        # The session is idle again: a new start is accepted.
        send_start(ws)
        ws.send_bytes(make_silence(0.2))
        ws.send_json({"type": "stop"})
        assert ws.receive_json()["code"] == "internal"

    assert wait_for(
        lambda: client.app.state.admission.snapshot()
        == AdmissionSnapshot(0, 0)
    )


def test_ws_payload_too_large_still_closes_1009(cfg):
    cfg.limits.max_audio_bytes = 1024
    client, _, _ = build_client(cfg)

    with client.websocket_connect("/ws/dictate") as ws:
        send_start(ws)
        ws.send_bytes(b"\x00" * 1025)
        error = ws.receive_json()
        assert error["code"] == "payload_too_large"
        with pytest.raises(WebSocketDisconnect) as info:
            ws.receive_json()
        assert info.value.code == 1009


# -- multi-client composition -------------------------------------------


def test_two_ws_clients_and_rest_share_queue_budget_and_keep_profiles(cfg):
    cfg.timeouts.queue_seconds = 5.0
    cfg.timeouts.processing_seconds = 5.0
    stt = BlockingFakeSTT()
    client, _, llm = build_client(cfg, stt=stt)
    admission = client.app.state.admission
    scheduler = client.app.state.scheduler
    audio = make_silence(0.2)
    rest_results = []

    with client:
        with client.websocket_connect("/ws/dictate") as first:
            send_start(first, profile="code")
            first.send_bytes(audio)
            first.send_json({"type": "stop"})
            assert wait_for(lambda: stt.started.is_set())

            with client.websocket_connect("/ws/dictate") as second:
                send_start(second, profile="email", bypass_llm=True)
                second.send_bytes(audio)
                second.send_json({"type": "stop"})
                assert wait_for(
                    lambda: scheduler.snapshot().pending == 1
                )

                def upload():
                    rest_results.append(
                        client.post(
                            "/v1/audio/transcriptions?format=pcm",
                            data={"profile": "email"},
                            files={
                                "file": (
                                    "d.pcm",
                                    audio,
                                    "application/octet-stream",
                                )
                            },
                        )
                    )

                thread = threading.Thread(target=upload)
                thread.start()
                try:
                    assert wait_for(
                        lambda: scheduler.snapshot().pending == 2
                    )
                    shared = admission.snapshot()
                    assert shared.sessions == 3
                    assert shared.retained_audio_bytes == 3 * len(audio)
                    assert stt.starts == 1
                finally:
                    stt.release.set()
                    thread.join(timeout=5)

                assert not thread.is_alive()
                first_result = first.receive_json()
                second_result = second.receive_json()

    assert first_result["type"] == "result"
    assert first_result["final_text"].startswith("[code]")
    assert first_result["fast_path"] is False
    assert second_result["type"] == "result"
    assert second_result["final_text"] == second_result["raw_text"]
    assert second_result["fast_path"] is True
    assert [r.status_code for r in rest_results] == [200]
    rest_body = rest_results[0].json()
    assert rest_body["text"].startswith("[email]")
    assert {profile for _, profile in llm.calls} == {"code", "email"}

    assert wait_for(
        lambda: admission.snapshot() == AdmissionSnapshot(0, 0)
    )
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=False, pending=0, draining=False
    )


def test_disconnect_and_abort_leave_admission_zero_and_scheduler_idle(cfg):
    cfg.timeouts.queue_seconds = 5.0
    cfg.timeouts.processing_seconds = 5.0
    stt = BlockingFakeSTT()
    client, _, _ = build_client(cfg, stt=stt)
    admission = client.app.state.admission
    scheduler = client.app.state.scheduler

    with client:
        with client.websocket_connect("/ws/dictate") as first:
            send_start(first)
            first.send_bytes(make_silence(0.2))
            first.send_json({"type": "stop"})
            assert wait_for(lambda: stt.started.is_set())

            with client.websocket_connect("/ws/dictate") as second:
                send_start(second)
                second.send_bytes(make_silence(0.2))
                second.send_json({"type": "stop"})
                assert wait_for(
                    lambda: scheduler.snapshot().pending == 1
                )

            # Disconnecting the queued client must drop its pending job and
            # release its slot and bytes.
            assert wait_for(
                lambda: scheduler.snapshot().pending == 0
            )
            assert wait_for(
                lambda: admission.snapshot().sessions == 1
            )

            first.send_json({"type": "abort"})
            stt.release.set()
            assert wait_for(
                lambda: admission.snapshot() == AdmissionSnapshot(0, 0)
            )

        assert wait_for(
            lambda: scheduler.snapshot()
            == SchedulerSnapshot(active=False, pending=0, draining=False)
        )
        assert wait_for(
            lambda: admission.snapshot() == AdmissionSnapshot(0, 0)
        )


# -- logging -------------------------------------------------------------


def test_logs_never_contain_dictation_text_metadata_or_tokens(cfg, caplog):
    raw_secret = "contenuto dettato riservato"
    metadata_secrets = [
        "App Riservata",
        "Finestra Riservata",
        "testo selezionato riservato",
    ]
    filename_secret = "audio-riservato.pcm"
    token_secret = API_KEY

    class SecretSTT(FakeSTT):
        def transcribe(self, audio):
            raise RuntimeError(raw_secret)

    app, _, _ = build_app(
        cfg, stt=SecretSTT(), auth=ApiKeyPolicy(token_secret)
    )
    client = TestClient(app)

    with caplog.at_level(logging.DEBUG):
        with client.websocket_connect(
            "/ws/dictate",
            headers={"Authorization": f"Bearer {token_secret}"},
        ) as ws:
            send_start(
                ws,
                app_name=metadata_secrets[0],
                window_title=metadata_secrets[1],
                selected_text=metadata_secrets[2],
            )
            ws.send_bytes(make_silence(0.2))
            ws.send_json({"type": "stop"})
            assert ws.receive_json()["code"] == "internal"

        response = client.post(
            "/v1/audio/transcriptions?format=pcm",
            files={
                "file": (
                    filename_secret,
                    make_silence(0.2),
                    "application/octet-stream",
                )
            },
            headers={"Authorization": f"Bearer {token_secret}"},
        )
        assert response.status_code == 500

    for secret in [raw_secret, filename_secret, token_secret, *metadata_secrets]:
        assert secret not in caplog.text
