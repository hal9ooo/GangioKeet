import asyncio
import logging
import time
from typing import Callable

from pydantic import BaseModel, Field

from .audio import SAMPLE_RATE, pcm_bytes_to_mlx_array
from .config import AppConfig
from .llm import LLMCleaner
from .routing import should_run_llm
from .stt import ParakeetSTT

logger = logging.getLogger(__name__)

_WARM_UP_PCM = b"\x00\x00" * SAMPLE_RATE


class Metrics(BaseModel):
    stt_ms: float
    llm_ms: float
    total_ms: float


class DictationResult(BaseModel):
    raw_text: str
    final_text: str
    fast_path: bool
    metrics: Metrics
    warnings: list[str] = Field(default_factory=list)


def _clear_metal_cache() -> None:
    import mlx.core as mx

    mx.clear_cache()


class Pipeline:
    def __init__(
        self,
        stt: ParakeetSTT,
        llm: LLMCleaner,
        cfg: AppConfig,
        clear_metal: Callable[[], None] | None = None,
    ):
        self.stt = stt
        self.llm = llm
        self.cfg = cfg
        self._clear_metal = clear_metal or _clear_metal_cache
        self._metal = asyncio.Lock()

    @staticmethod
    async def _drain(worker: asyncio.Task) -> None:
        while not worker.done():
            try:
                await asyncio.shield(worker)
            except asyncio.CancelledError:
                continue
            except BaseException:
                break
        if worker.done() and not worker.cancelled():
            try:
                error = worker.exception()
            except BaseException:
                error = None
            if error is not None:
                logger.warning(
                    "engine worker failed during cancellation drain: %s",
                    error,
                )

    async def _run_blocking(self, fn, *args):
        worker = asyncio.create_task(asyncio.to_thread(fn, *args))
        try:
            return await asyncio.shield(worker)
        except asyncio.CancelledError:
            await self._drain(worker)
            raise

    async def process(
        self,
        pcm: bytes,
        profile: str = "default",
        bypass_llm: bool = False,
    ) -> DictationResult:
        total_start = time.perf_counter()
        raw_text = ""
        final_text = ""
        stt_ms = 0.0
        llm_ms = 0.0
        fast_path = True

        async with self._metal:
            try:
                if pcm:
                    audio = pcm_bytes_to_mlx_array(pcm)
                else:
                    audio = None

                if audio is not None:
                    stt_start = time.perf_counter()
                    raw_text = await self._run_blocking(
                        self.stt.transcribe, audio
                    )
                    stt_ms = (time.perf_counter() - stt_start) * 1000.0

                    if bypass_llm or not should_run_llm(
                        raw_text,
                        self.cfg.routing,
                        self.cfg.glossary.phonetic_overrides,
                    ):
                        final_text = raw_text
                        fast_path = True
                    else:
                        fast_path = False
                        llm_start = time.perf_counter()
                        final_text = await self._run_blocking(
                            self.llm.clean, raw_text, profile
                        )
                        llm_ms = (time.perf_counter() - llm_start) * 1000.0
            finally:
                self._clear_metal()

        return DictationResult(
            raw_text=raw_text,
            final_text=final_text,
            fast_path=fast_path,
            metrics=Metrics(
                stt_ms=stt_ms,
                llm_ms=llm_ms,
                total_ms=(time.perf_counter() - total_start) * 1000.0,
            ),
        )

    async def warm_up(self) -> Metrics:
        result = await self.process(_WARM_UP_PCM)
        async with self._metal:
            try:
                await self._run_blocking(self.llm.clean, "warm up", "default")
            finally:
                self._clear_metal()
        return result.metrics
