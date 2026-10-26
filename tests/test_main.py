import logging
import plistlib
import stat
import subprocess
import sys
from pathlib import Path

import pytest

from dictation_server.config import load_config
from dictation_server.errors import DictationFailure
from dictation_server.main import build_app, main, serve
from dictation_server.protocol import MAX_AUDIO_BYTES
from dictation_server.scheduler import DictationScheduler
from tests.fakes import EagerFakeLLM, EagerFakeSTT

REPO_ROOT = Path(__file__).resolve().parent.parent
INSTALLER = REPO_ROOT / "deploy/install_launchd.sh"
TEMPLATE = REPO_ROOT / "deploy/com.gangio.mlx-dictation.plist"


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


@pytest.mark.parametrize(
    "value", [None, ""], ids=["missing", "empty"]
)
def test_build_app_fails_before_loading_models_without_api_key(
    monkeypatch, value
):
    variable = "GANGIOKEET_TEST_API_KEY"
    cfg = load_config("config.yaml")
    cfg.security.api_key_env = variable
    if value is None:
        monkeypatch.delenv(variable, raising=False)
    else:
        monkeypatch.setenv(variable, value)
    stt, llm = EagerFakeSTT(), EagerFakeLLM()

    with pytest.raises(ValueError) as info:
        build_app(cfg, stt=stt, llm=llm)

    assert variable in str(info.value)
    assert stt.loads == []
    assert llm.loads == []


def test_build_app_enables_optional_bearer_auth(monkeypatch):
    variable = "GANGIOKEET_TEST_API_KEY"
    token = "expected-test-token"
    monkeypatch.setenv(variable, token)
    cfg = load_config("config.yaml")
    cfg.security.api_key_env = variable
    stt, llm = EagerFakeSTT(), EagerFakeLLM()

    app = build_app(cfg, stt=stt, llm=llm)

    assert app.state.auth.enabled is True
    app.state.auth.authorize(f"Bearer {token}")
    with pytest.raises(DictationFailure):
        app.state.auth.authorize("Bearer wrong-token")
    assert stt.loads == ["load"]
    assert llm.loads == ["load", "freeze"]


def test_create_app_exposes_one_scheduler_from_config():
    cfg = load_config("config.yaml")
    app = build_app(cfg, stt=EagerFakeSTT(), llm=EagerFakeLLM())

    scheduler = app.state.scheduler
    assert isinstance(scheduler, DictationScheduler)
    assert app.state.scheduler is scheduler
    assert scheduler.snapshot().active is False
    assert scheduler.snapshot().pending == 0


def test_create_app_starts_not_ready():
    cfg = load_config("config.yaml")
    app = build_app(cfg, stt=EagerFakeSTT(), llm=EagerFakeLLM())
    assert app.state.ready is False


@pytest.mark.asyncio
async def test_serve_starts_scheduler_then_warm_up_then_binds_and_closes():
    cfg = load_config("config.yaml")
    stt, llm = EagerFakeSTT(), EagerFakeLLM()
    app = build_app(cfg, stt=stt, llm=llm)
    pipeline = app.state.pipeline
    events = []

    async def fake_start():
        events.append(("scheduler.start", app.state.ready))

    async def fake_close():
        events.append(("scheduler.close", app.state.ready))

    async def fake_warm_up():
        events.append(("warm_up", app.state.ready))

    pipeline.warm_up = fake_warm_up
    app.state.scheduler.start = fake_start
    app.state.scheduler.close = fake_close

    class FakeServer:
        async def serve(self):
            events.append(("serve", app.state.ready))

    await serve(cfg, app, pipeline, uvicorn_server=FakeServer())
    assert events == [
        ("scheduler.start", False),
        ("warm_up", False),
        ("serve", True),
        ("scheduler.close", False),
    ]
    assert app.state.ready is False


