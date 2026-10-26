"""Single-worker FIFO scheduler with queue and processing deadlines.

The scheduler owns a job's audio lease from the moment it is accepted until
the engine work for that job has really finished (or its worker thread has
drained after cancellation). A rejected job never changes lease ownership, so
the caller can always release it safely.

Only one worker task executes jobs, and it is created lazily. Queue deadlines
are enforced by a separate watchdog task so an expired job is released even
while the active engine thread is blocked.
"""

from __future__ import annotations

import asyncio
import logging
from collections import deque
from dataclasses import dataclass

from .admission import AudioLease
from .errors import DictationFailure
from .pipeline import DictationResult, Pipeline

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class SchedulerSnapshot:
    active: bool
    pending: int
    draining: bool


def _queue_timeout_failure() -> DictationFailure:
    return DictationFailure(
        "queue_timeout", "queue deadline exceeded", retryable=True
    )


def _processing_timeout_failure() -> DictationFailure:
    return DictationFailure(
        "processing_timeout", "processing deadline exceeded", retryable=True
    )


def _shutdown_failure() -> DictationFailure:
    return DictationFailure(
        "busy", "scheduler is shutting down", retryable=True
    )


@dataclass(eq=False)
class _Job:
    pcm: bytes
    profile: str
    bypass_llm: bool
    lease: AudioLease
    future: asyncio.Future
    done_future: asyncio.Future
    accepted_at: float
    deadline: float
    state: str = "pending"
    pipeline_task: asyncio.Task | None = None
    lease_closed: bool = False
    finalized: bool = False


