import asyncio
import logging
import threading
import time
from pathlib import Path

import pytest

from dictation_server.admission import AdmissionController
from dictation_server.config import load_config
from dictation_server.errors import DictationFailure
from dictation_server.pipeline import DictationResult, Metrics, Pipeline
from dictation_server.scheduler import DictationScheduler, SchedulerSnapshot
from tests.fakes import (
    BlockingFakeLLM,
    BlockingFakeSTT,
    FakeLLM,
    FakeSTT,
)
from tests.helpers import make_silence

CONFIG_PATH = Path(__file__).resolve().parents[1] / "config.yaml"
PCM = make_silence(0.1)


def _admission(max_bytes: int = 1_000_000) -> AdmissionController:
    return AdmissionController(max_sessions=8, max_bytes=max_bytes)


def _lease(admission: AdmissionController, pcm: bytes = PCM):
    lease = admission.acquire()
    lease.reserve(len(pcm))
    return lease


def _scheduler(
    *,
    stt=None,
    llm=None,
    pipeline=None,
    max_pending_jobs: int = 2,
    queue_timeout: float = 0.5,
    processing_timeout: float = 1.0,
) -> DictationScheduler:
    if pipeline is None:
        pipeline = Pipeline(
            stt or FakeSTT(),
            llm or FakeLLM(),
            load_config(CONFIG_PATH),
        )
    return DictationScheduler(
        pipeline,
        max_pending_jobs=max_pending_jobs,
        queue_timeout=queue_timeout,
        processing_timeout=processing_timeout,
    )


async def _wait_until(predicate, timeout: float = 2.0) -> None:
    loop = asyncio.get_running_loop()
    deadline = loop.time() + timeout
    while loop.time() < deadline:
        if predicate():
            return
        await asyncio.sleep(0.01)
    assert predicate()


async def _wait_thread(event, timeout: float = 3.0) -> None:
    await asyncio.wait_for(asyncio.to_thread(event.wait), timeout)


async def _shutdown(scheduler: DictationScheduler, stt=None) -> None:
    if stt is not None:
        stt.release.set()
    await scheduler.close()


@pytest.mark.asyncio
async def test_capacity_is_one_active_plus_two_pending_and_fifo():
    admission = _admission()
    stt, llm = BlockingFakeSTT(), FakeLLM()
    scheduler = _scheduler(stt=stt, llm=llm)

    first_lease = _lease(admission)
    first = asyncio.create_task(
        scheduler.submit(PCM, "code", False, first_lease)
    )
    await _wait_thread(stt.started)

    second_lease = _lease(admission)
    third_lease = _lease(admission)
    second = asyncio.create_task(
        scheduler.submit(PCM, "terminal", False, second_lease)
    )
    third = asyncio.create_task(
        scheduler.submit(PCM, "chat", False, third_lease)
    )
    await _wait_until(lambda: scheduler.snapshot().pending == 2)

    rejected = _lease(admission)
    with pytest.raises(DictationFailure) as info:
        await scheduler.submit(PCM, "email", False, rejected)
    assert info.value.code == "busy"
    assert info.value.retryable is True
    assert rejected.transferred is False
    assert rejected.closed is False
    rejected.close()

    assert scheduler.snapshot() == SchedulerSnapshot(
        active=True, pending=2, draining=False
    )
    assert llm.calls == []

    stt.release.set()
    results = await asyncio.gather(first, second, third)

    assert [r.final_text.split("]")[0].lstrip("[") for r in results] == [
        "code",
        "terminal",
        "chat",
    ]
    assert [profile for _, profile in llm.calls] == [
        "code",
        "terminal",
        "chat",
    ]
    assert stt.max_active == 1
    await _wait_until(lambda: scheduler.snapshot().active is False)
    assert admission.snapshot().sessions == 0
    await scheduler.close()


@pytest.mark.asyncio
async def test_queue_timeout_expires_while_engine_thread_is_blocked():
    admission = _admission()
    stt = BlockingFakeSTT()
    scheduler = _scheduler(
        stt=stt, queue_timeout=0.2, processing_timeout=5.0
    )

    active_lease = _lease(admission)
    active = asyncio.create_task(
        scheduler.submit(PCM, "default", True, active_lease)
    )
    await _wait_thread(stt.started)

    expired_lease = _lease(admission)
    expired = asyncio.create_task(
        scheduler.submit(PCM, "default", True, expired_lease)
    )
    await _wait_until(lambda: scheduler.snapshot().pending == 1)

    with pytest.raises(DictationFailure) as info:
        await asyncio.wait_for(expired, timeout=2.0)
    assert info.value.code == "queue_timeout"
    assert info.value.retryable is True
    assert expired_lease.closed is True
    assert scheduler.snapshot().pending == 0
    assert scheduler.snapshot().active is True
    assert stt.starts == 1

    stt.release.set()
    await active
    assert stt.starts == 1
    assert active_lease.closed is True
    assert admission.snapshot().sessions == 0
    await scheduler.close()