@pytest.mark.asyncio
async def test_serve_closes_scheduler_when_serving_fails():
    cfg = load_config("config.yaml")
    stt, llm = EagerFakeSTT(), EagerFakeLLM()
    app = build_app(cfg, stt=stt, llm=llm)
    pipeline = app.state.pipeline
    events = []

    async def fake_start():
        events.append(("scheduler.start", app.state.ready))

    async def fake_close():
        events.append(("scheduler.close", app.state.ready))

    async def fake_warm_up():
        events.append(("warm_up", app.state.ready))

    pipeline.warm_up = fake_warm_up
    app.state.scheduler.start = fake_start
    app.state.scheduler.close = fake_close

    class FakeServer:
        async def serve(self):
            events.append(("serve", app.state.ready))
            raise RuntimeError("bind failed")

    with pytest.raises(RuntimeError, match="bind failed"):
        await serve(cfg, app, pipeline, uvicorn_server=FakeServer())

    assert events == [
        ("scheduler.start", False),
        ("warm_up", False),
        ("serve", True),
        ("scheduler.close", False),
    ]
    assert app.state.ready is False


@pytest.mark.asyncio
async def test_serve_logs_readiness_transitions(caplog):
    cfg = load_config("config.yaml")
    app = build_app(cfg, stt=EagerFakeSTT(), llm=EagerFakeLLM())
    pipeline = app.state.pipeline

    async def fake_start():
        pass

    async def fake_close():
        pass

    async def fake_warm_up():
        pass

    pipeline.warm_up = fake_warm_up
    app.state.scheduler.start = fake_start
    app.state.scheduler.close = fake_close

    class FakeServer:
        async def serve(self):
            pass

    with caplog.at_level(logging.INFO, logger="dictation_server.main"):
        await serve(cfg, app, pipeline, uvicorn_server=FakeServer())

    assert "warm-up complete ready=true" in caplog.text
    assert "shutdown ready=false" in caplog.text


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


def test_configure_logging_enables_info_for_production_entrypoint(caplog):
    logger = logging.getLogger("dictation_server")
    old_level = logger.level
    old_handlers = list(logger.handlers)
    old_propagate = logger.propagate
    old_disabled = logger.disabled

    try:
        logger.handlers[:] = []
        logger.setLevel(logging.WARNING)
        logger.propagate = True
        logger.disabled = False

        from dictation_server.main import configure_logging

        configure_logging()

        assert logger.getEffectiveLevel() == logging.INFO
        assert any(
            handler.level <= logging.INFO for handler in logger.handlers
        )
        handler_count = len(logger.handlers)

        configure_logging()
        assert len(logger.handlers) == handler_count

        logging.getLogger("dictation_server.reliability").info(
            "ready=true state=test"
        )
        assert "ready=true state=test" in caplog.text
    finally:
        for handler in list(logger.handlers):
            if handler not in old_handlers:
                logger.removeHandler(handler)
                handler.close()
        logger.handlers[:] = old_handlers
        logger.setLevel(old_level)
        logger.propagate = old_propagate
        logger.disabled = old_disabled


def test_main_configures_logging_before_startup(monkeypatch):
    cfg = load_config("config.yaml")
    events = []

    monkeypatch.setattr(
        "dictation_server.main.configure_logging",
        lambda: events.append("logging"),
    )
    monkeypatch.setattr(
        "dictation_server.main.load_config", lambda path: cfg
    )

    class FakeApp:
        state = type("State", (), {"pipeline": "pipeline"})()

    monkeypatch.setattr(
        "dictation_server.main.build_app", lambda received: FakeApp()
    )

    async def fake_serve(received_cfg, received_app, pipeline):
        events.append("serve")

    monkeypatch.setattr("dictation_server.main.serve", fake_serve)

    main([])

    assert events == ["logging", "serve"]


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
    monkeypatch.setattr(
        "dictation_server.main.configure_logging", lambda: None
    )

    main(argv)

    assert seen["config_path"] == expected_path
    assert seen["build_cfg"] is cfg
    assert seen["serve"] == (cfg, app, "pipeline")


