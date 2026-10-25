import argparse
import asyncio
import importlib.metadata
import json
import math
import os
import platform
import resource
import subprocess
import sys
import time
from pathlib import Path

import mlx.core as mx
import uvicorn
import websockets

from dictation_server.config import load_config
from dictation_server.main import build_app
from dictation_server.protocol import MAX_AUDIO_BYTES

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PCM = ROOT / "tests" / "fixtures" / "it_short.pcm"
CHUNK_BYTES = 3200  # 100 ms of PCM16 mono 16 kHz
RSS_GATE_MB = 2.8 * 1024
P95_GATE_MS = 400.0
TTFT_GATE_MS = 25.0


def _sysctl(name: str) -> str:
    try:
        return subprocess.check_output(
            ["sysctl", "-n", name], text=True
        ).strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def _package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def _machine_info() -> dict:
    ram_bytes = _sysctl("hw.memsize")
    ram_gb = (
        int(ram_bytes) / (1024 ** 3) if ram_bytes.isdigit() else 0.0
    )
    return {
        "chip": _sysctl("machdep.cpu.brand_string"),
        "ram_gb": round(ram_gb, 1),
        "macos": platform.mac_ver()[0],
        "machine": platform.machine(),
        "python": platform.python_version(),
        "mlx": _package_version("mlx"),
        "mlx_lm": _package_version("mlx-lm"),
        "parakeet_mlx": _package_version("parakeet-mlx"),
    }


def _current_rss_mb(pid: int | None = None) -> float:
    output = subprocess.check_output(
        ["ps", "-o", "rss=", "-p", str(pid or os.getpid())], text=True
    )
    return int(output.strip()) / 1024


def _peak_rss_mb() -> float:
    peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    if sys.platform != "darwin":
        peak *= 1024
    return peak / (1024 * 1024)


def _percentile(values: list[float], percentile: int) -> float:
    ordered = sorted(values)
    index = math.ceil(percentile / 100 * len(ordered)) - 1
    return ordered[max(0, min(index, len(ordered) - 1))]


def _summary(values: list[float]) -> dict:
    return {
        "count": len(values),
        "p50": round(_percentile(values, 50), 1),
        "p95": round(_percentile(values, 95), 1),
        "max": round(max(values), 1),
    }


async def _start_in_process(cfg):
    load_started = time.perf_counter()
    app = build_app(cfg)
    load_s = time.perf_counter() - load_started
    pipeline = app.state.pipeline

    started = time.perf_counter()
    await pipeline.warm_up()
    warmup_s = time.perf_counter() - started
    metal_peak_warmup_mb = mx.get_peak_memory() / (1024 ** 2)
    mx.reset_peak_memory()

    server = uvicorn.Server(
        uvicorn.Config(
            app,
            host="127.0.0.1",
            port=0,
            log_level="warning",
            access_log=False,
            ws_max_size=MAX_AUDIO_BYTES,
        )
    )
    server_task = asyncio.create_task(server.serve())
    deadline = time.monotonic() + 60
    while not server.started:
        if server_task.done():
            await server_task
            raise RuntimeError("uvicorn exited before becoming ready")
        if time.monotonic() > deadline:
            raise TimeoutError("uvicorn did not start within 60 s")
        await asyncio.sleep(0.02)

    port = server.servers[0].sockets[0].getsockname()[1]
    url = f"ws://127.0.0.1:{port}/ws/dictate"
    return (
        server,
        server_task,
        pipeline,
        url,
        load_s,
        warmup_s,
        metal_peak_warmup_mb,
    )


async def _stop_server(server, server_task) -> None:
    server.should_exit = True
    await server_task


async def _request(
    ws,
    pcm: bytes,
    profile: str,
    bypass_llm: bool,
) -> tuple[dict, float]:
    await ws.send(
        json.dumps(
            {
                "type": "start",
                "app_name": "benchmark",
                "window_title": "benchmark",
                "profile": profile,
                "bypass_llm": bypass_llm,
            }
        )
    )
    for offset in range(0, len(pcm), CHUNK_BYTES):
        await ws.send(pcm[offset:offset + CHUNK_BYTES])
    started = time.perf_counter()
    await ws.send(json.dumps({"type": "stop"}))
    message = json.loads(await ws.recv())
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    if message.get("type") != "result":
        raise RuntimeError(f"unexpected server message: {message!r}")
    return message, elapsed_ms


