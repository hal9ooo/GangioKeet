"""Reproducible local STT comparison harness.

One candidate runs per process.  The harness never installs packages, never
downloads a corpus, and never fabricates a benchmark: manifest audio and
references must be supplied by the user.  Optional model runtimes are imported
only when an adapter is loaded.
"""

from __future__ import annotations

import argparse
import importlib.metadata
import json
import math
import platform
import resource
import subprocess
import sys
import time
import wave
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
ROOT = SCRIPT_DIR.parent
for _entry in (SCRIPT_DIR, ROOT):
    if str(_entry) not in sys.path:
        sys.path.insert(0, str(_entry))

from stt_eval_adapters import (  # noqa: E402
    AdapterError,
    CANDIDATE_IDS,
    create_adapter,
)
from stt_eval_metrics import (  # noqa: E402
    aggregate_scores,
    score_transcripts,
)

ALLOWED_CATEGORIES = (
    "it_general",
    "it_technical",
    "identifiers",
    "hesitations_self_corrections",
    "noise_weak_voice",
    "silence_noise",
)
REQUIRED_FIELDS = (
    "id",
    "audio_path",
    "reference_raw",
    "reference_final",
    "category",
    "profile",
    "technical_terms",
    "is_silence",
)
EXPECTED_SAMPLE_RATE = 16_000
EXPECTED_SAMPLE_WIDTH = 2  # PCM16
EXPECTED_CHANNELS = 1


class ManifestError(ValueError):
    """Invalid or missing evaluation manifest."""


def _verify_wav(path: Path) -> None:
    try:
        with wave.open(str(path), "rb") as handle:
            channels = handle.getnchannels()
            sample_width = handle.getsampwidth()
            sample_rate = handle.getframerate()
            frames = handle.getnframes()
            compression = handle.getcomptype()
    except (wave.Error, EOFError, OSError) as exc:
        raise ManifestError(
            f"audio file is not a readable WAV: {path}: {exc}"
        ) from exc

    if compression != "NONE":
        raise ManifestError(
            f"audio file must contain uncompressed PCM, found "
            f"{compression!r}: {path}"
        )
    if sample_width != EXPECTED_SAMPLE_WIDTH:
        raise ManifestError(
            f"audio file must be PCM16 ({EXPECTED_SAMPLE_WIDTH} bytes per "
            f"sample), found {sample_width}: {path}"
        )
    if channels != EXPECTED_CHANNELS:
        raise ManifestError(
            f"audio file must be mono ({EXPECTED_CHANNELS} channel), found "
            f"{channels}: {path}"
        )
    if sample_rate != EXPECTED_SAMPLE_RATE:
        raise ManifestError(
            f"audio file must be 16000 Hz, found {sample_rate}: {path}"
        )
    if frames <= 0:
        raise ManifestError(f"audio file must contain frames: {path}")


def _require_string(entry: dict, field: str, line: int) -> str:
    value = entry[field]
    if not isinstance(value, str) or not value.strip():
        raise ManifestError(
            f"line {line}: field '{field}' must be a non-empty string"
        )
    return value


