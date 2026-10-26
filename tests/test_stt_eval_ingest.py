import json
import sys
import wave
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS))

from stt_eval import load_manifest  # noqa: E402
from stt_eval_ingest_fleurs import (  # noqa: E402
    ingest_fleurs,
    select_fleurs_clips,
    write_fleurs_corpus,
)

EXAMPLE_MANIFEST = (
    ROOT / "tests" / "fixtures" / "stt_eval" / "manifest.example.jsonl"
)


def _fake_rows(count: int = 60) -> list[dict]:
    rows = []
    for index in range(count):
        duration = 2 + (index % 15)
        rows.append(
            {
                "id": index,
                "audio": {
                    "array": [0.25] * (duration * 16000),
                    "sampling_rate": 16000,
                },
                "transcription": f"testo numero {index}",
                "raw_transcription": f"testo numero {index}",
                "gender": "female" if index % 2 == 0 else "male",
            }
        )
    return rows


def test_select_is_deterministic_and_stratified():
    rows = _fake_rows(60)

    first = select_fleurs_clips(rows, count=30, seed=20261026)
    second = select_fleurs_clips(rows, count=30, seed=20261026)

    assert [row["id"] for row in first] == [row["id"] for row in second]
    assert len(first) == 30
    assert sum(row["duration_bucket"] == "short" for row in first) == 15
    assert sum(row["duration_bucket"] == "medium" for row in first) == 15
    assert {row["gender"] for row in first} == {"female", "male"}


def test_writer_emits_valid_wav_manifest_and_provenance(tmp_path):
    output = write_fleurs_corpus(
        _fake_rows(6),
        tmp_path / "fleurs_it",
        count=4,
        seed=1,
        provenance={"dataset": "google/fleurs", "license": "CC-BY-4.0"},
    )

    clips = load_manifest(output, require_audio=True)

    assert len(clips) == 4
    assert all(clip["category"] == "it_general" for clip in clips)
    assert all(clip["is_silence"] is False for clip in clips)
    assert all(clip["gender"] in {"female", "male"} for clip in clips)
    assert {clip["duration_bucket"] for clip in clips} == {"short", "medium"}

    with wave.open(str(clips[0]["audio_path"]), "rb") as handle:
        assert handle.getnchannels() == 1
        assert handle.getsampwidth() == 2
        assert handle.getframerate() == 16000
        assert handle.getnframes() > 0

    provenance = json.loads(
        (output.parent / "provenance.json").read_text(encoding="utf-8")
    )
    assert provenance["dataset"] == "google/fleurs"
    assert provenance["license"] == "CC-BY-4.0"
    assert provenance["seed"] == 1
    assert provenance["count"] == 4
    assert len(provenance["manifest_sha256"]) == 64


def test_ingest_accepts_injected_dataset_loader(tmp_path):
    output = ingest_fleurs(
        tmp_path / "corpus",
        count=4,
        seed=7,
        dataset_loader=lambda: _fake_rows(8),
    )

    assert len(load_manifest(output, require_audio=True)) == 4


def test_odd_count_is_rejected(tmp_path):
    with pytest.raises(ValueError, match="even"):
        write_fleurs_corpus(_fake_rows(6), tmp_path, count=3)


def test_non_16khz_audio_is_rejected(tmp_path):
    rows = _fake_rows(2)
    rows[0]["audio"]["sampling_rate"] = 8000

    with pytest.raises(ValueError, match="16000"):
        write_fleurs_corpus(rows, tmp_path, count=2)


def test_existing_manifest_without_optional_fields_still_loads():
    clips = load_manifest(EXAMPLE_MANIFEST, require_audio=False)

    assert clips
    assert all("gender" in clip for clip in clips)
    assert all(clip["gender"] is None for clip in clips)
