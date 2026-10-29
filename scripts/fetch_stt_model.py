"""Download the sherpa-onnx Parakeet v3 int8 model used by the ONNX backend.

    python scripts/fetch_stt_model.py            # -> models/sherpa-onnx-...
    python scripts/fetch_stt_model.py --dest D:  # custom destination

Idempotent: skips the download when every required file is already present.
"""

import argparse
import hashlib
import sys
import tarfile
import urllib.request
from pathlib import Path

NAME = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8"
URL = (
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/"
    f"{NAME}.tar.bz2"
)
# SHA-256 of the pinned GitHub release asset. Update this together with URL.
SHA256 = "5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf"
REQUIRED = (
    "encoder.int8.onnx",
    "decoder.int8.onnx",
    "joiner.int8.onnx",
    "tokens.txt",
)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _verify_archive(path: Path) -> None:
    actual = _sha256(path)
    if actual != SHA256:
        raise ValueError(
            f"archive hash mismatch for {path}: expected {SHA256}, got {actual}"
        )


def _download(url: str, target: Path) -> None:
    partial = target.with_suffix(target.suffix + ".part")
    print(f"downloading {url}")
    with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as out:
        total = int(response.headers.get("Content-Length") or 0)
        done = 0
        while True:
            chunk = response.read(1 << 20)
            if not chunk:
                break
            out.write(chunk)
            done += len(chunk)
            if total:
                pct = 100.0 * done / total
                print(
                    f"\r{done / 1e6:7.1f} / {total / 1e6:.1f} MB ({pct:5.1f}%)",
                    end="",
                    flush=True,
                )
    print()
    try:
        _verify_archive(partial)
    except ValueError:
        partial.unlink(missing_ok=True)
        raise
    partial.replace(target)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--dest",
        default="models",
        help="directory that will contain the extracted model (default: models)",
    )
    args = parser.parse_args(argv)

    dest = Path(args.dest).expanduser().resolve()
    dest.mkdir(parents=True, exist_ok=True)
    model_dir = dest / NAME

    if all((model_dir / name).is_file() for name in REQUIRED):
        print(f"model already present: {model_dir}")
        return 0

    archive = dest / f"{NAME}.tar.bz2"
    if not archive.is_file():
        _download(URL, archive)
    _verify_archive(archive)

    print(f"extracting {archive.name}")
    with tarfile.open(archive, "r:bz2") as tar:
        tar.extractall(dest, filter="data")

    missing = [name for name in REQUIRED if not (model_dir / name).is_file()]
    if missing:
        print(
            f"error: {model_dir} is missing {missing}; "
            f"contents: {sorted(p.name for p in model_dir.glob('*'))}",
            file=sys.stderr,
        )
        return 1

    print(f"ready: {model_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
