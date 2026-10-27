"""Human review of local recordings; never derives references from model output."""
from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def _read_lines(path: Path) -> list[dict]:
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            rows.append(json.loads(line))
    return rows


def _write_atomic(path: Path, rows: list[dict]) -> None:
    temporary = path.with_name(f".{path.name}.tmp")
    text = "".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows)
    temporary.write_text(text, encoding="utf-8", newline="\n")
    temporary.replace(path)


def apply_reference(
    manifest: Path,
    clip_id: str,
    reference_raw: str,
    reference_final: str,
    expected_audio_sha256: str,
) -> None:
    """Update one human-reviewed capture after verifying its PCM hash."""
    manifest = Path(manifest)
    rows = _read_lines(manifest)
    matches = [row for row in rows if row.get("id") == clip_id]
    if len(matches) != 1:
        raise KeyError(f"clip id must be unique: {clip_id}")
    row = matches[0]
    actual = str(row.get("audio_sha256", "")).lower()
    expected = expected_audio_sha256.lower()
    if len(expected) != 64 or any(c not in "0123456789abcdef" for c in expected):
        raise ValueError("expected_audio_sha256 must be 64 hexadecimal characters")
    if actual != expected:
        raise ValueError(f"audio hash mismatch for {clip_id}")

    row["reference_raw"] = reference_raw
    row["reference_final"] = reference_final
    row["reference_status"] = "provided" if reference_raw or reference_final else "unreviewed"
    row["reference_revision"] = "human:" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    _write_atomic(manifest, rows)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--clip-id", required=True)
    parser.add_argument("--reference-raw", required=True)
    parser.add_argument("--reference-final", required=True)
    parser.add_argument("--expected-audio-sha256", required=True)
    args = parser.parse_args()
    try:
        apply_reference(args.manifest, args.clip_id, args.reference_raw,
                        args.reference_final, args.expected_audio_sha256)
    except (KeyError, ValueError, OSError) as error:
        print(f"Review failed: {error}")
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
