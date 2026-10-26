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
