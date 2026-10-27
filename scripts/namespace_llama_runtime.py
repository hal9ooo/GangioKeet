"""Build a private, equal-length DLL namespace; never alter the NuGet cache.

ggml exported function names contain underscores and remain byte-identical.
Only dependency/module filenames and dynamic backend filename prefixes change.
Original and modified bytes must match the committed runtime lock.
"""
import argparse
import hashlib
import json
from pathlib import Path


def namespace_bytes(data: bytes) -> bytes:
    # ModuleRef names are ASCII; managed loader literals are UTF-16 in #US.
    # Preserve all exported/PInvoke entrypoint names (ggml_... with underscore).
    return (data.replace(b"ggml-", b"lgml-").replace(b"ggml.dll", b"lgml.dll")
            .replace(b"ggml\0", b"lgml\0")
            .replace("ggml-".encode("utf-16-le"), "lgml-".encode("utf-16-le"))
            .replace("ggml".encode("utf-16-le") + b"\0", "lgml".encode("utf-16-le") + b"\0"))


def prepare(source: Path, destination: Path, lock_path: Path) -> None:
    runtime = next(x for x in json.loads(lock_path.read_text(encoding="utf-8-sig"))["runtimes"]
                   if x["id"] == "llama-vulkan")
    destination.mkdir(parents=True, exist_ok=True)
    for item in runtime["files"]:
        data = (source / item["source_path"]).read_bytes()
        if hashlib.sha256(data).hexdigest() != item["source_sha256"]:
            raise ValueError(f"Source hash mismatch: {item['source_path']}")
        patched = namespace_bytes(data)
        if hashlib.sha256(patched).hexdigest() != item["sha256"]:
            raise ValueError(f"Patched hash mismatch: {item['path']}")
        target = destination / item["path"]
        if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == item["sha256"]:
            continue  # An existing verified DLL may be mapped by a development probe.
        target.write_bytes(patched)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--source", type=Path, required=True)
    p.add_argument("--destination", type=Path, required=True)
    p.add_argument("--lock", type=Path, required=True)
    a = p.parse_args()
    prepare(a.source, a.destination, a.lock)
