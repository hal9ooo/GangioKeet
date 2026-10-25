import asyncio
import importlib.util
from pathlib import Path

import pytest

BENCHMARK_PATH = (
    Path(__file__).resolve().parents[1] / "scripts" / "benchmark_mac.py"
)
_SPEC = importlib.util.spec_from_file_location(
    "benchmark_mac", BENCHMARK_PATH
)
benchmark_mac = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(benchmark_mac)


class HangingSocket:
    async def send(self, data):
        pass

    async def recv(self):
        await asyncio.sleep(0.2)
        return "{}"


@pytest.mark.asyncio
async def test_request_fails_on_result_timeout(monkeypatch):
    monkeypatch.setattr(benchmark_mac, "RESULT_TIMEOUT_S", 0.05)

    with pytest.raises(RuntimeError, match="did not return a result"):
        await benchmark_mac._request(
            HangingSocket(), b"\x00\x00", "code", False
        )