def test_launchd_plist_template():
    with open(TEMPLATE, "rb") as fh:
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
    assert "GANGIOKEET_API_KEY" not in TEMPLATE.read_text()
    assert "api_key" not in plist["EnvironmentVariables"]


def test_install_launchd_script_is_valid_and_idempotent():
    script = INSTALLER
    assert script.exists()
    subprocess.run(["bash", "-n", str(script)], check=True)

    text = script.read_text()
    assert "command -v uv" in text
    assert 'sed "s|__UV_PATH__|$ESCAPED_UV_PATH|g"' in text
    assert "bootout" in text
    assert "unload" in text
    assert "bootstrap" in text
    assert "launchctl load" in text

    # The key is read from the environment by an embedded Python snippet,
    # never expanded by the shell or passed through argv.
    assert "os.environ" in text
    assert "${GANGIOKEET_API_KEY}" not in text
    assert '"$GANGIOKEET_API_KEY"' not in text


def run_installer(tmp_path, api_key):
    home = tmp_path / "home"
    home.mkdir()
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()

    uv = bin_dir / "uv"
    uv.write_text("#!/usr/bin/env bash\nexit 0\n")
    uv.chmod(0o755)

    launchctl_log = tmp_path / "launchctl.log"
    launchctl = bin_dir / "launchctl"
    launchctl.write_text(
        "#!/usr/bin/env bash\n"
        'printf \'%s\\n\' "$*" >> "$LAUNCHCTL_LOG"\n'
        "exit 0\n"
    )
    launchctl.chmod(0o755)

    (bin_dir / "python3").symlink_to(sys.executable)

    env = {
        "HOME": str(home),
        "PATH": f"{bin_dir}:/usr/bin:/bin:/usr/sbin:/sbin",
        "LAUNCHCTL_LOG": str(launchctl_log),
    }
    if api_key is not None:
        env["GANGIOKEET_API_KEY"] = api_key

    result = subprocess.run(
        ["bash", str(INSTALLER)],
        env=env,
        capture_output=True,
        text=True,
    )
    target = home / "Library/LaunchAgents/com.gangio.mlx-dictation.plist"
    return result, target, launchctl_log, bin_dir


def test_installer_embeds_api_key_only_in_private_local_plist(tmp_path):
    api_key = "s3cr3t-&|;token"
    result, target, launchctl_log, bin_dir = run_installer(
        tmp_path, api_key
    )

    assert result.returncode == 0, result.stderr
    assert api_key not in result.stdout
    assert api_key not in result.stderr

    plist = plistlib.loads(target.read_bytes())
    assert (
        plist["EnvironmentVariables"]["GANGIOKEET_API_KEY"] == api_key
    )
    assert plist["ProgramArguments"][0] == str(bin_dir / "uv")
    assert stat.S_IMODE(target.stat().st_mode) == 0o600

    log = launchctl_log.read_text()
    assert "bootout" in log
    assert "unload" in log
    assert "bootstrap" in log


def test_installer_omits_api_key_when_unset(tmp_path):
    result, target, _, _ = run_installer(tmp_path, None)

    assert result.returncode == 0, result.stderr
    plist = plistlib.loads(target.read_bytes())
    assert "GANGIOKEET_API_KEY" not in plist["EnvironmentVariables"]
    assert stat.S_IMODE(target.stat().st_mode) == 0o600
    assert "GANGIOKEET_API_KEY" not in result.stdout
    assert "GANGIOKEET_API_KEY" not in result.stderr


def test_module_entrypoint_runs_main():
    result = subprocess.run(
        [sys.executable, "-m", "dictation_server.main", "--help"],
        capture_output=True,
        text=True,
        check=True,
    )
    assert "usage:" in result.stdout
