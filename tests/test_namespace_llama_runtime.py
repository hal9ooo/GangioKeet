import hashlib
import json
from pathlib import Path

from scripts.namespace_llama_runtime import namespace_bytes, prepare


def test_namespace_preserves_offsets_and_exported_function_names():
    original = b"MZ\0ggml-base.dll\0ggml-vulkan.dll\0ggml.dll\0ggml_backend_load\0llama.dll\0"
    patched = namespace_bytes(original)
    assert len(patched) == len(original)
    assert b"lgml-base.dll\0lgml-vulkan.dll\0lgml.dll\0" in patched
    assert b"ggml_backend_load\0llama.dll\0" in patched


def test_prepared_runtime_is_not_rewritten_while_it_can_be_loaded(tmp_path, monkeypatch):
    source = tmp_path / "source"
    dest = tmp_path / "dest"
    source.mkdir()
    dest.mkdir()
    data = b"MZ\0ggml.dll\0"
    patched = namespace_bytes(data)
    (source / "llama.dll").write_bytes(data)
    (dest / "llama.dll").write_bytes(patched)
    lock = tmp_path / "lock.json"
    lock.write_text(json.dumps({"runtimes": [{"id": "llama-vulkan", "files": [dict(
        source_path="llama.dll", source_sha256=hashlib.sha256(data).hexdigest(),
        path="llama.dll", sha256=hashlib.sha256(patched).hexdigest())]}]}))
    def deny_write(*args):
        raise PermissionError("DLL loaded by another development runner")
    monkeypatch.setattr(Path, "write_bytes", deny_write)
    prepare(source, dest, lock)


def test_managed_module_refs_and_utf16_loader_literals_use_same_namespace():
    original = b"ggml\0ggml-base\0ggml_backend_dev_count\0" + (
        "ggml\0ggml-base\0ggml-vulkan\0ggml_backend_load\0").encode("utf-16-le")
    patched = namespace_bytes(original)
    assert b"lgml\0lgml-base\0ggml_backend_dev_count\0" in patched
    assert ("lgml\0lgml-base\0lgml-vulkan\0ggml_backend_load\0").encode("utf-16-le") in patched
    assert len(patched) == len(original)
