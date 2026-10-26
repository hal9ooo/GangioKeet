import pytest

from dictation_server.admission import (
    AdmissionController,
    AdmissionSnapshot,
)
from dictation_server.errors import DictationFailure

MIB = 1024 * 1024


def test_four_sessions_and_192_mib_cumulative_budget():
    admission = AdmissionController(max_sessions=4, max_bytes=192 * MIB)
    leases = [admission.acquire() for _ in range(4)]
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=4, retained_audio_bytes=0
    )

    with pytest.raises(DictationFailure) as info:
        admission.acquire()
    assert info.value.code == "busy"
    assert info.value.retryable is True
    assert admission.snapshot().sessions == 4

    for lease in leases:
        lease.reserve(48 * MIB)
    assert admission.snapshot().retained_audio_bytes == 192 * MIB

    with pytest.raises(DictationFailure) as info:
        leases[0].reserve(1)
    assert info.value.code == "busy"
    assert info.value.retryable is True
    assert admission.snapshot().retained_audio_bytes == 192 * MIB

    leases[3].release_bytes(48 * MIB)
    leases[3].reserve(1)
    assert admission.snapshot().retained_audio_bytes == 144 * MIB + 1

    for lease in leases:
        lease.close()
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=0, retained_audio_bytes=0
    )


def test_reserve_is_atomic_when_budget_is_exhausted():
    admission = AdmissionController(max_sessions=2, max_bytes=100)
    first = admission.acquire()
    second = admission.acquire()

    first.reserve(60)
    with pytest.raises(DictationFailure) as info:
        second.reserve(50)

    assert info.value.code == "busy"
    assert info.value.retryable is True
    assert second.reserved_bytes == 0
    assert admission.snapshot().retained_audio_bytes == 60

    second.reserve(40)
    assert second.reserved_bytes == 40
    assert admission.snapshot().retained_audio_bytes == 100

    with pytest.raises(DictationFailure):
        second.reserve(1)
    assert second.reserved_bytes == 40
    assert admission.snapshot().retained_audio_bytes == 100


def test_close_is_idempotent_and_releases_owned_bytes():
    admission = AdmissionController(max_sessions=2, max_bytes=100)
    lease = admission.acquire()
    lease.reserve(30)

    lease.close()
    lease.close()

    assert lease.closed is True
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=0, retained_audio_bytes=0
    )

    replacement = admission.acquire()
    assert admission.snapshot().sessions == 1
    replacement.close()
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=0, retained_audio_bytes=0
    )


def test_transfer_keeps_one_session_and_the_same_bytes():
    admission = AdmissionController(max_sessions=1, max_bytes=100)
    lease = admission.acquire()
    lease.reserve(40)

    moved = lease.transfer()

    assert moved is lease
    assert lease.transferred is True
    assert lease.closed is False
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=1, retained_audio_bytes=40
    )

    with pytest.raises(DictationFailure) as info:
        admission.acquire()
    assert info.value.code == "busy"

    moved.reserve(10)
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=1, retained_audio_bytes=50
    )

    moved.close()
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=0, retained_audio_bytes=0
    )


def test_release_never_drives_counters_negative():
    admission = AdmissionController(max_sessions=1, max_bytes=100)
    lease = admission.acquire()
    lease.reserve(10)

    lease.release_bytes(1000)
    assert lease.reserved_bytes == 0
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=1, retained_audio_bytes=0
    )

    lease.release_bytes(5)
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=1, retained_audio_bytes=0
    )

    lease.close()
    lease.release_bytes(5)
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=0, retained_audio_bytes=0
    )


def test_zero_reserve_is_a_noop():
    admission = AdmissionController(max_sessions=1, max_bytes=100)
    lease = admission.acquire()

    lease.reserve(0)
    lease.release_bytes(0)

    assert lease.reserved_bytes == 0
    assert admission.snapshot() == AdmissionSnapshot(
        sessions=1, retained_audio_bytes=0
    )


def test_negative_byte_counts_are_rejected():
    admission = AdmissionController(max_sessions=1, max_bytes=100)
    lease = admission.acquire()

    with pytest.raises(ValueError):
        lease.reserve(-1)
    with pytest.raises(ValueError):
        lease.release_bytes(-1)

    assert admission.snapshot() == AdmissionSnapshot(
        sessions=1, retained_audio_bytes=0
    )


def test_closed_lease_cannot_reserve_or_transfer():
    admission = AdmissionController(max_sessions=1, max_bytes=100)
    lease = admission.acquire()
    lease.close()

    with pytest.raises(DictationFailure) as info:
        lease.reserve(1)
    assert info.value.code == "internal"
    assert info.value.retryable is False

    with pytest.raises(DictationFailure):
        lease.transfer()
