"""Admission control for audio sessions and the shared byte budget.

All methods are synchronous: the server runs them on the single event-loop
thread, so counters are updated without awaits or locks.
"""

from __future__ import annotations

from dataclasses import dataclass

from .errors import DictationFailure


@dataclass(frozen=True)
class AdmissionSnapshot:
    """Point-in-time counters without exposing retained audio contents."""

    sessions: int
    retained_audio_bytes: int


class AudioLease:
    """A session slot plus the bytes retained by that session.

    A lease starts with no reserved bytes. ``reserve`` is atomic: when the
    shared budget cannot satisfy the whole request, neither the controller
    nor the lease is modified. ``close`` is idempotent and releases both the
    remaining bytes and the session slot exactly once. ``transfer`` keeps the
    same lease (and therefore the same session slot) for a later owner.
    """

    def __init__(self, controller: "AdmissionController"):
        self._controller = controller
        self._reserved_bytes = 0
        self._closed = False
        self._transferred = False

    @property
    def closed(self) -> bool:
        return self._closed

    @property
    def transferred(self) -> bool:
        return self._transferred

    @property
    def reserved_bytes(self) -> int:
        return self._reserved_bytes

    def reserve(self, byte_count: int) -> None:
        if byte_count < 0:
            raise ValueError("byte_count must be >= 0")
        if self._closed:
            raise DictationFailure("internal", "audio lease is closed")
        if byte_count == 0:
            return

        controller = self._controller
        if (
            controller._retained_audio_bytes + byte_count
            > controller._max_bytes
        ):
            raise DictationFailure(
                "busy", "audio budget exhausted", retryable=True
            )

        controller._retained_audio_bytes += byte_count
        self._reserved_bytes += byte_count

    def release_bytes(self, byte_count: int) -> None:
        if byte_count < 0:
            raise ValueError("byte_count must be >= 0")
        if self._closed:
            return

        released = min(byte_count, self._reserved_bytes)
        if released == 0:
            return
        self._reserved_bytes -= released
        self._controller._retained_audio_bytes -= released

    def transfer(self) -> "AudioLease":
        if self._closed:
            raise DictationFailure("internal", "audio lease is closed")
        self._transferred = True
        return self

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True

        released = self._reserved_bytes
        self._reserved_bytes = 0
        if released:
            self._controller._retained_audio_bytes -= released
        self._controller._sessions -= 1


class AdmissionController:
    """Limits concurrent sessions and the cumulative retained-audio budget."""

    def __init__(self, max_sessions: int, max_bytes: int):
        self._max_sessions = max_sessions
        self._max_bytes = max_bytes
        self._sessions = 0
        self._retained_audio_bytes = 0

    def acquire(self) -> AudioLease:
        if self._sessions >= self._max_sessions:
            raise DictationFailure(
                "busy", "too many active audio sessions", retryable=True
            )
        self._sessions += 1
        return AudioLease(self)

    def snapshot(self) -> AdmissionSnapshot:
        return AdmissionSnapshot(
            sessions=self._sessions,
            retained_audio_bytes=self._retained_audio_bytes,
        )
