"""Ingest a deterministic FLEURS Italian sample into the STT eval format.

The audio itself is local-only.  The committed manifest contains references
and provenance, never audio.  ``datasets`` is imported only when a real loader
is used; tests pass ``dataset_loader`` and never touch the network.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import re
import sys
import wave
from pathlib import Path
from typing import Callable, Iterable, Sequence

import numpy as np

DEFAULT_COUNT = 30
DEFAULT_SEED = 20261026
SAMPLE_RATE = 16000
SAMPLE_WIDTH = 2
GENDER_ORDER = ("female", "male", "other", "unknown")


def _normalize_gender(value) -> str:
    if not isinstance(value, str):
        return "unknown"
    value = value.strip().lower()
    return value if value in GENDER_ORDER else "unknown"


def _duration(row: dict) -> float:
    audio = row.get("audio") or {}
    array = audio.get("array")
    rate = audio.get("sampling_rate", SAMPLE_RATE)
    if array is None or not rate:
        raise ValueError(f"row {row.get('id')!r} has no audio array/rate")
    return len(array) / float(rate)


def _select_bucket(bucket: list[dict], per_bucket: int) -> list[dict]:
    groups: dict[str, list[dict]] = {gender: [] for gender in GENDER_ORDER}
    for row in bucket:
        groups[_normalize_gender(row.get("gender"))].append(row)
    for gender in GENDER_ORDER:
        groups[gender].sort(key=lambda row: str(row["id"]))

    selected: list[dict] = []
    while len(selected) < per_bucket:
        progressed = False
        for gender in GENDER_ORDER:
            if groups[gender] and len(selected) < per_bucket:
                selected.append(groups[gender].pop(0))
                progressed = True
        if not progressed:
            break
    if len(selected) < per_bucket:
        raise ValueError(
            f"not enough rows for a stratified bucket: wanted {per_bucket}, "
            f"got {len(selected)}"
        )
    return selected


def select_fleurs_clips(
    rows: list[dict],
    count: int = DEFAULT_COUNT,
    seed: int = DEFAULT_SEED,
) -> list[dict]:
    """Select short/medium clips deterministically, balancing gender."""
    if count < 2 or count % 2:
        raise ValueError("count must be an even number >= 2")
    if len(rows) < count:
        raise ValueError(
            f"need at least {count} rows, received {len(rows)}"
        )

    prepared = []
    for row in rows:
        prepared.append({**row, "_duration": _duration(row)})

    # The seed only affects ties: duration ordering remains the stratification.
    shuffled = list(prepared)
    random.Random(seed).shuffle(shuffled)
    shuffled.sort(key=lambda row: (row["_duration"], str(row["id"])))

    midpoint = len(shuffled) // 2
    short = shuffled[:midpoint]
    medium = shuffled[midpoint:]
    per_bucket = count // 2

    selected = _select_bucket(short, per_bucket) + _select_bucket(
        medium, per_bucket
    )
    selected.sort(key=lambda row: str(row["id"]))

    result = []
    for row in selected:
        result.append(
            {
                key: value
                for key, value in row.items()
                if key != "_duration"
            }
            | {
                "duration_seconds": round(row["_duration"], 6),
                "duration_bucket": (
                    "short" if row in short else "medium"
                ),
            }
        )
    return result


def _pcm16_bytes(array: Sequence[float]) -> bytes:
    values = np.asarray(array)
    if np.issubdtype(values.dtype, np.integer):
        return values.astype("<i2", copy=False).tobytes()
    values = np.clip(values.astype(np.float32), -1.0, 1.0)
    return np.round(values * 32767.0).astype("<i2").tobytes()


def _write_wav(path: Path, array: Sequence[float], sample_rate: int) -> None:
    if sample_rate != SAMPLE_RATE:
        raise ValueError(
            f"audio must be {SAMPLE_RATE} Hz, found {sample_rate}"
        )
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(SAMPLE_WIDTH)
        handle.setframerate(SAMPLE_RATE)
        handle.writeframes(_pcm16_bytes(array))


def _clip_id(source_id) -> str:
    safe = re.sub(r"[^A-Za-z0-9_.-]+", "_", str(source_id)).strip("_")
    return f"fleurs_it_{safe}"


def write_fleurs_corpus(
    rows: list[dict],
    output_dir: Path,
    *,
    count: int = DEFAULT_COUNT,
    seed: int = DEFAULT_SEED,
    provenance: dict | None = None,
) -> Path:
    output_dir = Path(output_dir)
    audio_dir = output_dir / "audio"
    audio_dir.mkdir(parents=True, exist_ok=True)

    selected = select_fleurs_clips(rows, count=count, seed=seed)
    entries = []
    for row in selected:
        clip_id = _clip_id(row["id"])
        audio = row["audio"]
        sample_rate = int(audio.get("sampling_rate", SAMPLE_RATE))
        audio_path = audio_dir / f"{clip_id}.wav"
        _write_wav(audio_path, audio["array"], sample_rate)

        entries.append(
            {
                "id": clip_id,
                "audio_path": f"audio/{clip_id}.wav",
                "reference_raw": row["transcription"],
                "reference_final": row["transcription"],
                "category": "it_general",
                "profile": "default",
                "technical_terms": [],
                "is_silence": False,
                "gender": row.get("gender"),
                "duration_bucket": row["duration_bucket"],
            }
        )

    manifest_path = output_dir / "manifest.jsonl"
    manifest_text = "".join(
        json.dumps(entry, ensure_ascii=False) + "\n" for entry in entries
    )
    manifest_path.write_text(manifest_text, encoding="utf-8")

    metadata = {
        "dataset": "google/fleurs",
        "config": "it_it",
        "split": "test",
        "revision": None,
        "license": "CC-BY-4.0",
        "seed": seed,
        "count": count,
        "selection": "15 short + 15 medium, gender-balanced where available",
        **(provenance or {}),
        "manifest_sha256": hashlib.sha256(
            manifest_text.encode("utf-8")
        ).hexdigest(),
    }
    (output_dir / "provenance.json").write_text(
        json.dumps(metadata, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    return manifest_path


def ingest_fleurs(
    output_dir: Path,
    *,
    count: int = DEFAULT_COUNT,
    seed: int = DEFAULT_SEED,
    split: str = "test",
    revision: str | None = None,
    dataset_loader: Callable[[], Iterable[dict]] | None = None,
) -> Path:
    if dataset_loader is None:
        try:
            from datasets import load_dataset
        except ImportError as exc:
            raise RuntimeError(
                "datasets is required to download FLEURS; install it only in "
                ".venv-stt-eval"
            ) from exc
        dataset = load_dataset(
            "google/fleurs",
            "it_it",
            split=split,
            revision=revision,
        )
        rows = list(dataset)
    else:
        rows = list(dataset_loader())

    return write_fleurs_corpus(
        rows,
        output_dir,
        count=count,
        seed=seed,
        provenance={"split": split, "revision": revision},
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Download/select FLEURS Italian for the STT benchmark."
    )
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--count", type=int, default=DEFAULT_COUNT)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--split", default="test")
    parser.add_argument("--revision", default=None)
    args = parser.parse_args(argv)

    try:
        manifest = ingest_fleurs(
            args.output_dir,
            count=args.count,
            seed=args.seed,
            split=args.split,
            revision=args.revision,
        )
    except (OSError, RuntimeError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    print(f"manifest: {manifest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