def _validate_entry(entry, *, line: int, base: Path, require_audio: bool):
    if not isinstance(entry, dict):
        raise ManifestError(f"line {line}: manifest entry must be a JSON object")

    missing = [field for field in REQUIRED_FIELDS if field not in entry]
    if missing:
        raise ManifestError(
            f"line {line}: missing required field(s): {', '.join(missing)}"
        )

    clip_id = _require_string(entry, "id", line)
    audio_value = _require_string(entry, "audio_path", line)

    if not isinstance(entry["reference_raw"], str):
        raise ManifestError(f"line {line}: 'reference_raw' must be a string")
    if not isinstance(entry["reference_final"], str):
        raise ManifestError(f"line {line}: 'reference_final' must be a string")

    category = entry["category"]
    if category not in ALLOWED_CATEGORIES:
        raise ManifestError(
            f"line {line}: unknown category {category!r}; allowed categories: "
            f"{', '.join(ALLOWED_CATEGORIES)}"
        )

    profile = _require_string(entry, "profile", line)

    terms = entry["technical_terms"]
    if not isinstance(terms, list) or not all(
        isinstance(term, str) for term in terms
    ):
        raise ManifestError(
            f"line {line}: 'technical_terms' must be a list of strings"
        )

    is_silence = entry["is_silence"]
    if not isinstance(is_silence, bool):
        raise ManifestError(f"line {line}: 'is_silence' must be a boolean")

    if is_silence and category != "silence_noise":
        raise ManifestError(
            f"line {line}: is_silence=true requires category "
            "'silence_noise'"
        )
    if category == "silence_noise" and not is_silence:
        raise ManifestError(
            f"line {line}: category 'silence_noise' requires "
            "is_silence=true"
        )
    if is_silence and (
        entry["reference_raw"].strip() or entry["reference_final"].strip()
    ):
        raise ManifestError(
            f"line {line}: silence clip '{clip_id}' must have empty "
            "reference_raw and reference_final"
        )

    audio_path = Path(audio_value)
    if audio_path.is_absolute():
        raise ManifestError(
            f"line {line}: audio_path must be relative to the manifest, "
            f"found absolute path: {audio_value}"
        )

    base_resolved = base.resolve()
    resolved = (base / audio_path).resolve()
    if not resolved.is_relative_to(base_resolved):
        raise ManifestError(
            f"line {line}: audio_path escapes the manifest directory: "
            f"{audio_value}"
        )

    if require_audio:
        if not resolved.is_file():
            raise ManifestError(
                f"line {line}: audio file not found for clip '{clip_id}': "
                f"{resolved}"
            )
        _verify_wav(resolved)

    return {
        "id": clip_id,
        "audio_path": resolved,
        "reference_raw": entry["reference_raw"],
        "reference_final": entry["reference_final"],
        "category": category,
        "profile": profile,
        "technical_terms": list(terms),
        "is_silence": is_silence,
    }


def load_manifest(
    path: Path,
    *,
    require_audio: bool = True,
) -> list[dict]:
    """Load and validate a JSONL manifest.

    Paths are resolved relative to the manifest directory.  With
    ``require_audio=False`` (used only for the committed schema example),
    existence and WAV format checks are skipped.
    """

    path = Path(path)
    if not path.is_file():
        raise ManifestError(f"manifest not found: {path}")

    clips: list[dict] = []
    seen_ids: dict[str, int] = {}
    base = path.parent

    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise ManifestError(f"cannot read manifest {path}: {exc}") from exc

    for line_number, line in enumerate(lines, start=1):
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError as exc:
            raise ManifestError(
                f"{path}:{line_number}: malformed JSON: {exc.msg}"
            ) from exc

        clip = _validate_entry(
            entry,
            line=line_number,
            base=base,
            require_audio=require_audio,
        )
        if clip["id"] in seen_ids:
            raise ManifestError(
                f"line {line_number}: duplicate clip id '{clip['id']}' "
                f"(first seen on line {seen_ids[clip['id']]})"
            )
        seen_ids[clip["id"]] = line_number
        clips.append(clip)

    if not clips:
        raise ManifestError(f"manifest contains no clips: {path}")
    return clips


def _package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def _sysctl(name: str) -> str:
    try:
        return subprocess.check_output(
            ["sysctl", "-n", name], text=True
        ).strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def _environment() -> dict:
    ram_bytes = _sysctl("hw.memsize")
    ram_gb = int(ram_bytes) / (1024 ** 3) if ram_bytes.isdigit() else 0.0
    return {
        "platform": platform.platform(),
        "machine": platform.machine(),
        "chip": _sysctl("machdep.cpu.brand_string"),
        "ram_gb": round(ram_gb, 1),
        "python": platform.python_version(),
        "packages": {
            "mlx": _package_version("mlx"),
            "mlx-lm": _package_version("mlx-lm"),
            "parakeet-mlx": _package_version("parakeet-mlx"),
        },
    }


