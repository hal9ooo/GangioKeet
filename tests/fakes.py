import threading
import time


class FakeSTT:
    def __init__(self):
        self.seen = []

    def transcribe(self, audio):
        self.seen.append(audio)
        return "ehm ciao fast a pi"


class FakeLLM:
    def __init__(self):
        self.calls = []

    def clean(self, raw_text, profile):
        self.calls.append((raw_text, profile))
        return f"[{profile}] {raw_text}"


class EagerFakeSTT(FakeSTT):
    def __init__(self):
        super().__init__()
        self.loads = []

    def load(self):
        self.loads.append("load")


class EagerFakeLLM(FakeLLM):
    def __init__(self):
        super().__init__()
        self.loads = []
        self.frozen_with = None

    def load(self):
        self.loads.append("load")

    def freeze(self, system_prompt):
        self.loads.append("freeze")
        self.frozen_with = system_prompt


class SlowFakeLLM(FakeLLM):
    def __init__(self, delay: float = 0.3):
        super().__init__()
        self.delay = delay
        self.finished = False

    def clean(self, raw_text, profile):
        self.calls.append((raw_text, profile))
        time.sleep(self.delay)
        self.finished = True
        return "l"


class TracedFakeSTT(FakeSTT):
    def __init__(self, events, delay: float = 0.05):
        super().__init__()
        self.events = events
        self.delay = delay

    def transcribe(self, audio):
        self.events.append((time.perf_counter(), "start", "stt"))
        try:
            time.sleep(self.delay)
            return super().transcribe(audio)
        finally:
            self.events.append((time.perf_counter(), "end", "stt"))


class TracedFakeLLM(FakeLLM):
    def __init__(self, events, delay: float = 0.05):
        super().__init__()
        self.events = events
        self.delay = delay

    def clean(self, raw_text, profile):
        self.events.append((time.perf_counter(), "start", "llm"))
        try:
            time.sleep(self.delay)
            return super().clean(raw_text, profile)
        finally:
            self.events.append((time.perf_counter(), "end", "llm"))


class Trace:
    @staticmethod
    def no_overlap(events) -> bool:
        active = set()
        for _, kind, name in sorted(events, key=lambda event: event[0]):
            if kind == "start":
                if active:
                    return False
                active.add(name)
            else:
                active.discard(name)
        return not active


class ClearCounter:
    def __init__(self):
        self.n = 0

    def __call__(self):
        self.n += 1


class BlockingFakeSTT(FakeSTT):
    """STT whose worker thread can be held across a processing deadline.

    ``started`` is set when a transcription begins, and the thread then waits
    for ``release``. This keeps a real ``asyncio.to_thread`` worker blocked so
    tests can observe scheduler deadlines and drain semantics.
    """

    def __init__(self):
        super().__init__()
        self.started = threading.Event()
        self.release = threading.Event()
        self.finished = threading.Event()
        self._lock = threading.Lock()
        self.starts = 0
        self.active = 0
        self.max_active = 0

    def transcribe(self, audio):
        with self._lock:
            self.starts += 1
            self.active += 1
            self.max_active = max(self.max_active, self.active)
        self.started.set()
        try:
            self.release.wait()
            return super().transcribe(audio)
        finally:
            with self._lock:
                self.active -= 1
            self.finished.set()


class BlockingFakeLLM(FakeLLM):
    """LLM whose worker thread can be held across a processing deadline."""

    def __init__(self):
        super().__init__()
        self.started = threading.Event()
        self.release = threading.Event()
        self.finished = threading.Event()
        self.starts = 0

    def clean(self, raw_text, profile):
        self.starts += 1
        self.started.set()
        try:
            self.release.wait()
            return super().clean(raw_text, profile)
        finally:
            self.finished.set()


def wait_thread_event(event: threading.Event, timeout: float = 3.0) -> bool:
    return event.wait(timeout)
