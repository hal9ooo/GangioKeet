import json
import hashlib
import io
from types import SimpleNamespace
import sys
import wave
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "scripts"
sys.path.insert(0, str(SCRIPTS))

from stt_eval_manifest import load_manifest  # noqa: E402
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


def test_integer_gender_metadata_is_normalized(tmp_path):
    rows = _fake_rows(2)
    rows[0]["gender"] = 0
    rows[1]["gender"] = 1

    output = write_fleurs_corpus(rows, tmp_path, count=2, seed=1)
    genders = {clip["gender"] for clip in load_manifest(output)}

    assert genders == {"male", "female"}


def test_duplicate_source_ids_get_unique_clip_ids(tmp_path):
    rows = _fake_rows(2)
    rows[1]["id"] = rows[0]["id"]

    output = write_fleurs_corpus(rows, tmp_path, count=2, seed=1)
    clips = load_manifest(output, require_audio=True)

    assert len({clip["id"] for clip in clips}) == 2


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


def test_preserves_punctuated_reference(tmp_path):
    rows = _fake_rows(2)
    rows[0].update(transcription="come funziona", raw_transcription="Come funziona?")
    output = write_fleurs_corpus(rows, tmp_path, count=2)
    clips = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
    clip = next(row for row in clips if row["reference_raw"] == "come funziona")
    assert clip["reference_final"] == "Come funziona?"
    assert clip["reference_status"] == "provided"
    assert clip["reference_revision"] == "fleurs-source-v2"


def test_duplicate_sentence_ids_keep_distinct_audio(tmp_path):
    rows = _fake_rows(2)
    rows[1]["id"] = rows[0]["id"]
    rows[0]["path"] = "first.wav"
    rows[1]["path"] = "second.wav"
    rows[1]["audio"]["array"] = [-0.25] * 48000
    output = write_fleurs_corpus(rows, tmp_path, count=2)
    clips = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
    assert {clip["source_path"] for clip in clips} == {"first.wav", "second.wav"}
    assert {clip["source_row_index"] for clip in clips} == {0, 1}
    assert len({clip["audio_sha256"] for clip in clips}) == 2
    for clip in clips:
        with wave.open(str(tmp_path / clip["audio_path"]), "rb") as handle:
            assert hashlib.sha256(handle.readframes(handle.getnframes())).hexdigest() == clip["audio_sha256"]


def test_missing_raw_transcription_is_not_invented(tmp_path):
    rows = _fake_rows(2)
    for row in rows:
        row.pop("raw_transcription")
    output = write_fleurs_corpus(rows, tmp_path, count=2)
    clips = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
    assert all(clip["reference_final"] == "" for clip in clips)
    assert all(clip["reference_status"] == "raw_only" for clip in clips)


def test_manifest_hash_is_stable_with_crlf(tmp_path):
    output = write_fleurs_corpus(_fake_rows(2), tmp_path, count=2)
    metadata = json.loads((tmp_path / "provenance.json").read_text(encoding="utf-8"))
    blob = output.read_bytes()
    assert b"\r\n" not in blob
    assert metadata["manifest_hash_encoding"] == "utf-8-lf"
    crlf = blob.replace(b"\n", b"\r\n")
    assert hashlib.sha256(crlf.replace(b"\r\n", b"\n")).hexdigest() == metadata["manifest_sha256"]


def test_source_id_cannot_escape_audio_root(tmp_path):
    rows = _fake_rows(2)
    rows[0]["id"] = "../../outside"
    output = write_fleurs_corpus(rows, tmp_path, count=2)
    for line in output.read_text(encoding="utf-8").splitlines():
        entry = json.loads(line)
        audio = (tmp_path / entry["audio_path"]).resolve()
        assert audio.is_relative_to((tmp_path / "audio").resolve())
        assert audio.is_file()


def test_default_loader_reads_pinned_parquet_without_remote_scripts(tmp_path, monkeypatch):
    import numpy as np

    revision = "a" * 40
    parquet = "parquet-data/it_it/validation-00000-of-00001.parquet"
    encoded = io.BytesIO()
    with wave.open(encoded, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(16000)
        handle.writeframes(b"\x00\x20" * 32)

    class Dataset:
        def cast_column(self, name, feature):
            return self

        def __iter__(self):
            for index in range(2):
                yield {"id": index, "transcription": "come funziona",
                       "raw_transcription": "Come funziona?", "gender": index,
                       "audio": {"bytes": encoded.getvalue(), "path": f"{index}.wav"}}

    def load(name, config=None, **kwargs):
        if name != "parquet" or kwargs.get("data_files") != [
            f"https://huggingface.co/datasets/google/fleurs/resolve/{revision}/{parquet}"
        ]:
            raise AssertionError("must read pinned parquet, never execute dataset scripts")
        return Dataset()

    api = SimpleNamespace(dataset_info=lambda *a, **k: SimpleNamespace(
        sha=revision, siblings=[SimpleNamespace(rfilename=parquet)]))
    monkeypatch.setitem(sys.modules, "datasets", SimpleNamespace(
        load_dataset=load, Audio=lambda **kwargs: kwargs))
    monkeypatch.setitem(sys.modules, "huggingface_hub", SimpleNamespace(HfApi=lambda: api))
    monkeypatch.setitem(sys.modules, "soundfile", SimpleNamespace(
        read=lambda *a, **k: (np.full(32, 0.25, dtype=np.float32), 16000)))
    output = ingest_fleurs(tmp_path, count=2, split="validation", revision=revision)
    metadata = json.loads((tmp_path / "provenance.json").read_text(encoding="utf-8"))
    assert metadata["revision"] == revision
    assert metadata["purpose"] == "development"
    assert all(json.loads(line)["reference_final"] == "Come funziona?"
               for line in output.read_text(encoding="utf-8").splitlines())