class DictationScheduler:
    def __init__(
        self,
        pipeline: Pipeline,
        max_pending_jobs: int,
        queue_timeout: float,
        processing_timeout: float,
    ):
        if max_pending_jobs < 1:
            raise ValueError("max_pending_jobs must be >= 1")
        if queue_timeout <= 0:
            raise ValueError("queue_timeout must be > 0")
        if processing_timeout <= 0:
            raise ValueError("processing_timeout must be > 0")

        self.pipeline = pipeline
        self.max_pending_jobs = max_pending_jobs
        self.queue_timeout = queue_timeout
        self.processing_timeout = processing_timeout
        self._pending: deque[_Job] = deque()
        self._active: _Job | None = None
        self._draining = False
        self._closed = False
        self._close_complete = False
        self._worker: asyncio.Task | None = None
        self._watchdog: asyncio.Task | None = None
        self._pending_changed: asyncio.Event | None = None
        self._loop: asyncio.AbstractEventLoop | None = None

    # -- public API -----------------------------------------------------

    def snapshot(self) -> SchedulerSnapshot:
        return SchedulerSnapshot(
            active=self._active is not None,
            pending=len(self._pending),
            draining=self._draining,
        )

    async def start(self) -> None:
        if self._closed:
            return
        self._bind_loop()
        self._ensure_services()

    async def submit(
        self,
        pcm: bytes,
        profile: str,
        bypass_llm: bool,
        lease: AudioLease,
    ) -> DictationResult:
        self._bind_loop()
        if self._closed:
            raise _shutdown_failure()
        if self._inflight() >= self.max_pending_jobs + 1:
            raise DictationFailure(
                "busy", "transcription queue is full", retryable=True
            )

        loop = asyncio.get_running_loop()
        now = loop.time()
        job = _Job(
            pcm=pcm,
            profile=profile,
            bypass_llm=bypass_llm,
            lease=lease,
            future=loop.create_future(),
            done_future=loop.create_future(),
            accepted_at=now,
            deadline=now + self.queue_timeout,
        )
        # Ownership moves only after every rejection check has passed.
        lease.transfer()
        self._pending.append(job)
        self._signal_pending_changed()
        self._ensure_services()

        try:
            return await asyncio.shield(job.future)
        except asyncio.CancelledError:
            await self._cancel_job(job)
            raise

    async def close(self) -> None:
        if self._closed and self._close_complete:
            return

        self._closed = True
        self._bind_loop()
        if self._active is not None or self._pending:
            self._draining = True
        self._signal_pending_changed()

        while self._pending:
            job = self._pending.popleft()
            self._fail_job(job, _shutdown_failure())
            self._finalize_job(job)
        self._signal_pending_changed()

        active = self._active
        if active is not None:
            self._draining = True
            self._fail_job(active, _shutdown_failure())
            task = active.pipeline_task
            if task is not None and not task.done():
                task.cancel()
            await self._wait_for_done(active)

        worker = self._worker
        if worker is not None and not worker.done():
            if worker.get_loop() is asyncio.get_running_loop():
                await self._shield_until_done(worker, "scheduler worker")
            else:
                worker.cancel()

        watchdog = self._watchdog
        if watchdog is not None and not watchdog.done():
            if watchdog.get_loop() is asyncio.get_running_loop():
                watchdog.cancel()
                await self._gather_task(watchdog)
            else:
                watchdog.cancel()

        self._draining = False
        self._close_complete = True

    # -- loop/task machinery --------------------------------------------

    def _bind_loop(self) -> None:
        loop = asyncio.get_running_loop()
        if self._loop is loop:
            return
        self._loop = loop
        self._pending_changed = asyncio.Event()
        if self._worker is not None and self._worker.done():
            self._worker = None
        if self._watchdog is not None and self._watchdog.done():
            self._watchdog = None

    def _inflight(self) -> int:
        return len(self._pending) + (1 if self._active is not None else 0)

    def _signal_pending_changed(self) -> None:
        if self._pending_changed is not None:
            self._pending_changed.set()

    def _ensure_services(self) -> None:
        if self._closed:
            return
        if self._pending or self._active is not None:
            if self._worker is None or self._worker.done():
                self._worker = asyncio.create_task(
                    self._worker_run(),
                    name="dictation-scheduler-worker",
                )
        if self._pending:
            if self._watchdog is None or self._watchdog.done():
                self._watchdog = asyncio.create_task(
                    self._watchdog_run(),
                    name="dictation-scheduler-watchdog",
                )

    async def _worker_run(self) -> None:
        loop = asyncio.get_running_loop()
        while not self._closed:
            job = self._claim_next(loop)
            if job is None:
                return
            try:
                await self._execute(job, loop)
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                logger.error(
                    "scheduler job failed type=%s",
                    type(exc).__name__,
                )
                self._fail_job(
                    job,
                    DictationFailure(
                        "internal", "transcription job failed unexpectedly"
                    ),
                )
                self._finalize_job(job)

    def _claim_next(self, loop: asyncio.AbstractEventLoop) -> _Job | None:
        while self._pending:
            job = self._pending.popleft()
            self._signal_pending_changed()
            if job.finalized:
                continue
            if loop.time() >= job.deadline:
                self._fail_job(job, _queue_timeout_failure())
                self._finalize_job(job)
                continue
            job.state = "active"
            self._active = job
            return job
        return None

    async def _watchdog_run(self) -> None:
        loop = asyncio.get_running_loop()
        while not self._closed:
            now = loop.time()
            for job in list(self._pending):
                if job.finalized or job.deadline > now:
                    continue
                try:
                    self._pending.remove(job)
                except ValueError:
                    continue
                self._signal_pending_changed()
                self._fail_job(job, _queue_timeout_failure())
                self._finalize_job(job)

            if not self._pending:
                return

            deadline = min(job.deadline for job in self._pending)
            timeout = max(0.0, deadline - loop.time())
            assert self._pending_changed is not None
            self._pending_changed.clear()
            try:
                await asyncio.wait_for(
                    self._pending_changed.wait(), timeout=timeout
                )
            except TimeoutError:
                continue

    async def _execute(
        self, job: _Job, loop: asyncio.AbstractEventLoop
    ) -> None:
        queue_ms = max(0.0, (loop.time() - job.accepted_at) * 1000.0)
        logger.debug("dictation job start queue_ms=%.1f", queue_ms)

        pipeline_task = asyncio.create_task(
            self.pipeline.process(
                job.pcm,
                profile=job.profile,
                bypass_llm=job.bypass_llm,
            ),
            name="dictation-scheduler-pipeline",
        )
        job.pipeline_task = pipeline_task
        try:
            try:
                await asyncio.wait(
                    {pipeline_task}, timeout=self.processing_timeout
                )
            except asyncio.CancelledError:
                # The worker itself is being cancelled (loop shutdown).
                self._draining = True
                job.state = "draining"
                self._fail_job(job, _shutdown_failure())
                await self._cancel_and_drain(pipeline_task)
                raise

            if not pipeline_task.done():
                self._fail_job(job, _processing_timeout_failure())
                job.state = "draining"
                self._draining = True
                logger.warning(
                    "dictation job processing timeout; draining engine"
                )
                await self._cancel_and_drain(pipeline_task)
                return

            if pipeline_task.cancelled():
                return

            error = pipeline_task.exception()
            if error is not None:
                self._fail_job(job, error)
                if not isinstance(error, DictationFailure):
                    logger.error(
                        "dictation pipeline failed type=%s",
                        type(error).__name__,
                    )
                return

            result = pipeline_task.result()
            result.metrics.total_ms += queue_ms
            self._resolve_job(job, result)
        finally:
            self._finalize_job(job)

    def _finalize_job(self, job: _Job) -> None:
        if job.finalized:
            return
        job.finalized = True
        job.state = "done"

        if not job.lease_closed:
            job.lease_closed = True
            try:
                job.lease.close()
            except Exception as exc:
                logger.error(
                    "failed to release audio lease type=%s",
                    type(exc).__name__,
                )

        if self._active is job:
            self._active = None
            self._draining = False

        if not job.done_future.done():
            job.done_future.set_result(None)

    @staticmethod
    def _fail_job(job: _Job, failure: BaseException) -> None:
        if not job.future.done():
            job.future.set_exception(failure)

    @staticmethod
    def _resolve_job(job: _Job, result: DictationResult) -> None:
        if not job.future.done():
            job.future.set_result(result)

    # -- cancellation/drain ---------------------------------------------

    async def _cancel_job(self, job: _Job) -> None:
        if job.state == "pending":
            try:
                self._pending.remove(job)
            except ValueError:
                pass
            self._signal_pending_changed()
            self._discard_future(job)
            self._finalize_job(job)
            return

        if job.state in {"active", "draining"}:
            self._draining = True
            self._discard_future(job)
            task = job.pipeline_task
            if task is not None and not task.done():
                task.cancel()
            await self._wait_for_done(job)
            return

        self._discard_future(job)

    def _discard_future(self, job: _Job) -> None:
        if not job.future.done():
            job.future.cancel()
            return
        if job.future.cancelled():
            return
        try:
            job.future.exception()
        except BaseException:
            pass

    async def _cancel_and_drain(self, task: asyncio.Task) -> None:
        if not task.done():
            task.cancel()
        await self._shield_until_done(task, "pipeline task")
        if task.done() and not task.cancelled():
            try:
                error = task.exception()
            except BaseException:
                error = None
            if error is not None:
                logger.warning(
                    "pipeline task failed during drain type=%s",
                    type(error).__name__,
                )

    async def _wait_for_done(self, job: _Job) -> None:
        await self._shield_until_done(job.done_future, "job cleanup")

    async def _shield_until_done(self, awaitable, label: str) -> None:
        """Await completion while absorbing repeated caller cancellations.

        Cancellation of the caller must never abandon an engine thread or a
        lease. The original CancelledError is re-raised by the caller once the
        owned work has really finished.
        """
        while not _is_done(awaitable):
            try:
                await asyncio.shield(awaitable)
            except asyncio.CancelledError:
                continue
            except Exception as exc:
                logger.warning(
                    "%s failed during drain type=%s",
                    label,
                    type(exc).__name__,
                )
                break

    async def _gather_task(self, task: asyncio.Task) -> None:
        while not task.done():
            try:
                await asyncio.shield(task)
            except asyncio.CancelledError:
                continue
            except Exception as exc:
                logger.warning(
                    "scheduler task failed during close type=%s",
                    type(exc).__name__,
                )
                break


def _is_done(awaitable) -> bool:
    if isinstance(awaitable, asyncio.Future):
        return awaitable.done()
    done = getattr(awaitable, "done", None)
    if callable(done):
        return bool(done())
    return False
