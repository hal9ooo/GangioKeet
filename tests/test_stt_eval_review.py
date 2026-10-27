import json
from pathlib import Path

import pytest

from scripts.stt_eval_review import apply_reference


def _manifest(tmp_path: Path, *, hash_value: str = "a" * 64) -> Path:
    path = tmp_path / "manifest.jsonl"
    rows = [
        {"id": "a", "audio_sha256": hash_value, "audio_path": "audio/a.wav",
         "reference_raw": "", "reference_final": "", "reference_status": "unreviewed"},
        {"id": "b", "audio_sha256": "b" * 64, "audio_path": "audio/b.wav",
         "reference_raw": "vecchia", "reference_final": "Vecchia.", "reference_status": "provided"},
    ]
    path.write_text("".join(json.dumps(x) + "\n" for x in rows), encoding="utf-8")
    return path


def test_apply_reference_updates_only_the_selected_clip(tmp_path: Path) -> None:
    path = _manifest(tmp_path)

    apply_reference(path, "a", "come funziona", "Come funziona?", "a" * 64)

    text = path.read_text(encoding="utf-8")
    assert "\r" not in text
    rows = [json.loads(line) for line in text.splitlines()]
    assert rows[0]["reference_raw"] == "come funziona"
    assert rows[0]["reference_final"] == "Come funziona?"
    assert rows[0]["reference_status"] == "provided"
    assert rows[0]["reference_revision"].startswith("human:")
    assert rows[1] == {"id": "b", "audio_sha256": "b" * 64, "audio_path": "audio/b.wav",
                       "reference_raw": "vecchia", "reference_final": "Vecchia.", "reference_status": "provided"}


def test_wrong_audio_hash_leaves_the_file_unchanged(tmp_path: Path) -> None:
    path = _manifest(tmp_path)
    before = path.read_bytes()

    with pytest.raises(ValueError):
        apply_reference(path, "a", "x", "X.", "c" * 64)

    assert path.read_bytes() == before


def test_empty_references_clear_to_unreviewed(tmp_path: Path) -> None:
    path = _manifest(tmp_path)

    apply_reference(path, "a", "", "", "a" * 64)

    row = json.loads(path.read_text(encoding="utf-8").splitlines()[0])
    assert row["reference_status"] == "unreviewed"
    assert row["reference_raw"] == ""
    assert row["reference_final"] == ""


def test_missing_clip_is_an_error(tmp_path: Path) -> None:
    path = _manifest(tmp_path)

    with pytest.raises(KeyError):
        apply_reference(path, "missing", "x", "X.", "a" * 64)
