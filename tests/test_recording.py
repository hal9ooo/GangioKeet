import json
import shutil
import stat
import sys
import wave
from pathlib import Path

from fastapi.testclient import TestClient

from dictation_server.config import load_config
from dictation_server.pipeline import Pipeline
from dictation_server.server import create_app
from tests.fakes import FakeLLM, FakeSTT
from tests.helpers import make_silence

ROOT = Path(__file__).resolve().parents[1]
CONFIG_PATH = ROOT / "config.yaml"
sys.path.insert(0, str(ROOT / "scripts"))

from stt_eval import load_manifest  # noqa: E402


def enabled_config(tmp_path: Path, *, store_context: bool = False):
    cfg = load_config(CONFIG_PATH)
    cfg.recording.enabled = True
    cfg.recording.directory = str(tmp_path / "recordings")
    cfg.recording.store_context = store_context
    return cfg


def build_client(cfg) -> TestClient:
    return TestClient(create_app(cfg, Pipeline(FakeSTT(), FakeLLM(), cfg)))


def send_websocket_dictation(
    client: TestClient,
    pcm: bytes,
    *,
    profile: str = "code",
    app_name: str = "code.exe",
    window_title: str = "secret.txt",
    selected_text: str = "private selection",
) -> dict:
    with client.websocket_connect("/ws/dictate") as ws:
        ws.send_json(
            {
                "type": "start",
                "profile": profile,
                "app_name": app_name,
                "window_title": window_title,
                "selected_text": selected_text,
            }
        )
        ws.send_bytes(pcm)
        ws.send_json({"type": "stop"})
        return ws.receive_json()


def read_manifest(directory: Path) -> list[dict]:
    manifest = directory / "manifest.jsonl"
    return [
        json.loads(line)
        for line in manifest.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


def test_recording_is_disabled_by_default(tmp_path: Path):
    cfg = load_config(CONFIG_PATH)
    assert cfg.recording.enabled is False
    assert cfg.recording.directory == "recordings"
    assert cfg.recording.store_context is False

    cfg.recording.directory = str(tmp_path / "recordings")
    client = build_client(cfg)
    send_websocket_dictation(client, make_silence(0.2))

    assert not (tmp_path / "recordings").exists()


def test_websocket_dictation_writes_wav_and_manifest(tmp_path: Path):
    cfg = enabled_config(tmp_path)
    client = build_client(cfg)
    pcm = make_silence(0.5)

    result = send_websocket_dictation(client, pcm, profile="code")

    directory = Path(cfg.recording.directory)
    entries = read_manifest(directory)
    assert len(entries) == 1
    entry = entries[0]

    assert entry["id"] == Path(entry["audio_path"]).stem
    assert entry["transport"] == "websocket"
    assert entry["profile"] == "code"
    assert entry["bypass_llm"] is False
    assert entry["audio_bytes"] == len(pcm)
    assert entry["duration_seconds"] == 0.5
    assert entry["sample_rate"] == 16000
    assert entry["channels"] == 1
    assert entry["sample_width"] == 2
    assert entry["raw_text"] == result["raw_text"] == "ehm ciao fast a pi"
    assert entry["final_text"] == result["final_text"]
    assert entry["fast_path"] is False
    assert entry["stt_model"] == cfg.server.stt_model
    assert entry["llm_model"] == cfg.server.llm_model
    assert entry["metrics"]["total_ms"] == result["metrics"]["total_ms"]
    assert entry["reference_raw"] == ""
    assert entry["reference_final"] == ""
    assert entry["technical_terms"] == []
    assert entry["is_silence"] is False
    assert entry["category"] == "it_general"

    audio_path = directory / entry["audio_path"]
    assert audio_path.is_file()
    assert stat.S_IMODE(audio_path.stat().st_mode) == 0o600
    with wave.open(str(audio_path), "rb") as handle:
        assert handle.getnchannels() == 1
        assert handle.getsampwidth() == 2
        assert handle.getframerate() == 16000
        assert handle.readframes(handle.getnframes()) == pcm

    clips = load_manifest(directory / "manifest.jsonl")
    assert [clip["id"] for clip in clips] == [entry["id"]]
    assert clips[0]["audio_path"] == audio_path
    assert clips[0]["reference_raw"] == ""


def test_rest_dictation_writes_wav_and_manifest(tmp_path: Path):
    cfg = enabled_config(tmp_path)
    client = build_client(cfg)
    pcm = make_silence(0.25)
    wav_path = tmp_path / "upload.wav"
    with wave.open(str(wav_path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(16000)
        handle.writeframes(pcm)

    response = client.post(
        "/v1/audio/transcriptions",
        files={"file": ("upload.wav", wav_path.read_bytes(), "audio/wav")},
        data={"profile": "chat"},
    )
    assert response.status_code == 200

    entries = read_manifest(Path(cfg.recording.directory))
    assert len(entries) == 1
    entry = entries[0]
    assert entry["transport"] == "rest"
    assert entry["profile"] == "chat"
    assert entry["bypass_llm"] is False
    assert entry["raw_text"] == response.json()["raw_text"]
    assert entry["final_text"] == response.json()["text"]
    assert (Path(cfg.recording.directory) / entry["audio_path"]).is_file()


def test_recording_omits_context_by_default(tmp_path: Path):
    cfg = enabled_config(tmp_path)
    client = build_client(cfg)
    send_websocket_dictation(
        client,
        make_silence(0.2),
        app_name="code.exe",
        window_title="secret.txt",
        selected_text="private selection",
    )

    manifest_text = (
        Path(cfg.recording.directory) / "manifest.jsonl"
    ).read_text(encoding="utf-8")
    entry = read_manifest(Path(cfg.recording.directory))[0]
    assert "context" not in entry
    assert "secret.txt" not in manifest_text
    assert "private selection" not in manifest_text


def test_recording_can_store_context_without_selected_text(tmp_path: Path):
    cfg = enabled_config(tmp_path, store_context=True)
    client = build_client(cfg)
    send_websocket_dictation(
        client,
        make_silence(0.2),
        app_name="code.exe",
        window_title="secret.txt",
        selected_text="private selection",
    )

    entry = read_manifest(Path(cfg.recording.directory))[0]
    assert entry["context"] == {
        "app_name": "code.exe",
        "window_title": "secret.txt",
    }
    assert "selected_text" not in entry["context"]


def test_recording_failure_does_not_fail_dictation(tmp_path: Path, caplog):
    cfg = enabled_config(tmp_path)
    client = build_client(cfg)
    directory = Path(cfg.recording.directory)
    shutil.rmtree(directory)
    directory.write_text("not a directory", encoding="utf-8")

    result = send_websocket_dictation(client, make_silence(0.2))

    assert result["type"] == "result"
    assert "recording failed" in caplog.text


def test_aborted_dictation_is_not_recorded(tmp_path: Path):
    cfg = enabled_config(tmp_path)
    client = build_client(cfg)
    with client.websocket_connect("/ws/dictate") as ws:
        ws.send_json({"type": "start", "profile": "code"})
        ws.send_bytes(make_silence(0.2))
        ws.send_json({"type": "abort"})

    directory = Path(cfg.recording.directory)
    assert not (directory / "manifest.jsonl").exists()
    assert not list((directory / "audio").glob("*.wav"))
