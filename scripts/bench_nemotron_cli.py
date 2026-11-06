"""Benchmark the NeMo-Speech.cpp SALM server on a local STT manifest.

This is evaluation tooling, not part of the client runtime.  It starts the
nightly `nemo-speech serve` binary, sends one complete utterance per request,
and reports weighted WER, warm latency, RTF, cold start, and peak RSS.
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path

import psutil
import requests
import soundfile as sf

sys.path.insert(0, str(Path(__file__).resolve().parent))
from stt_eval_metrics import aggregate_scores, score_transcripts  # noqa: E402


def load_manifest(path: Path, limit: int | None) -> list[dict]:
    rows = []
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows[:limit] if limit else rows


def wait_ready(base: str, proc: subprocess.Popen, timeout: float) -> tuple[float, str]:
    started = time.perf_counter()
    deadline = started + timeout
    last_error = "no response"
    while time.perf_counter() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"server exited with code {proc.returncode}")
        try:
            response = requests.get(f"{base}/v1/models", timeout=2)
            if response.status_code == 200:
                return time.perf_counter() - started, response.text
            last_error = f"HTTP {response.status_code}: {response.text[:200]}"
        except requests.RequestException as exc:
            last_error = str(exc)
        time.sleep(1)
    raise RuntimeError(f"server not ready after {timeout:.0f}s: {last_error}")


def serve_model_id(base: str) -> str | None:
    try:
        payload = requests.get(f"{base}/v1/models", timeout=5).json()
    except (requests.RequestException, ValueError):
        return None
    data = payload.get("data") or []
    if isinstance(data, list) and data:
        return data[0].get("id")
    return None


def transcribe(base: str, wav: Path, model_id: str | None, language: str | None,
               timeout: float) -> tuple[str, float]:
    data = {"response_format": "json"}
    if model_id:
        data["model"] = model_id
    if language:
        data["language"] = language
    started = time.perf_counter()
    with wav.open("rb") as handle:
        response = requests.post(
            f"{base}/v1/audio/transcriptions",
            files={"file": (wav.name, handle, "audio/wav")},
            data=data,
            timeout=timeout,
        )
    elapsed = time.perf_counter() - started
    if response.status_code != 200:
        raise RuntimeError(f"HTTP {response.status_code}: {response.text[:400]}")
    payload = response.json()
    text = payload.get("text")
    if text is None:
        text = payload.get("transcript")
    if text is None:
        raise RuntimeError(f"no text in response: {str(payload)[:400]}")
    return str(text), elapsed


def percentile(values: list[float], q: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round((len(ordered) - 1) * q)))
    return ordered[index]


def peak_rss_watch(pid: int, stop: threading.Event, out: dict) -> None:
    try:
        process = psutil.Process(pid)
    except psutil.Error:
        return
    peak = 0
    while not stop.wait(0.5):
        try:
            total = process.memory_info().rss
            for child in process.children(recursive=True):
                try:
                    total += child.memory_info().rss
                except psutil.Error:
                    pass
            peak = max(peak, total)
            out["peak_rss_bytes"] = peak
        except psutil.Error:
            return


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--nemo-speech", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--encoder", type=Path, default=None)
    parser.add_argument("--device", default="vulkan:0")
    parser.add_argument("--language", default="it")
    parser.add_argument("--port", type=int, default=8099)
    parser.add_argument("--limit", type=int, default=None)
    parser.add_argument("--max-sequences", type=int, default=4)
    parser.add_argument("--max-context", type=int, default=8192)
    parser.add_argument("--mtp-draft", type=int, default=0)
    parser.add_argument("--output-dir", type=Path, default=Path("reports/nemotron-transcribe"))
    parser.add_argument("--ready-timeout", type=float, default=900)
    parser.add_argument("--request-timeout", type=float, default=900)
    args = parser.parse_args(argv)

    clips = load_manifest(args.manifest, args.limit)
    if not clips:
        print("empty manifest", file=sys.stderr)
        return 2

    base = f"http://127.0.0.1:{args.port}"
    command = [
        str(args.nemo_speech),
        "serve",
        "--asr-model",
        str(args.model),
        "--host",
        "127.0.0.1",
        "--port",
        str(args.port),
        "--no-ui",
        "--device",
        args.device,
        "--asr.salm.max_sequences",
        str(args.max_sequences),
        "--asr.salm.max_context",
        str(args.max_context),
        "--asr.salm.mtp_draft",
        str(args.mtp_draft),
    ]
    if args.encoder:
        command += ["--salm-encoder", str(args.encoder)]

    args.output_dir.mkdir(parents=True, exist_ok=True)
    log_path = args.output_dir / "server.log"
    log_handle = log_path.open("w", encoding="utf-8", newline="\n")
    proc = subprocess.Popen(
        command,
        stdout=log_handle,
        stderr=subprocess.STDOUT,
        cwd=str(args.nemo_speech.parent),
    )
    print(f"server pid {proc.pid}; log {log_path}", flush=True)

    stop = threading.Event()
    rss: dict = {}
    watcher = threading.Thread(target=peak_rss_watch, args=(proc.pid, stop, rss), daemon=True)
    watcher.start()

    results = []
    try:
        ready_seconds, models = wait_ready(base, proc, args.ready_timeout)
        model_id = serve_model_id(base)
        print(f"ready in {ready_seconds:.2f}s; model id {model_id!r}", flush=True)

        warm = clips[0]
        warm_path = args.manifest.parent / warm["audio_path"]
        warm_text, warm_seconds = transcribe(
            base, warm_path, model_id, args.language, args.request_timeout
        )
        print(f"warm-up {warm['id']}: {warm_seconds:.2f}s -> {warm_text[:100]!r}", flush=True)

        run_started = time.perf_counter()
        for index, clip in enumerate(clips, 1):
            wav = args.manifest.parent / clip["audio_path"]
            duration = float(sf.info(str(wav)).duration)
            try:
                text, elapsed = transcribe(
                    base, wav, model_id, args.language, args.request_timeout
                )
                score = score_transcripts(clip.get("reference_raw", ""), text)
                error = None
            except Exception as exc:  # keep the remaining clips running
                text = ""
                elapsed = None
                score = None
                error = str(exc)
            results.append(
                {
                    "id": clip["id"],
                    "audio": clip["audio_path"],
                    "duration_seconds": duration,
                    "latency_seconds": elapsed,
                    "rtf": (duration / elapsed) if elapsed else None,
                    "text": text,
                    "reference": clip.get("reference_raw", ""),
                    "score": score,
                    "error": error,
                }
            )
            status = "ok" if error is None else f"ERROR {error[:120]}"
            print(
                f"[{index}/{len(clips)}] {clip['id']} {duration:.1f}s "
                f"{elapsed if elapsed is not None else float('nan'):.2f}s "
                f"WER {score['wer'] if score and score['wer'] is not None else float('nan'):.3f} "
                f"{status}",
                flush=True,
            )
        wall_seconds = time.perf_counter() - run_started
    finally:
        stop.set()
        watcher.join(timeout=2)
        proc.terminate()
        try:
            proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            proc.kill()
        log_handle.close()

    scored = [item for item in results if item["score"] is not None]
    aggregate = aggregate_scores(scored)
    latencies = [item["latency_seconds"] for item in scored if item["latency_seconds"] is not None]
    summary = {
        "ok": len(scored) == len(results) and bool(scored),
        "clips": len(results),
        "errors": sum(1 for item in results if item["error"]),
        "model": str(args.model),
        "encoder": str(args.encoder) if args.encoder else None,
        "device": args.device,
        "language": args.language,
        "cold_start_seconds": round(ready_seconds, 3),
        "warmup_seconds": round(warm_seconds, 3),
        "wall_seconds": round(wall_seconds, 3),
        "wer": aggregate["wer"],
        "substitutions": aggregate["substitutions"],
        "deletions": aggregate["deletions"],
        "insertions": aggregate["insertions"],
        "reference_words": aggregate["reference_words"],
        "latency_p50_seconds": percentile(latencies, 0.50),
        "latency_p95_seconds": percentile(latencies, 0.95),
        "latency_max_seconds": max(latencies) if latencies else None,
        "latency_mean_seconds": statistics.fmean(latencies) if latencies else None,
        "audio_seconds": sum(item["duration_seconds"] for item in results),
        "rtf_mean": (
            sum(item["duration_seconds"] for item in results) / sum(latencies)
            if latencies and sum(latencies) > 0
            else None
        ),
        "peak_rss_bytes": rss.get("peak_rss_bytes"),
        "aggregate": aggregate,
        "server_log": str(log_path),
    }
    (args.output_dir / "results.json").write_text(
        json.dumps({"summary": summary, "clips": results}, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )

    lines = [
        "# Nemotron 3.5 Transcribe — benchmark CLI",
        "",
        f"- Modello: `{args.model}`",
        f"- Device: `{args.device}`, lingua: `{args.language}`",
        f"- Clip: {summary['clips']} ({summary['audio_seconds']:.1f} s di audio), errori: {summary['errors']}",
        f"- Cold start: {summary['cold_start_seconds']:.2f} s; warm-up: {summary['warmup_seconds']:.2f} s",
        f"- WER: {summary['wer']:.4f} ({summary['substitutions']} S, {summary['deletions']} D, {summary['insertions']} I su {summary['reference_words']} parole)",
        f"- Latenza calda: p50 {summary['latency_p50_seconds']:.3f} s, p95 {summary['latency_p95_seconds']:.3f} s, max {summary['latency_max_seconds']:.3f} s",
        f"- RTF medio: {summary['rtf_mean']:.2f}x",
        f"- Picco RSS: {(summary['peak_rss_bytes'] or 0) / 1e9:.2f} GB",
        "",
        "| Clip | Durata | Latenza | WER |",
        "|---|---:|---:|---:|",
    ]
    for item in results:
        wer = item["score"]["wer"] if item["score"] and item["score"]["wer"] is not None else None
        lines.append(
            f"| {item['id']} | {item['duration_seconds']:.1f}s | "
            f"{item['latency_seconds']:.2f}s | {wer:.3f} |"
            if item["latency_seconds"] is not None and wer is not None
            else f"| {item['id']} | {item['duration_seconds']:.1f}s | errore | - |"
        )
    (args.output_dir / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2), flush=True)
    return 0 if summary["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