def _peak_rss_mb() -> float:
    peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    if sys.platform != "darwin":
        peak *= 1024
    return peak / (1024 * 1024)


def _percentile(values: list[float], percentile: int) -> float:
    ordered = sorted(values)
    index = math.ceil(percentile / 100 * len(ordered)) - 1
    return ordered[max(0, min(index, len(ordered) - 1))]


def _latency_summary(values: list[float]) -> dict:
    if not values:
        return {"count": 0, "p50": None, "p95": None, "max": None}
    return {
        "count": len(values),
        "p50": round(_percentile(values, 50), 3),
        "p95": round(_percentile(values, 95), 3),
        "max": round(max(values), 3),
    }


def _read_pcm(path: Path) -> tuple[bytes, float]:
    with wave.open(str(path), "rb") as handle:
        frames = handle.getnframes()
        sample_rate = handle.getframerate()
        pcm = handle.readframes(frames)
    return pcm, frames / sample_rate


def _write_report(output: Path, result: dict) -> None:
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def run_candidate(
    candidate_id: str,
    manifest: Path,
    output: Path,
    warmups: int,
    repeats: int,
    *,
    adapter_factory=None,
    require_audio: bool = True,
) -> dict:
    """Run one candidate against one manifest in this process.

    ``adapter_factory`` is a test/automation seam.  The CLI never supplies it,
    so real runs use the registry and the strict runtime checks.
    """

    if warmups < 0:
        raise ValueError("warmups must be >= 0")
    if repeats < 1:
        raise ValueError("repeats must be >= 1")

    manifest = Path(manifest)
    clips = load_manifest(manifest, require_audio=require_audio)

    if adapter_factory is None:
        adapter = create_adapter(candidate_id)
    else:
        adapter = adapter_factory(candidate_id)

    load_started = time.perf_counter()
    adapter.load()
    load_seconds = time.perf_counter() - load_started

    finalizer = getattr(adapter, "finalize", None)
    has_finalizer = callable(finalizer)

    clip_results: list[dict] = []
    measured_started = time.perf_counter()

    for clip in clips:
        pcm, duration_seconds = _read_pcm(clip["audio_path"])

        # Warm-ups are deliberately unmeasured and never enter the report.
        for _ in range(warmups):
            adapter.transcribe(pcm)

        raw_hypotheses: list[str] = []
        final_hypotheses: list[str] = []
        latencies_ms: list[float] = []

        for _ in range(repeats):
            started = time.perf_counter()
            raw_text = adapter.transcribe(pcm)
            latencies_ms.append((time.perf_counter() - started) * 1000.0)
            raw_hypotheses.append(raw_text)
            if has_finalizer:
                final_hypotheses.append(finalizer(raw_text, clip["profile"]))

        raw_hypothesis = raw_hypotheses[0]
        deterministic = all(
            hypothesis == raw_hypothesis
            for hypothesis in raw_hypotheses
        )
        raw_score = score_transcripts(
            clip["reference_raw"], raw_hypothesis
        )

        final_result = None
        if has_finalizer:
            final_hypothesis = final_hypotheses[0]
            final_result = {
                "hypothesis": final_hypothesis,
                "hypotheses": final_hypotheses,
                "score": score_transcripts(
                    clip["reference_final"], final_hypothesis
                ),
                "deterministic": all(
                    hypothesis == final_hypothesis
                    for hypothesis in final_hypotheses
                ),
            }

        clip_results.append(
            {
                "id": clip["id"],
                "category": clip["category"],
                "profile": clip["profile"],
                "technical_terms": clip["technical_terms"],
                "is_silence": clip["is_silence"],
                "audio_path": str(clip["audio_path"]),
                "duration_seconds": round(duration_seconds, 3),
                "raw_hypothesis": raw_hypothesis,
                "final_hypothesis": (
                    final_result["hypothesis"] if final_result else None
                ),
                "raw_wer": raw_score["wer"],
                "final_wer": (
                    final_result["score"]["wer"] if final_result else None
                ),
                "raw": {
                    "hypothesis": raw_hypothesis,
                    "hypotheses": raw_hypotheses,
                    "score": raw_score,
                },
                "final": final_result,
                "deterministic": deterministic,
                "text_changed": not deterministic,
                "latency_ms": [round(value, 3) for value in latencies_ms],
                "latency": _latency_summary(latencies_ms),
                "load_seconds": round(load_seconds, 3),
            }
        )

    measured_seconds = time.perf_counter() - measured_started
    peak_rss_mb = _peak_rss_mb()

    raw_per_clip = [
        {"is_silence": clip["is_silence"], "score": clip["raw"]["score"]}
        for clip in clip_results
    ]
    final_per_clip = [
        {"is_silence": clip["is_silence"], "score": clip["final"]["score"]}
        for clip in clip_results
        if clip["final"] is not None
    ]

    result = {
        "schema_version": 1,
        "candidate": {
            "id": candidate_id,
            "model_id": getattr(adapter, "model_id", None),
            "revision": getattr(adapter, "revision", None),
            "precision": getattr(adapter, "precision", None),
            "status": getattr(adapter, "status", None),
            "has_finalizer": has_finalizer,
        },
        "manifest": str(manifest.resolve()),
        "warmups": warmups,
        "repeats": repeats,
        "environment": _environment(),
        "load_seconds": round(load_seconds, 3),
        "measured_seconds": round(measured_seconds, 3),
        "peak_rss_mb": round(peak_rss_mb, 1),
        "clips": clip_results,
        "aggregate": {
            "raw": aggregate_scores(raw_per_clip),
            "final": (
                aggregate_scores(final_per_clip) if has_finalizer else None
            ),
        },
    }

    _write_report(Path(output), result)
    return result