@pytest.mark.asyncio
async def test_abort_while_queued_removes_job_and_frees_slot():
    admission = _admission()
    stt = BlockingFakeSTT()
    scheduler = _scheduler(
        stt=stt, queue_timeout=5.0, processing_timeout=5.0
    )

    active_lease = _lease(admission)
    active = asyncio.create_task(
        scheduler.submit(PCM, "default", True, active_lease)
    )
    await _wait_thread(stt.started)

    aborted_lease = _lease(admission)
    aborted = asyncio.create_task(
        scheduler.submit(PCM, "default", True, aborted_lease)
    )
    await _wait_until(lambda: scheduler.snapshot().pending == 1)

    aborted.cancel()
    with pytest.raises(asyncio.CancelledError):
        await aborted

    assert scheduler.snapshot().pending == 0
    assert aborted_lease.closed is True
    assert stt.starts == 1

    replacement_lease = _lease(admission)
    replacement = asyncio.create_task(
        scheduler.submit(PCM, "default", True, replacement_lease)
    )
    await _wait_until(lambda: scheduler.snapshot().pending == 1)

    stt.release.set()
    await active
    await replacement
    assert replacement_lease.closed is True
    assert admission.snapshot().sessions == 0
    await scheduler.close()


@pytest.mark.asyncio
async def test_total_metrics_include_queue_wait_and_queue_ms_is_logged(
    caplog,
):
    admission = _admission()
    stt = BlockingFakeSTT()
    scheduler = _scheduler(
        stt=stt, queue_timeout=2.0, processing_timeout=5.0
    )

    first_lease = _lease(admission)
    first = asyncio.create_task(
        scheduler.submit(PCM, "default", True, first_lease)
    )
    await _wait_thread(stt.started)

    second_lease = _lease(admission)
    with caplog.at_level(
        logging.DEBUG, logger="dictation_server.scheduler"
    ):
        second = asyncio.create_task(
            scheduler.submit(PCM, "code", False, second_lease)
        )
        await _wait_until(lambda: scheduler.snapshot().pending == 1)
        await asyncio.sleep(0.15)
        stt.release.set()
        first_result, second_result = await asyncio.gather(first, second)

    assert second_result.metrics.total_ms >= 100.0
    assert second_result.metrics.total_ms >= (
        second_result.metrics.stt_ms + second_result.metrics.llm_ms
    )
    assert second_result.metrics.stt_ms < 100.0
    assert "queue_ms" in caplog.text
    assert first_result.metrics.total_ms >= 0.0
    assert second_result.final_text.startswith("[code]")
    await scheduler.close()


@pytest.mark.asyncio
async def test_processing_timeout_resolves_caller_then_drains_before_next():
    admission = _admission()
    stt = BlockingFakeSTT()
    scheduler = _scheduler(
        stt=stt, queue_timeout=2.0, processing_timeout=0.15
    )

    timed_out_lease = _lease(admission)
    timed_out = asyncio.create_task(
        scheduler.submit(PCM, "default", True, timed_out_lease)
    )
    await _wait_thread(stt.started)

    with pytest.raises(DictationFailure) as info:
        await asyncio.wait_for(timed_out, timeout=2.0)
    assert info.value.code == "processing_timeout"
    assert info.value.retryable is True
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=True, pending=0, draining=True
    )
    assert timed_out_lease.closed is False

    next_lease = _lease(admission)
    next_job = asyncio.create_task(
        scheduler.submit(PCM, "default", True, next_lease)
    )
    await _wait_until(lambda: scheduler.snapshot().pending == 1)
    await asyncio.sleep(0.1)
    assert stt.starts == 1
    assert scheduler.snapshot().draining is True

    stt.release.set()
    await _wait_until(
        lambda: scheduler.snapshot()
        == SchedulerSnapshot(active=False, pending=0, draining=False)
    )
    assert timed_out_lease.closed is True
    await next_job
    assert next_lease.closed is True
    assert stt.starts == 2
    assert admission.snapshot().sessions == 0
    await scheduler.close()


