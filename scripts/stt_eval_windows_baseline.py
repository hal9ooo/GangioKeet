"""Measure the existing sherpa-onnx CPU engine without changing production."""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys
import time
import wave

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "scripts"))

from stt_eval_manifest import load_manifest
from stt_eval_metrics import aggregate_scores
from stt_eval_punctuation import score_record, aggregate_punctuation


def run_baseline(model_dir: Path, manifest: Path, output: Path, *, stt_factory=None) -> None:
    if stt_factory is None:
        from dictation_server.stt_onnx import OnnxParakeetSTT
        stt_factory = OnnxParakeetSTT
    model_dir = Path(model_dir).resolve()
    files = {}
    for file in sorted(model_dir.iterdir()):
        if file.is_file() and (file.suffix == ".onnx" or file.name == "tokens.txt"):
            with file.open("rb") as stream:
                files[file.name] = hashlib.file_digest(stream, "sha256").hexdigest()
    if not files:
        raise ValueError("Baseline directory has no model files.")
    bundle = hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()
    engine_id = {"model_id": "sherpa-onnx:" + model_dir.name, "revision": "local-bundle:" + bundle,
                 "artifact_sha256": bundle, "runtime_version": importlib.metadata.version("sherpa-onnx"),
                 "backend": "cpu", "precision": "int8", "model_files": files}
    clips = load_manifest(manifest)
    engine = stt_factory(model_dir)
    started = time.perf_counter()
    engine.load()
    load_ms = (time.perf_counter() - started) * 1000
    results = []
    for index, clip in enumerate(clips):
        with wave.open(str(clip["audio_path"]), "rb") as audio:
            duration = audio.getnframes() / audio.getframerate()
            pcm = audio.readframes(audio.getnframes())
        if index == 0:
            engine.transcribe(pcm)  # one unmeasured warm-up, excluded below
        started = time.perf_counter()
        text = engine.transcribe(pcm)
        elapsed = (time.perf_counter() - started) * 1000
        score = score_record(clip, text)
        results.append({"id": clip["id"], "is_silence": clip["is_silence"], "duration_seconds": duration,
                        "latency_ms": [elapsed], "raw": {"hypothesis": text, "score": score["raw"],
                        "punctuation": score["punctuation"]}})
    report = {"schema_version": 1, "phase": "asr", "candidate": "parakeet_onnx_cpu",
              "status": "completed", "engine": engine_id, "load_ms": load_ms, "clips": results,
              "aggregate": {"raw": aggregate_scores([{"score": row["raw"]["score"], "is_silence": row["is_silence"]}
                  for row in results if row["raw"]["score"] is not None]),
                  "punctuation": aggregate_punctuation([row["raw"]["punctuation"] for row in results])}}
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        run_baseline(args.model_dir, args.manifest, args.output)
    except Exception as error:
        print(f"Baseline unavailable: {type(error).__name__}: {error}", file=sys.stderr)
        return 2
    print(f"Report: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
