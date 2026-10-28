"""Manifest for pinned MLX model artifacts.

The manifest is deliberately data-only: no MLX import, no network.  It is
shared by the fetch CLI, the benchmark and the production bootstrap so the
same revision/hash is checked everywhere.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

_RUNTIMES = frozenset({"mlx-audio", "mlx-lm"})
_HEX = frozenset("0123456789abcdef")


class ModelIntegrityError(RuntimeError):
    """A required model file is missing, truncated or has a wrong hash."""


@dataclass(frozen=True)
class ModelFile:
    path: str
    sha256: str
    size: int

    def __post_init__(self) -> None:
        if not self.path or Path(self.path).is_absolute() or ".." in Path(self.path).parts:
            raise ValueError("model file path must be a non-empty relative path")
        if len(self.sha256) != 64 or any(c not in _HEX for c in self.sha256.lower()):
            raise ValueError(f"invalid sha256 for {self.path!r}")
        if self.size <= 0:
            raise ValueError(f"size for {self.path!r} must be > 0")


@dataclass(frozen=True)
class ModelEntry:
    id: str
    repository: str
    revision: str
    runtime: str
    engine: str
    precision: str
    files: tuple[ModelFile, ...]

    def __post_init__(self) -> None:
        for name in ("id", "repository", "revision", "engine", "precision"):
            if not getattr(self, name).strip():
                raise ValueError(f"model entry {name} must not be empty")
        if self.runtime not in _RUNTIMES:
            raise ValueError(
                f"unsupported runtime {self.runtime!r}; expected one of "
                f"{sorted(_RUNTIMES)}"
            )
        if not self.files:
            raise ValueError(f"model entry {self.id!r} has no files")
        paths = [item.path for item in self.files]
        if len(paths) != len(set(paths)):
            raise ValueError(f"model entry {self.id!r} has duplicate file paths")


@dataclass(frozen=True)
class ModelManifest:
    pair_id: str
    status: str
    stt: ModelEntry
    cleaner: ModelEntry
    prompt: dict[str, Any] | None = None
    cleanup: dict[str, Any] | None = None
    evidence: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not self.pair_id.strip():
            raise ValueError("pair_id must not be empty")
        if not self.status.strip():
            raise ValueError("status must not be empty")


@dataclass(frozen=True)
class PairSpec:
    id: str
    stt: ModelEntry
    cleaner: ModelEntry


def _require_mapping(value: Any, name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"{name} must be an object")
    return value


def _model_file(data: Any) -> ModelFile:
    item = _require_mapping(data, "file")
    try:
        return ModelFile(
            path=item["path"],
            sha256=item["sha256"],
            size=int(item["size"]),
        )
    except KeyError as exc:
        raise ValueError(f"model file missing field {exc.args[0]!r}") from exc
    except (TypeError, ValueError) as exc:
        raise ValueError(f"invalid model file: {exc}") from exc


def _model_entry(data: Any) -> ModelEntry:
    item = _require_mapping(data, "model entry")
    try:
        files = tuple(_model_file(file) for file in item["files"])
        return ModelEntry(
            id=item["id"],
            repository=item["repository"],
            revision=item["revision"],
            runtime=item["runtime"],
            engine=item["engine"],
            precision=item["precision"],
            files=files,
        )
    except KeyError as exc:
        raise ValueError(f"model entry missing field {exc.args[0]!r}") from exc
    except (TypeError, ValueError) as exc:
        raise ValueError(f"invalid model entry: {exc}") from exc


def _load_json(path: str | Path) -> dict[str, Any]:
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    return _require_mapping(data, "manifest")


def load_manifest(path: str | Path) -> ModelManifest:
    data = _load_json(path)
    version = data.get("schema_version", 1)
    if version != 1:
        raise ValueError(f"unsupported manifest schema_version {version!r}")
    prompt = data.get("prompt")
    cleanup = data.get("cleanup")
    evidence = data.get("evidence", {})
    if prompt is not None and not isinstance(prompt, dict):
        raise ValueError("prompt must be an object or null")
    if cleanup is not None and not isinstance(cleanup, dict):
        raise ValueError("cleanup must be an object or null")
    if not isinstance(evidence, dict):
        raise ValueError("evidence must be an object")
    return ModelManifest(
        pair_id=data["pair_id"],
        status=data["status"],
        stt=_model_entry(data["stt"]),
        cleaner=_model_entry(data["cleaner"]),
        prompt=prompt,
        cleanup=cleanup,
        evidence=evidence,
    )


def load_candidates(path: str | Path) -> dict[str, PairSpec]:
    data = _load_json(path)
    version = data.get("schema_version", 1)
    if version != 1:
        raise ValueError(f"unsupported candidates schema_version {version!r}")
    raw_candidates = _require_mapping(data.get("candidates"), "candidates")
    entries: dict[str, ModelEntry] = {}
    ids: set[str] = set()
    for key, value in raw_candidates.items():
        entry = _model_entry(value)
        if entry.id in ids:
            raise ValueError(f"duplicate candidate id {entry.id!r}")
        ids.add(entry.id)
        entries[key] = entry
    raw_pairs = _require_mapping(data.get("pairs"), "pairs")
    pairs: dict[str, PairSpec] = {}
    for key, value in raw_pairs.items():
        pair = _require_mapping(value, f"pair {key!r}")
        for role in ("stt", "cleaner"):
            ref = pair.get(role)
            if not isinstance(ref, str) or ref not in entries:
                raise ValueError(
                    f"pair {key!r} references unknown {role} candidate {ref!r}"
                )
        pairs[key] = PairSpec(
            id=key,
            stt=entries[pair["stt"]],
            cleaner=entries[pair["cleaner"]],
        )
    return pairs


def sha256_file(path: str | Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _verify_entry(entry: ModelEntry, root: Path) -> None:
    model_dir = root / entry.id
    for item in entry.files:
        path = model_dir / item.path
        if not path.is_file():
            raise ModelIntegrityError(
                f"missing model file {path} (expected {entry.repository}@"
                f"{entry.revision})"
            )
        actual_size = path.stat().st_size
        if actual_size != item.size:
            raise ModelIntegrityError(
                f"size mismatch for {path}: expected {item.size}, "
                f"got {actual_size}"
            )
        actual_sha = sha256_file(path)
        if actual_sha != item.sha256.lower():
            raise ModelIntegrityError(
                f"sha256 mismatch for {path}: expected {item.sha256}, "
                f"got {actual_sha}"
            )


def verify_entry(entry: ModelEntry, root: str | Path) -> None:
    _verify_entry(entry, Path(root))


def verify_manifest(manifest: ModelManifest, root: str | Path) -> None:
    base = Path(root)
    _verify_entry(manifest.stt, base)
    _verify_entry(manifest.cleaner, base)


def resolve_model_path(
    manifest: ModelManifest, entry: ModelEntry, root: str | Path
) -> Path:
    return Path(root) / entry.id
