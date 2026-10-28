#!/usr/bin/env python3
"""Download and verify the pinned MLX model artifacts.

The CLI is intentionally separate from the server: provisioning is an
explicit step, and the server only loads local directories after the
manifest has been verified.
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from huggingface_hub import hf_hub_download, snapshot_download  # noqa: E402

from dictation_server.model_manifest import (  # noqa: E402
    ModelEntry,
    ModelIntegrityError,
    PairSpec,
    load_candidates,
    load_manifest,
    sha256_file,
    verify_entry,
)

# Only metadata needed to load the model.  Weights are fetched explicitly
# from the manifest, so multimodal vision files are never downloaded.
METADATA_PATTERNS = ("*.json", "*.jinja", "*.txt", "*.model")
DEFAULT_DISK_FACTOR = 1.2


def required_bytes_for_sizes(sizes: list[int], factor: float = DEFAULT_DISK_FACTOR) -> int:
    if factor < 1:
        raise ValueError("disk factor must be >= 1")
    return int(sum(sizes) * factor)


def entries_from_pairs(pairs: dict[str, PairSpec]) -> list[ModelEntry]:
    seen: set[tuple[str, str, str]] = set()
    entries: list[ModelEntry] = []
    for pair in pairs.values():
        for entry in (pair.stt, pair.cleaner):
            key = (entry.id, entry.repository, entry.revision)
            if key in seen:
                continue
            seen.add(key)
            entries.append(entry)
    return entries


def verify_entries(entries: list[ModelEntry], root: str | Path) -> None:
    for entry in entries:
        verify_entry(entry, root)


def _ensure_free_space(root: Path, entries: list[ModelEntry], factor: float) -> None:
    probe = root
    while not probe.exists() and probe != probe.parent:
        probe = probe.parent
    free = shutil.disk_usage(probe).free
    required = required_bytes_for_sizes(
        [item.size for entry in entries for item in entry.files], factor
    )
    if free < required:
        raise OSError(
            f"insufficient disk space in {probe}: need {required} bytes, "
            f"have {free}"
        )


def fetch_entries(entries: list[ModelEntry], root: str | Path) -> None:
    base = Path(root)
    for entry in entries:
        destination = base / entry.id
        destination.mkdir(parents=True, exist_ok=True)
        snapshot_download(
            repo_id=entry.repository,
            revision=entry.revision,
            local_dir=str(destination),
            allow_patterns=list(METADATA_PATTERNS),
        )
        for item in entry.files:
            downloaded = Path(
                hf_hub_download(
                    repo_id=entry.repository,
                    filename=item.path,
                    revision=entry.revision,
                    local_dir=str(destination),
                )
            )
            actual_size = downloaded.stat().st_size
            if actual_size != item.size:
                raise ModelIntegrityError(
                    f"size mismatch for {downloaded}: expected {item.size}, "
                    f"got {actual_size}"
                )
            actual_sha = sha256_file(downloaded)
            if actual_sha != item.sha256.lower():
                raise ModelIntegrityError(
                    f"sha256 mismatch for {downloaded}: expected "
                    f"{item.sha256}, got {actual_sha}"
                )


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Download and verify pinned MLX model artifacts"
    )
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--manifest", help="selected-pair manifest")
    source.add_argument("--candidates", help="candidate/pair manifest")
    parser.add_argument("--root", default="models", help="model root directory")
    parser.add_argument(
        "--verify-only",
        action="store_true",
        help="verify local files and exit without touching the network",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print the files and required space without writing",
    )
    parser.add_argument(
        "--disk-factor",
        type=float,
        default=DEFAULT_DISK_FACTOR,
        help="free-space margin multiplier (default 1.2)",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        if args.manifest:
            manifest = load_manifest(args.manifest)
            entries = [manifest.stt, manifest.cleaner]
        else:
            entries = entries_from_pairs(load_candidates(args.candidates))
        root = Path(args.root)

        required = required_bytes_for_sizes(
            [item.size for entry in entries for item in entry.files],
            args.disk_factor,
        )
        if args.verify_only:
            verify_entries(entries, root)
            print(f"verified {len(entries)} model(s) under {root}")
            return 0
        if args.dry_run:
            for entry in entries:
                for item in entry.files:
                    print(f"{entry.id}/{item.path} ({item.size} bytes)")
            print(f"required space: {required} bytes under {root}")
            return 0

        _ensure_free_space(root, entries, args.disk_factor)
        fetch_entries(entries, root)
        verify_entries(entries, root)
        print(f"fetched and verified {len(entries)} model(s) under {root}")
        return 0
    except (ModelIntegrityError, OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except Exception as exc:  # network/API errors from huggingface_hub
        print(f"error: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
