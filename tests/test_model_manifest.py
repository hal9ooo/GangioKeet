import hashlib
import json
from pathlib import Path

import pytest

from dictation_server.model_manifest import (
    ModelIntegrityError,
    load_candidates,
    load_manifest,
    resolve_model_path,
    verify_manifest,
)


def _file(path: str, content: bytes, size: int | None = None) -> dict:
    return {
        "path": path,
        "sha256": hashlib.sha256(content).hexdigest(),
        "size": len(content) if size is None else size,
    }


def _entry(entry_id: str, runtime: str = "mlx-lm", file: dict | None = None) -> dict:
    return {
        "id": entry_id,
        "repository": f"mlx-community/{entry_id}",
        "revision": "a" * 40,
        "runtime": runtime,
        "engine": "gemma4" if runtime == "mlx-lm" else "qwen3_asr",
        "precision": "4bit",
        "files": [file or _file("model.safetensors", b"weights")],
    }


def _manifest(stt: dict | None = None, cleaner: dict | None = None) -> dict:
    return {
        "schema_version": 1,
        "pair_id": "pair",
        "status": "test",
        "stt": stt or _entry("stt", runtime="mlx-audio"),
        "cleaner": cleaner or _entry("cleaner"),
        "prompt": None,
        "cleanup": None,
        "evidence": {},
    }


def _write(tmp_path: Path, name: str, data: dict) -> Path:
    path = tmp_path / name
    path.write_text(json.dumps(data), encoding="utf-8")
    return path


def test_load_manifest_and_candidates(tmp_path: Path):
    manifest_path = _write(tmp_path, "selected.json", _manifest())
    manifest = load_manifest(manifest_path)
    assert manifest.pair_id == "pair"
    assert manifest.stt.runtime == "mlx-audio"
    assert manifest.cleaner.runtime == "mlx-lm"
    assert manifest.prompt is None

    candidates = {
        "schema_version": 1,
        "candidates": {
            "stt": _entry("stt", runtime="mlx-audio"),
            "cleaner": _entry("cleaner"),
        },
        "pairs": {
            "primary": {"stt": "stt", "cleaner": "cleaner"},
        },
    }
    candidates_path = _write(tmp_path, "candidates.json", candidates)
    pairs = load_candidates(candidates_path)
    assert set(pairs) == {"primary"}
    assert pairs["primary"].stt.id == "stt"
    assert pairs["primary"].cleaner.id == "cleaner"


def test_verify_manifest_checks_size_and_sha(tmp_path: Path):
    content = b"weights"
    entry = _entry("stt", runtime="mlx-audio", file=_file("model.safetensors", content))
    manifest = load_manifest(_write(tmp_path, "selected.json", _manifest(stt=entry)))
    model_dir = tmp_path / "models" / "stt"
    model_dir.mkdir(parents=True)
    (model_dir / "model.safetensors").write_bytes(content)
    cleaner_dir = tmp_path / "models" / "cleaner"
    cleaner_dir.mkdir(parents=True)
    (cleaner_dir / "model.safetensors").write_bytes(b"weights")

    verify_manifest(manifest, tmp_path / "models")
    assert resolve_model_path(manifest, manifest.stt, tmp_path / "models") == model_dir


def test_verify_manifest_rejects_missing_file(tmp_path: Path):
    manifest = load_manifest(_write(tmp_path, "selected.json", _manifest()))
    with pytest.raises(ModelIntegrityError, match="missing"):
        verify_manifest(manifest, tmp_path / "models")


def test_verify_manifest_rejects_hash_mismatch(tmp_path: Path):
    manifest = load_manifest(_write(tmp_path, "selected.json", _manifest()))
    model_dir = tmp_path / "models" / "stt"
    model_dir.mkdir(parents=True)
    (model_dir / "model.safetensors").write_bytes(b"weightz")

    with pytest.raises(ModelIntegrityError, match="sha256"):
        verify_manifest(manifest, tmp_path / "models")


def test_verify_manifest_rejects_size_mismatch(tmp_path: Path):
    entry = _entry(
        "stt",
        runtime="mlx-audio",
        file=_file("model.safetensors", b"weights", size=999),
    )
    manifest = load_manifest(_write(tmp_path, "selected.json", _manifest(stt=entry)))
    model_dir = tmp_path / "models" / "stt"
    model_dir.mkdir(parents=True)
    (model_dir / "model.safetensors").write_bytes(b"weights")

    with pytest.raises(ModelIntegrityError, match="size"):
        verify_manifest(manifest, tmp_path / "models")


@pytest.mark.parametrize(
    "mutate",
    [
        lambda data: data["stt"].update(runtime="torch"),
        lambda data: data["stt"].update(revision=""),
        lambda data: data["stt"]["files"][0].update(sha256="not-a-hash"),
    ],
)
def test_invalid_manifest_fields_rejected(tmp_path: Path, mutate):
    data = _manifest()
    mutate(data)
    with pytest.raises(ValueError):
        load_manifest(_write(tmp_path, "bad.json", data))


def test_unknown_pair_reference_rejected(tmp_path: Path):
    data = {
        "schema_version": 1,
        "candidates": {
            "stt": _entry("stt", runtime="mlx-audio"),
            "cleaner": _entry("cleaner"),
        },
        "pairs": {"primary": {"stt": "stt", "cleaner": "missing"}},
    }
    with pytest.raises(ValueError, match="missing"):
        load_candidates(_write(tmp_path, "bad.json", data))


def test_duplicate_candidate_ids_rejected(tmp_path: Path):
    data = {
        "schema_version": 1,
        "candidates": {
            "one": _entry("same", runtime="mlx-audio"),
            "two": _entry("same"),
        },
        "pairs": {"primary": {"stt": "one", "cleaner": "two"}},
    }
    with pytest.raises(ValueError, match="duplicate"):
        load_candidates(_write(tmp_path, "bad.json", data))