def _format_wer(value) -> str:
    return "n/a" if value is None else f"{value:.4f}"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Run one STT candidate against a local WAV manifest. "
            "No model or corpus is downloaded automatically."
        )
    )
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--candidate")
    parser.add_argument("--warmups", type=int, default=2)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--list-candidates",
        action="store_true",
        help="print candidate ids and exit",
    )
    args = parser.parse_args(argv)

    if args.list_candidates:
        print("\n".join(CANDIDATE_IDS))
        return 0
    if args.manifest is None or args.candidate is None or args.output is None:
        parser.error(
            "the following arguments are required: "
            "--manifest, --candidate, --output"
        )
    if args.warmups < 0:
        parser.error("--warmups must be >= 0")
    if args.repeats < 1:
        parser.error("--repeats must be >= 1")

    try:
        result = run_candidate(
            args.candidate,
            args.manifest,
            args.output,
            args.warmups,
            args.repeats,
        )
    except (ManifestError, AdapterError, ValueError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    raw = result["aggregate"]["raw"]
    final = result["aggregate"]["final"]
    print(
        f"candidate: {result['candidate']['id']} "
        f"({result['candidate']['model_id']})"
    )
    print(
        f"raw WER: {_format_wer(raw['wer'])} "
        f"({raw['errors']}/{raw['reference_words']} words, "
        f"{raw['scored_clips']} clips)"
    )
    if final is not None:
        print(
            f"final WER: {_format_wer(final['wer'])} "
            f"({final['errors']}/{final['reference_words']} words)"
        )
    else:
        print("final WER: n/a (no finalizer)")
    print(
        f"silence false positives: {raw['silence_false_positives']}/"
        f"{raw['silence_clips']}"
    )
    print(f"report: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
