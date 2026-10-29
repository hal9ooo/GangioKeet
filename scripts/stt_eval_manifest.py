"""Manifest JSONL validation shared by the Windows evaluation scripts."""

from __future__ import annotations

import json
import wave
from pathlib import Path

ALLOWED_CATEGORIES = (
    "it_general",
    "it_technical",
    "identifiers",
    "hesitations_self_corrections",
    "noise_weak_voice",
    "silence_noise",
)
REQUIRED_FIELDS = (
    "id",
    "audio_path",
    "reference_raw",
    "reference_final",
    "category",
    "profile",
    "technical_terms",
    "is_silence",
)
OPTIONAL_FIELDS = ("gender", "duration_bucket")
EXPECTED_SAMPLE_RATE = 16_000
EXPECTED_SAMPLE_WIDTH = 2  # PCM16
EXPECTED_CHANNELS = 1


class ManifestError(ValueError):
    """Invalid or missing evaluation manifest."""


def _verify_wav(path: Path) -> None:
    try:
        with wave.open(str(path), "rb") as handle:
            channels = handle.getnchannels()
            sample_width = handle.getsampwidth()
            sample_rate = handle.getframerate()
            frames = handle.getnframes()
            compression = handle.getcomptype()
    except (wave.Error, EOFError, OSError) as exc:
        raise ManifestError(
            f"audio file is not a readable WAV: {path}: {exc}"
        ) from exc

    if compression != "NONE":
        raise ManifestError(
            f"audio file must contain uncompressed PCM, found "
            f"{compression!r}: {path}"
        )
    if sample_width != EXPECTED_SAMPLE_WIDTH:
        raise ManifestError(
            f"audio file must be PCM16 ({EXPECTED_SAMPLE_WIDTH} bytes per "
            f"sample), found {sample_width}: {path}"
        )
    if channels != EXPECTED_CHANNELS:
        raise ManifestError(
            f"audio file must be mono ({EXPECTED_CHANNELS} channel), found "
            f"{channels}: {path}"
        )
    if sample_rate != EXPECTED_SAMPLE_RATE:
        raise ManifestError(
            f"audio file must be 16000 Hz, found {sample_rate}: {path}"
        )
    if frames <= 0:
        raise ManifestError(f"audio file must contain frames: {path}")


def _require_string(entry: dict, field: str, line: int) -> str:
    value = entry[field]
    if not isinstance(value, str) or not value.strip():
        raise ManifestError(
            f"line {line}: field '{field}' must be a non-empty string"
        )
    return value


def _validate_entry(entry, *, line: int, base: Path, require_audio: bool):
    if not isinstance(entry, dict):
        raise ManifestError(f"line {line}: manifest entry must be a JSON object")

    missing = [field for field in REQUIRED_FIELDS if field not in entry]
    if missing:
        raise ManifestError(
            f"line {line}: missing required field(s): {', '.join(missing)}"
        )

    clip_id = _require_string(entry, "id", line)
    audio_value = _require_string(entry, "audio_path", line)

    if not isinstance(entry["reference_raw"], str):
        raise ManifestError(f"line {line}: 'reference_raw' must be a string")
    if not isinstance(entry["reference_final"], str):
        raise ManifestError(f"line {line}: 'reference_final' must be a string")

    category = entry["category"]
    if category not in ALLOWED_CATEGORIES:
        raise ManifestError(
            f"line {line}: unknown category {category!r}; allowed categories: "
            f"{', '.join(ALLOWED_CATEGORIES)}"
        )

    profile = _require_string(entry, "profile", line)

    terms = entry["technical_terms"]
    if not isinstance(terms, list) or not all(
        isinstance(term, str) for term in terms
    ):
        raise ManifestError(
            f"line {line}: 'technical_terms' must be a list of strings"
        )

    is_silence = entry["is_silence"]
    if not isinstance(is_silence, bool):
        raise ManifestError(f"line {line}: 'is_silence' must be a boolean")

    optional = {}
    for field in OPTIONAL_FIELDS:
        value = entry.get(field)
        if value is not None and (
            not isinstance(value, str) or not value.strip()
        ):
            raise ManifestError(
                f"line {line}: '{field}' must be a non-empty string or null"
            )
        optional[field] = value

    status = entry.get("reference_status")
    if status is not None and status not in {"provided", "reviewed", "raw_only", "unreviewed"}:
        raise ManifestError(f"line {line}: invalid reference_status: {status!r}")
    for field in ("reference_status", "reference_revision", "source_path", "source_row_index"):
        if field in entry:
            optional[field] = entry[field]

    if is_silence and category != "silence_noise":
        raise ManifestError(
            f"line {line}: is_silence=true requires category "
            "'silence_noise'"
        )
    if category == "silence_noise" and not is_silence:
        raise ManifestError(
            f"line {line}: category 'silence_noise' requires "
            "is_silence=true"
        )
    if is_silence and (
        entry["reference_raw"].strip() or entry["reference_final"].strip()
    ):
        raise ManifestError(
            f"line {line}: silence clip '{clip_id}' must have empty "
            "reference_raw and reference_final"
        )

    audio_path = Path(audio_value)
    if audio_path.is_absolute():
        raise ManifestError(
            f"line {line}: audio_path must be relative to the manifest, "
            f"found absolute path: {audio_value}"
        )

    base_resolved = base.resolve()
    resolved = (base / audio_path).resolve()
    if not resolved.is_relative_to(base_resolved):
        raise ManifestError(
            f"line {line}: audio_path escapes the manifest directory: "
            f"{audio_value}"
        )

    if require_audio:
        if not resolved.is_file():
            raise ManifestError(
                f"line {line}: audio file not found for clip '{clip_id}': "
                f"{resolved}"
            )
        _verify_wav(resolved)

    return {
        "id": clip_id,
        "audio_path": resolved,
        "reference_raw": entry["reference_raw"],
        "reference_final": entry["reference_final"],
        "category": category,
        "profile": profile,
        "technical_terms": list(terms),
        "is_silence": is_silence,
        **optional,
    }


def load_manifest(
    path: Path,
    *,
    require_audio: bool = True,
) -> list[dict]:
    """Load and validate a JSONL manifest.

    Paths are resolved relative to the manifest directory.  With
    ``require_audio=False`` (used only for the committed schema example),
    existence and WAV format checks are skipped.
    """

    path = Path(path)
    if not path.is_file():
        raise ManifestError(f"manifest not found: {path}")

    clips: list[dict] = []
    seen_ids: dict[str, int] = {}
    base = path.parent

    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise ManifestError(f"cannot read manifest {path}: {exc}") from exc

    for line_number, line in enumerate(lines, start=1):
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError as exc:
            raise ManifestError(
                f"{path}:{line_number}: malformed JSON: {exc.msg}"
            ) from exc

        clip = _validate_entry(
            entry,
            line=line_number,
            base=base,
            require_audio=require_audio,
        )
        if clip["id"] in seen_ids:
            raise ManifestError(
                f"line {line_number}: duplicate clip id '{clip['id']}' "
                f"(first seen on line {seen_ids[clip['id']]})"
            )
        seen_ids[clip["id"]] = line_number
        clips.append(clip)

    if not clips:
        raise ManifestError(f"manifest contains no clips: {path}")
    return clips