@pytest.mark.asyncio
async def test_processing_deadline_starts_when_worker_acquires_job():
    admission = _admission()

    class TimedFakeSTT(FakeSTT):
        def __init__(self):
            super().__init__()
            self.started = threading.Event()
            self.release = threading.Event()
            self.starts = 0

        def transcribe(self, audio):
            self.starts += 1
            if self.starts == 1:
                self.started.set()
                self.release.wait()
            else:
                time.sleep(0.3)
            return super().transcribe(audio)

    stt = TimedFakeSTT()
    scheduler = _scheduler(
        stt=stt, queue_timeout=2.0, processing_timeout=0.5
    )

    active_lease = _lease(admission)
    active = asyncio.create_task(
        scheduler.submit(PCM, "default", True, active_lease)
    )
    await _wait_thread(stt.started)

    queued_lease = _lease(admission)
    queued = asyncio.create_task(
        scheduler.submit(PCM, "default", True, queued_lease)
    )
    await _wait_until(lambda: scheduler.snapshot().pending == 1)
    await asyncio.sleep(0.35)

    stt.release.set()
    await active
    result = await asyncio.wait_for(queued, timeout=2.0)
    assert result.final_text == "ehm ciao fast a pi"
    assert queued_lease.closed is True
    await scheduler.close()


@pytest.mark.asyncio
async def test_cancellation_during_stt_waits_for_real_drain():
    admission = _admission()
    stt = BlockingFakeSTT()
    scheduler = _scheduler(
        stt=stt, queue_timeout=2.0, processing_timeout=5.0
    )

    lease = _lease(admission)
    job = asyncio.create_task(scheduler.submit(PCM, "default", True, lease))
    await _wait_thread(stt.started)

    job.cancel()
    await asyncio.sleep(0.05)
    assert job.done() is False
    assert lease.closed is False
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=True, pending=0, draining=True
    )

    job.cancel()
    await asyncio.sleep(0.05)
    assert job.done() is False
    assert stt.active == 1
    assert lease.closed is False

    stt.release.set()
    with pytest.raises(asyncio.CancelledError):
        await job

    assert lease.closed is True
    assert stt.active == 0
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=False, pending=0, draining=False
    )
    assert admission.snapshot().sessions == 0

    followup_lease = _lease(admission)
    followup = await scheduler.submit(PCM, "default", True, followup_lease)
    assert followup.final_text == "ehm ciao fast a pi"
    await scheduler.close()


@pytest.mark.asyncio
async def test_cancellation_during_llm_returns_no_result():
    admission = _admission()
    llm = BlockingFakeLLM()
    scheduler = _scheduler(
        stt=FakeSTT(), llm=llm, queue_timeout=2.0, processing_timeout=5.0
    )

    lease = _lease(admission)
    job = asyncio.create_task(
        scheduler.submit(PCM, "code", False, lease)
    )
    await _wait_thread(llm.started)

    job.cancel()
    await asyncio.sleep(0.05)
    assert job.done() is False
    assert lease.closed is False

    llm.release.set()
    with pytest.raises(asyncio.CancelledError):
        await job

    assert llm.starts == 1
    assert lease.closed is True
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=False, pending=0, draining=False
    )
    assert admission.snapshot().sessions == 0
    await scheduler.close()


@pytest.mark.asyncio
async def test_drain_error_is_logged_and_cleanup_is_preserved(caplog):
    admission = _admission()

    class CancelFailingPipeline:
        def __init__(self):
            self.calls = 0

        async def process(self, pcm, profile="default", bypass_llm=False):
            self.calls += 1
            if self.calls == 1:
                try:
                    await asyncio.sleep(60)
                except asyncio.CancelledError:
                    await asyncio.sleep(0.02)
                    raise RuntimeError("drain exploded") from None
            return DictationResult(
                raw_text="done",
                final_text="done",
                fast_path=True,
                metrics=Metrics(stt_ms=1.0, llm_ms=0.0, total_ms=1.0),
            )

    pipeline = CancelFailingPipeline()
    scheduler = _scheduler(
        pipeline=pipeline,
        queue_timeout=2.0,
        processing_timeout=0.1,
    )

    lease = _lease(admission)
    with caplog.at_level(
        logging.WARNING, logger="dictation_server.scheduler"
    ):
        with pytest.raises(DictationFailure) as info:
            await scheduler.submit(PCM, "default", True, lease)
        await _wait_until(
            lambda: scheduler.snapshot()
            == SchedulerSnapshot(active=False, pending=0, draining=False)
        )

    assert info.value.code == "processing_timeout"
    assert "RuntimeError" in caplog.text
    assert "drain exploded" not in caplog.text
    assert lease.closed is True
    assert admission.snapshot().sessions == 0

    next_lease = _lease(admission)
    result = await scheduler.submit(PCM, "default", True, next_lease)
    assert result.final_text == "done"
    await scheduler.close()


