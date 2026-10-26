"""Opt-in storage of completed dictations for local voice benchmarks.

The store is disabled by default. When enabled, every successful dictation
writes a PCM16 mono 16 kHz WAV plus one JSONL manifest entry. Audio and text
never leave the machine and are written with owner-only permissions.

The manifest is intentionally shaped like the ``scripts/stt_eval.py`` corpus
manifest: ``reference_raw`` and ``reference_final`` start empty and can be
filled in later. Until they are, the recording is a capture, not a scored
benchmark.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import uuid
import wave
from datetime import datetime, timezone
from pathlib import Path

from .audio import SAMPLE_RATE
from .config import RecordingConfig
from .pipeline import DictationResult

logger = logging.getLogger(__name__)

SCHEMA = "gangio-keet-recording-v1"
_MANIFEST_NAME = "manifest.jsonl"
_AUDIO_DIR_NAME = "audio"


class RecordingStore:
    def __init__(
        self,
        cfg: RecordingConfig,
        stt_model: str,
        llm_model: str,
        *,
        directory: str | Path | None = None,
    ):
        self.enabled = cfg.enabled
        self.store_context = cfg.store_context
        self.stt_model = stt_model
        self.llm_model = llm_model
        self.directory = Path(directory or cfg.directory).expanduser()
        self.audio_directory = self.directory / _AUDIO_DIR_NAME
        self.manifest_path = self.directory / _MANIFEST_NAME
        self._lock = asyncio.Lock()

        if self.enabled:
            self.prepare()

    def prepare(self) -> None:
        """Create the private storage tree or fail loudly at startup."""
        self.directory.mkdir(parents=True, exist_ok=True)
        self.audio_directory.mkdir(parents=True, exist_ok=True)
        os.chmod(self.directory, 0o700)
        os.chmod(self.audio_directory, 0o700)

    async def record(
        self,
        pcm: bytes,
        result: DictationResult,
        *,
        transport: str,
        profile: str,
        bypass_llm: bool,
        context: dict[str, str] | None = None,
    ) -> Path | None:
        """Persist one completed dictation without ever failing the request."""
        if not self.enabled or not pcm:
            return None

        entry = self._build_entry(
            pcm,
            result,
            transport=transport,
            profile=profile,
            bypass_llm=bypass_llm,
            context=context,
        )
        async with self._lock:
            try:
                audio_path = await asyncio.to_thread(self._write, pcm, entry)
            except Exception as exc:
                logger.warning(
                    "recording failed type=%s", type(exc).__name__
                )
                return None

        logger.info(
            "recording saved id=%s transport=%s duration_s=%.2f",
            entry["id"],
            transport,
            entry["duration_seconds"],
        )
        return audio_path

    def _build_entry(
        self,
        pcm: bytes,
        result: DictationResult,
        *,
        transport: str,
        profile: str,
        bypass_llm: bool,
        context: dict[str, str] | None,
    ) -> dict:
        created_at = datetime.now(timezone.utc)
        clip_id = (
            f"{created_at.strftime('%Y%m%dT%H%M%S%fZ')}-"
            f"{uuid.uuid4().hex[:8]}"
        )
        entry = {
            "schema": SCHEMA,
            "id": clip_id,
            "audio_path": f"{_AUDIO_DIR_NAME}/{clip_id}.wav",
            "audio_sha256": hashlib.sha256(pcm).hexdigest(),
            "audio_bytes": len(pcm),
            "duration_seconds": round(
                len(pcm) / (2 * SAMPLE_RATE), 3
            ),
            "sample_rate": SAMPLE_RATE,
            "channels": 1,
            "sample_width": 2,
            "created_at": created_at.isoformat(),
            "transport": transport,
            "profile": profile,
            "bypass_llm": bypass_llm,
            "stt_model": self.stt_model,
            "llm_model": self.llm_model,
            "raw_text": result.raw_text,
            "final_text": result.final_text,
            "fast_path": result.fast_path,
            "warnings": list(result.warnings),
            "metrics": result.metrics.model_dump(),
            # stt_eval.py corpus contract. Fill the references after capture.
            "reference_raw": "",
            "reference_final": "",
            "category": "it_general",
            "technical_terms": [],
            "is_silence": False,
        }
        stored_context = self._stored_context(context)
        if stored_context is not None:
            entry["context"] = stored_context
        return entry

    def _stored_context(
        self, context: dict[str, str] | None
    ) -> dict[str, str] | None:
        if not self.store_context or not context:
            return None
        stored = {
            key: context[key]
            for key in ("app_name", "window_title")
            if context.get(key)
        }
        # selected_text is deliberately never persisted.
        return stored or None

    def _write(self, pcm: bytes, entry: dict) -> Path:
        if not self.audio_directory.is_dir():
            raise FileNotFoundError(
                f"recording directory is missing: {self.audio_directory}"
            )
        audio_path = self.directory / entry["audio_path"]
        temporary = audio_path.with_name(f".{audio_path.name}.tmp")
        try:
            with wave.open(str(temporary), "wb") as handle:
                handle.setnchannels(1)
                handle.setsampwidth(2)
                handle.setframerate(SAMPLE_RATE)
                handle.writeframes(pcm)
            os.chmod(temporary, 0o600)
            os.replace(temporary, audio_path)
        finally:
            temporary.unlink(missing_ok=True)

        line = (
            json.dumps(entry, ensure_ascii=False, separators=(",", ":"))
            + "\n"
        )
        fd = os.open(
            self.manifest_path,
            os.O_WRONLY | os.O_CREAT | os.O_APPEND,
            0o600,
        )
        try:
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "ab") as handle:
                fd = -1
                handle.write(line.encode("utf-8"))
                handle.flush()
                os.fsync(handle.fileno())
        finally:
            if fd >= 0:
                os.close(fd)
        return audio_path
