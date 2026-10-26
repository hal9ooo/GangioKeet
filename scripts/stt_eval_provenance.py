"""Pinned model revisions and cached-snapshot lookup for the eval harness."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_REVISIONS = (
    ROOT
    / "tests"
    / "fixtures"
    / "stt_eval"
    / "public"
    / "fleurs_it"
    / "model_revisions.json"
)


class RevisionError(RuntimeError):
    """The requested pinned revision is not available in the local cache."""


def load_revisions(path: Path | None = None) -> dict:
    path = Path(path or DEFAULT_REVISIONS)
    return json.loads(path.read_text(encoding="utf-8"))


def _entry(kind: str, key: str, path: Path | None = None) -> dict:
    data = load_revisions(path)
    try:
        return data[kind][key]
    except KeyError as exc:
        raise KeyError(f"no revision metadata for {kind}/{key}") from exc


def revision_for(kind: str, key: str, path: Path | None = None) -> str | None:
    return _entry(kind, key, path).get("revision")


def status_for(kind: str, key: str, path: Path | None = None) -> str:
    return _entry(kind, key, path).get("status", "unknown")


def reason_for(kind: str, key: str, path: Path | None = None) -> str:
    return _entry(kind, key, path).get("reason", "")


def cached_snapshot(model_id: str, revision: str) -> Path:
    """Return the cached snapshot directory for an exact revision.

    The loader receives the snapshot directory, not the mutable ``main`` ref,
    so a fresh run cannot silently use different weights.  A missing cached
    revision fails with an explicit message instead of downloading silently.
    """
    if not revision:
        raise RevisionError(
            f"model {model_id!r} has no pinned revision; add it to "
            f"{DEFAULT_REVISIONS}"
        )
    try:
        from huggingface_hub import try_to_load_from_cache
    except ImportError as exc:
        raise RevisionError(
            "huggingface_hub is required to verify pinned model revisions"
        ) from exc

    cached = try_to_load_from_cache(
        model_id, "config.json", revision=revision
    )
    if not isinstance(cached, str):
        raise RevisionError(
            f"pinned revision {revision!r} for {model_id!r} is not in the "
            "local HF cache; run the model download first"
        )
    snapshot = Path(cached).parent
    if snapshot.name != revision:
        raise RevisionError(
            f"cache returned {snapshot} but expected revision {revision}"
        )
    return snapshot
