import collections
import hashlib
import json
import shutil
from pathlib import Path

from dictation_server.model_manifest import load_candidates
from scripts import fetch_mlx_models


def _file(path: str, content: bytes) -> dict:
    return {
        "path": path,
        "sha256": hashlib.sha256(content).hexdigest(),
        "size": len(content),
    }


def _entry(entry_id: str) -> dict:
    return {
        "id": entry_id,
        "repository": f"mlx-community/{entry_id}",
        "revision": "a" * 40,
        "runtime": "mlx-lm",
        "engine": "gemma4",
        "precision": "4bit",
        "files": [_file("model.safetensors", b"weights")],
    }


def _manifest(tmp_path: Path) -> Path:
    data = {
        "schema_version": 1,
        "pair_id": "pair",
        "status": "test",
        "stt": _entry("stt"),
        "cleaner": _entry("cleaner"),
        "prompt": None,
        "cleanup": None,
        "evidence": {},
    }
    path = tmp_path / "selected.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    return path


def _candidates(tmp_path: Path) -> Path:
    data = {
        "schema_version": 1,
        "candidates": {
            "stt": _entry("stt"),
            "cleaner": _entry("cleaner"),
        },
        "pairs": {
            "primary": {"stt": "stt", "cleaner": "cleaner"},
            "control": {"stt": "stt", "cleaner": "cleaner"},
        },
    }
    path = tmp_path / "candidates.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    return path


def test_required_bytes_includes_margin():
    assert fetch_mlx_models.required_bytes_for_sizes([100, 50], factor=1.2) == 180


def test_dry_run_does_not_download(tmp_path: Path, monkeypatch):
    manifest = _manifest(tmp_path)
    root = tmp_path / "models"
    calls = []

    def fail(*args, **kwargs):
        calls.append((args, kwargs))
        raise AssertionError("network must not be used")

    monkeypatch.setattr(fetch_mlx_models, "snapshot_download", fail)
    monkeypatch.setattr(fetch_mlx_models, "hf_hub_download", fail)

    assert fetch_mlx_models.main(
        ["--manifest", str(manifest), "--root", str(root), "--dry-run"]
    ) == 0
    assert calls == []
    assert not root.exists()


def test_verify_only_does_not_touch_network(tmp_path: Path, monkeypatch):
    manifest = _manifest(tmp_path)
    root = tmp_path / "models"
    for entry_id in ("stt", "cleaner"):
        model_dir = root / entry_id
        model_dir.mkdir(parents=True)
        (model_dir / "model.safetensors").write_bytes(b"weights")

    def fail(*args, **kwargs):
        raise AssertionError("network must not be used")

    monkeypatch.setattr(fetch_mlx_models, "snapshot_download", fail)
    monkeypatch.setattr(fetch_mlx_models, "hf_hub_download", fail)

    assert fetch_mlx_models.main(
        ["--manifest", str(manifest), "--root", str(root), "--verify-only"]
    ) == 0


def test_fetch_downloads_and_verifies(tmp_path: Path, monkeypatch):
    manifest = _manifest(tmp_path)
    root = tmp_path / "models"
    calls = []

    def fake_snapshot(repo_id, revision, local_dir, allow_patterns):
        calls.append(("snapshot", repo_id, revision, tuple(allow_patterns)))
        Path(local_dir, "config.json").write_text("{}", encoding="utf-8")

    def fake_download(repo_id, filename, revision, local_dir):
        calls.append(("download", repo_id, filename, revision))
        destination = Path(local_dir, filename)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(b"weights")
        return str(destination)

    monkeypatch.setattr(fetch_mlx_models, "snapshot_download", fake_snapshot)
    monkeypatch.setattr(fetch_mlx_models, "hf_hub_download", fake_download)

    assert fetch_mlx_models.main(
        ["--manifest", str(manifest), "--root", str(root)]
    ) == 0
    assert (root / "stt" / "model.safetensors").read_bytes() == b"weights"
    assert (root / "cleaner" / "model.safetensors").read_bytes() == b"weights"
    assert [call[0] for call in calls] == ["snapshot", "download", "snapshot", "download"]


def test_fetch_insufficient_disk_fails_before_download(tmp_path: Path, monkeypatch):
    manifest = _manifest(tmp_path)
    root = tmp_path / "models"

    usage = collections.namedtuple("usage", "total used free")

    def fake_disk_usage(_path):
        return usage(total=1, used=1, free=0)

    monkeypatch.setattr(fetch_mlx_models.shutil, "disk_usage", fake_disk_usage)
    monkeypatch.setattr(
        fetch_mlx_models,
        "snapshot_download",
        lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError("no network")),
    )
    monkeypatch.setattr(
        fetch_mlx_models,
        "hf_hub_download",
        lambda *args, **kwargs: (_ for _ in ()).throw(AssertionError("no network")),
    )

    assert fetch_mlx_models.main(
        ["--manifest", str(manifest), "--root", str(root)]
    ) == 1


def test_candidates_are_deduplicated(tmp_path: Path):
    candidates = fetch_mlx_models.load_candidates(_candidates(tmp_path))
    entries = fetch_mlx_models.entries_from_pairs(candidates)
    assert {entry.id for entry in entries} == {"stt", "cleaner"}