async def _measure_mode(
    ws,
    pipeline,
    pcm: bytes,
    *,
    bypass_llm: bool,
    samples: int,
    warmups: int,
) -> dict:
    label = "fast_path" if bypass_llm else "llm"
    for _ in range(warmups):
        await _request(ws, pcm, "code", bypass_llm)

    wall_ms: list[float] = []
    metric_total_ms: list[float] = []
    stt_ms: list[float] = []
    llm_ms: list[float] = []
    ttft_ms: list[float] = []

    for _ in range(samples):
        data, elapsed = await _request(ws, pcm, "code", bypass_llm)
        if not data["raw_text"].strip():
            raise RuntimeError(
                "fixture produced an empty transcript; refusing to report "
                "a fake end-to-end result"
            )
        if not data["final_text"].strip():
            raise RuntimeError("pipeline produced an empty final transcript")
        if bypass_llm and not data["fast_path"]:
            raise RuntimeError("bypass_llm request unexpectedly ran the LLM")
        if not bypass_llm and data["fast_path"]:
            raise RuntimeError(
                "LLM benchmark request took the fast path; use a longer "
                "fixture so the real LLM is exercised"
            )
        wall_ms.append(elapsed)
        metric_total_ms.append(data["metrics"]["total_ms"])
        stt_ms.append(data["metrics"]["stt_ms"])
        llm_ms.append(data["metrics"]["llm_ms"])
        if pipeline is not None and not bypass_llm:
            if pipeline.llm.last_ttft_ms <= 0:
                raise RuntimeError("LLM did not report a positive TTFT")
            ttft_ms.append(pipeline.llm.last_ttft_ms)

    result = {
        "mode": label,
        "wall": _summary(wall_ms),
        "total": _summary(metric_total_ms),
        "stt": _summary(stt_ms),
        "llm": _summary(llm_ms) if not bypass_llm else None,
    }
    if ttft_ms:
        result["ttft"] = _summary(ttft_ms)
    return result


def _print_report(
    machine: dict,
    models: dict,
    pcm: bytes,
    load_s: float | None,
    warmup_s: float | None,
    metal_warmup_mb: float | None,
    metal_peak_mb: float | None,
    metal_active_mb: float | None,
    rss: dict,
    llm: dict,
    fast: dict,
) -> list[str]:
    duration_s = len(pcm) / 2 / 16000
    print("== MLX dictation benchmark ==")
    print(
        f"Machine: {machine['chip']}, {machine['ram_gb']} GB, "
        f"macOS {machine['macos']}, Python {machine['python']}"
    )
    print(
        f"Versions: mlx {machine['mlx']}, mlx-lm {machine['mlx_lm']}, "
        f"parakeet-mlx {machine['parakeet_mlx']}"
    )
    print(f"Models: STT {models['stt']} | LLM {models['llm']}")
    print(
        f"Audio: {duration_s:.2f} s ({len(pcm)} bytes), "
        f"chunks of {CHUNK_BYTES} bytes"
    )
    if load_s is not None:
        print(
            f"Model load + freeze: {load_s:.2f} s | "
            f"warm-up: {warmup_s:.2f} s"
        )
    print(f"Samples: {llm['wall']['count']} LLM + {fast['wall']['count']} fast")
    print()
    print(
        f"{'Metric (ms)':<34}{'p50':>10}{'p95':>10}{'max':>10}"
    )
    for label, stats in (
        ("WS stop->result (LLM)", llm["wall"]),
        ("WS stop->result (fast)", fast["wall"]),
        ("Pipeline total (LLM)", llm["total"]),
        ("STT (LLM mode)", llm["stt"]),
        ("LLM total (LLM mode)", llm["llm"]),
        ("LLM TTFT incl. prefill", llm.get("ttft")),
        ("Pipeline total (fast)", fast["total"]),
        ("STT (fast mode)", fast["stt"]),
    ):
        if stats is None:
            continue
        print(
            f"{label:<34}{stats['p50']:>10.1f}"
            f"{stats['p95']:>10.1f}{stats['max']:>10.1f}"
        )
    print()

    def _mb(value):
        return "n/a" if value is None else f"{value:.0f} MB"

    print(
        f"RSS after warm-up: {_mb(rss['after_warmup'])} | "
        f"after short: {_mb(rss['after_short'])} | "
        f"after long: {_mb(rss['after_long'])} | "
        f"peak: {_mb(rss['peak'])}"
    )
    print(f"Metal peak after warm-up: {_mb(metal_warmup_mb)}")
    print(
        f"Metal peak during benchmark: {_mb(metal_peak_mb)} | "
        f"active: {_mb(metal_active_mb)}"
    )
    print()

    gates = {
        "WS p95 LLM < 400 ms": llm["wall"]["p95"] < P95_GATE_MS,
        "WS p95 fast < 400 ms": fast["wall"]["p95"] < P95_GATE_MS,
    }
    rss_values = [
        value for value in rss.values() if value is not None
    ]
    if rss_values:
        gates["RSS < 2.8 GB"] = max(rss_values) < RSS_GATE_MB
    ttft = llm.get("ttft")
    if ttft is not None:
        gates["LLM TTFT p95 < 25 ms"] = ttft["p95"] < TTFT_GATE_MS

    failures = []
    print("Gates:")
    for name, passed in gates.items():
        print(f"  [{'PASS' if passed else 'FAIL'}] {name}")
        if not passed:
            failures.append(name)
    return failures