@pytest.mark.asyncio
async def test_close_cancels_pending_and_awaits_active_drain():
    admission = _admission()
    stt = BlockingFakeSTT()
    scheduler = _scheduler(
        stt=stt, queue_timeout=10.0, processing_timeout=10.0
    )

    active_lease = _lease(admission)
    active = asyncio.create_task(
        scheduler.submit(PCM, "default", True, active_lease)
    )
    await _wait_thread(stt.started)

    pending_lease = _lease(admission)
    pending = asyncio.create_task(
        scheduler.submit(PCM, "default", True, pending_lease)
    )
    await _wait_until(lambda: scheduler.snapshot().pending == 1)

    close_task = asyncio.create_task(scheduler.close())
    await _wait_until(lambda: scheduler.snapshot().draining is True)
    assert close_task.done() is False

    with pytest.raises(DictationFailure) as info:
        await asyncio.wait_for(pending, timeout=2.0)
    assert info.value.code == "busy"
    assert info.value.retryable is True
    assert pending_lease.closed is True
    assert active_lease.closed is False

    stt.release.set()
    await asyncio.wait_for(close_task, timeout=3.0)
    with pytest.raises(DictationFailure) as info:
        await active
    assert info.value.code == "busy"
    assert info.value.retryable is True

    assert active_lease.closed is True
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=False, pending=0, draining=False
    )
    await scheduler.close()
    assert admission.snapshot().sessions == 0

    rejected = _lease(admission)
    with pytest.raises(DictationFailure) as info:
        await scheduler.submit(PCM, "default", True, rejected)
    assert info.value.code == "busy"
    assert rejected.transferred is False
    rejected.close()

    leftovers = [
        task
        for task in asyncio.all_tasks()
        if task.get_name().startswith("dictation-scheduler")
    ]
    assert leftovers == []


@pytest.mark.asyncio
async def test_start_is_idempotent_and_submit_lazily_starts():
    scheduler = _scheduler()
    await scheduler.start()
    await scheduler.start()
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=False, pending=0, draining=False
    )

    admission = _admission()
    lease = _lease(admission)
    result = await scheduler.submit(PCM, "default", True, lease)
    assert result.final_text == "ehm ciao fast a pi"
    await scheduler.close()
    await scheduler.close()


@pytest.mark.asyncio
async def test_synchronous_pipeline_crash_fails_caller_and_releases_lease():
    admission = _admission()

    class ExplodingPipeline:
        def process(self, *_args, **_kwargs):
            raise RuntimeError("sync boom")

    scheduler = _scheduler(pipeline=ExplodingPipeline())
    lease = _lease(admission)

    with pytest.raises(DictationFailure) as info:
        await asyncio.wait_for(
            scheduler.submit(PCM, "default", True, lease), timeout=1.0
        )
    assert info.value.code == "internal"
    assert lease.closed is True
    assert scheduler.snapshot() == SchedulerSnapshot(
        active=False, pending=0, draining=False
    )
    assert admission.snapshot().sessions == 0
    await scheduler.close()


@pytest.mark.asyncio
async def test_scheduler_preserves_warnings_from_pipeline():
    admission = _admission()

    class WarningPipeline:
        async def process(self, pcm, profile="default", bypass_llm=False):
            return DictationResult(
                raw_text="raw",
                final_text="final",
                fast_path=False,
                metrics=Metrics(stt_ms=1.0, llm_ms=2.0, total_ms=3.0),
                warnings=["llm_failed"],
            )

    scheduler = _scheduler(pipeline=WarningPipeline())
    lease = _lease(admission)
    result = await scheduler.submit(PCM, "default", False, lease)
    assert result.raw_text == "raw"
    assert result.final_text == "final"
    assert result.fast_path is False
    assert result.warnings == ["llm_failed"]
    assert result.metrics.stt_ms == 1.0
    assert result.metrics.llm_ms == 2.0
    assert result.metrics.total_ms >= 3.0
    await scheduler.close()
