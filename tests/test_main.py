import plistlib
import subprocess
import sys
from pathlib import Path

import pytest

from dictation_server.config import load_config
from dictation_server.main import build_app, main, serve
from dictation_server.protocol import MAX_AUDIO_BYTES
from tests.fakes import EagerFakeLLM, EagerFakeSTT


def test_build_app_order_load_freeze_and_pipeline():
    cfg = load_config("config.yaml")
    stt, llm = EagerFakeSTT(), EagerFakeLLM()
    app = build_app(cfg, stt=stt, llm=llm)
    assert stt.loads == ["load"]
    assert llm.loads == ["load", "freeze"]
    assert "FastAPI" in llm.frozen_with
    assert "fast a pi" in llm.frozen_with
    assert app.state.pipeline is not None
    assert app.state.pipeline.stt is stt
    assert app.state.pipeline.llm is llm


def test_build_app_global_engine_order():
    cfg = load_config("config.yaml")
    events = []

    class RecordingSTT(EagerFakeSTT):
        def load(self):
            events.append("stt.load")
            super().load()

    class RecordingLLM(EagerFakeLLM):
        def load(self):
            events.append("llm.load")
            super().load()

        def freeze(self, system_prompt):
            events.append("llm.freeze")
            super().freeze(system_prompt)

    build_app(cfg, stt=RecordingSTT(), llm=RecordingLLM())
    assert events == ["stt.load", "llm.load", "llm.freeze"]


@pytest.mark.asyncio
async def test_serve_runs_warm_up_before_binding():
    cfg = load_config("config.yaml")
    stt, llm = EagerFakeSTT(), EagerFakeLLM()
    app = build_app(cfg, stt=stt, llm=llm)
    pipeline = app.state.pipeline
    events = []

    async def fake_warm_up():
        events.append("warm_up")

    pipeline.warm_up = fake_warm_up

    class FakeServer:
        async def serve(self):
            events.append("serve")

    await serve(cfg, app, pipeline, uvicorn_server=FakeServer())
    assert events == ["warm_up", "serve"]


@pytest.mark.asyncio
async def test_serve_configures_ws_max_size(monkeypatch):
    cfg = load_config("config.yaml")
    seen = {}

    class StubPipeline:
        async def warm_up(self):
            seen["warm_up"] = True

    class FakeConfig:
        def __init__(self, app, **kwargs):
            seen["app"] = app
            seen.update(kwargs)

    class FakeServer:
        def __init__(self, config):
            seen["config"] = config

        async def serve(self):
            seen["served"] = True

    monkeypatch.setattr("dictation_server.main.uvicorn.Config", FakeConfig)
    monkeypatch.setattr("dictation_server.main.uvicorn.Server", FakeServer)

    app = object()
    await serve(cfg, app, StubPipeline())

    assert seen["app"] is app
    assert seen["host"] == cfg.server.host
    assert seen["port"] == cfg.server.port
    assert seen["ws_max_size"] == MAX_AUDIO_BYTES
    assert seen["warm_up"] is True
    assert seen["served"] is True


@pytest.mark.parametrize(
    "argv, expected_path",
    [([], "./config.yaml"), (["--config", "custom.yaml"], "custom.yaml")],
)
def test_main_loads_config_and_serves(monkeypatch, argv, expected_path):
    cfg = load_config("config.yaml")
    seen = {}

    def fake_load_config(path):
        seen["config_path"] = path
        return cfg

    class FakeApp:
        state = type("State", (), {"pipeline": "pipeline"})()

    app = FakeApp()

    def fake_build_app(received_cfg):
        seen["build_cfg"] = received_cfg
        return app

    async def fake_serve(received_cfg, received_app, pipeline):
        seen["serve"] = (received_cfg, received_app, pipeline)

    monkeypatch.setattr(
        "dictation_server.main.load_config", fake_load_config
    )
    monkeypatch.setattr("dictation_server.main.build_app", fake_build_app)
    monkeypatch.setattr("dictation_server.main.serve", fake_serve)

    main(argv)

    assert seen["config_path"] == expected_path
    assert seen["build_cfg"] is cfg
    assert seen["serve"] == (cfg, app, "pipeline")


def test_launchd_plist_template():
    with open("deploy/com.gangio.mlx-dictation.plist", "rb") as fh:
        plist = plistlib.load(fh)

    assert plist["Label"] == "com.gangio.mlx-dictation"
    assert plist["RunAtLoad"] is True
    assert plist["KeepAlive"] is True
    assert plist["ProgramArguments"] == [
        "__UV_PATH__",
        "run",
        "python",
        "-m",
        "dictation_server.main",
    ]
    assert plist["WorkingDirectory"] == "/Users/gangio75/GangioKeet"
    assert plist["EnvironmentVariables"]["PATH"].split(":") == [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        "/usr/sbin",
        "/sbin",
    ]
    assert plist["StandardOutPath"].startswith(
        "/Users/gangio75/Library/Logs/mlx-dictation/"
    )
    assert plist["StandardErrPath"].startswith(
        "/Users/gangio75/Library/Logs/mlx-dictation/"
    )


def test_install_launchd_script_is_valid_and_idempotent():
    script = Path("deploy/install_launchd.sh")
    assert script.exists()
    subprocess.run(["bash", "-n", str(script)], check=True)

    text = script.read_text()
    assert "command -v uv" in text
    assert 'sed "s|__UV_PATH__|$ESCAPED_UV_PATH|g"' in text
    assert "bootout" in text
    assert "unload" in text
    assert "bootstrap" in text
    assert "launchctl load" in text


def test_module_entrypoint_runs_main():
    result = subprocess.run(
        [sys.executable, "-m", "dictation_server.main", "--help"],
        capture_output=True,
        text=True,
        check=True,
    )
    assert "usage:" in result.stdout