async def _run(args) -> int:
    cfg = None
    models = {"stt": "external", "llm": "external"}
    pipeline = None
    server = None
    server_task = None
    load_s = None
    warmup_s = None
    metal_warmup_mb = None
    metal_peak_mb = None
    metal_active_mb = None
    rss_pid = None

    if args.url:
        url = args.url
        rss_pid = args.rss_pid
    else:
        cfg = load_config(args.config)
        models = {
            "stt": cfg.server.stt_model,
            "llm": cfg.server.llm_model,
        }
        (
            server,
            server_task,
            pipeline,
            url,
            load_s,
            warmup_s,
            metal_warmup_mb,
        ) = await _start_in_process(cfg)
        rss_pid = os.getpid()

    pcm = Path(args.pcm).read_bytes()
    rss_after_warmup = (
        _current_rss_mb(rss_pid) if rss_pid is not None else None
    )
    rss_after_long = rss_after_warmup

    try:
        async with websockets.connect(
            url, ping_interval=None, max_size=None
        ) as ws:
            llm = await _measure_mode(
                ws,
                pipeline,
                pcm,
                bypass_llm=False,
                samples=args.samples,
                warmups=args.warmups,
            )
            rss_after_short = (
                _current_rss_mb(rss_pid) if rss_pid is not None else None
            )

            fast = await _measure_mode(
                ws,
                pipeline,
                pcm,
                bypass_llm=True,
                samples=args.samples,
                warmups=args.warmups,
            )

            long_pcm = pcm * 4
            await _request(ws, long_pcm, "code", bypass_llm=True)
            if rss_pid is not None:
                rss_after_long = _current_rss_mb(rss_pid)
    finally:
        if server is not None:
            await _stop_server(server, server_task)

    rss = {
        "after_warmup": rss_after_warmup,
        "after_short": rss_after_short,
        "after_long": rss_after_long,
        "peak": _peak_rss_mb() if rss_pid == os.getpid() else None,
    }
    if pipeline is not None:
        metal_peak_mb = mx.get_peak_memory() / (1024 ** 2)
        metal_active_mb = mx.get_active_memory() / (1024 ** 2)

    failures = _print_report(
        _machine_info(),
        models,
        pcm,
        load_s,
        warmup_s,
        metal_warmup_mb,
        metal_peak_mb,
        metal_active_mb,
        rss,
        llm,
        fast,
    )
    summary = {
        "machine": _machine_info(),
        "models": models,
        "audio_seconds": round(len(pcm) / 2 / 16000, 2),
        "samples": args.samples,
        "load_seconds": round(load_s, 2) if load_s is not None else None,
        "warmup_seconds": (
            round(warmup_s, 2) if warmup_s is not None else None
        ),
        "llm": llm,
        "fast": fast,
        "rss_mb": {
            key: (round(value, 1) if value is not None else None)
            for key, value in rss.items()
        },
        "metal_peak_mb": (
            round(metal_peak_mb, 1)
            if metal_peak_mb is not None
            else None
        ),
        "metal_active_mb": (
            round(metal_active_mb, 1)
            if metal_active_mb is not None
            else None
        ),
        "failures": failures,
    }
    if args.json_out:
        Path(args.json_out).write_text(
            json.dumps(summary, indent=2), encoding="utf-8"
        )
        print(f"\nJSON report: {args.json_out}")
    return 1 if failures else 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Benchmark the warm MLX dictation server over WebSocket"
    )
    parser.add_argument("--config", default=str(ROOT / "config.yaml"))
    parser.add_argument("--pcm", default=str(DEFAULT_PCM))
    parser.add_argument(
        "--url",
        default=None,
        help="connect to an already-running warm server instead of "
        "starting one in-process",
    )
    parser.add_argument(
        "--rss-pid",
        type=int,
        default=None,
        help="PID to sample when using --url (default: this process)",
    )
    parser.add_argument("--samples", type=int, default=30)
    parser.add_argument("--warmups", type=int, default=2)
    parser.add_argument("--json-out", default=None)
    args = parser.parse_args(argv)

    if args.samples < 30:
        parser.error("--samples must be at least 30 for the p95 gate")
    if args.warmups < 0:
        parser.error("--warmups must be >= 0")
    return asyncio.run(_run(args))


if __name__ == "__main__":
    sys.exit(main())
