import json
import sys
import wave
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))


def test_baseline_preserves_clip_ids_and_model_identity(tmp_path):
    import stt_eval_windows_baseline

    model = tmp_path / "model"
    model.mkdir()
    (model / "encoder.int8.onnx").write_bytes(b"model")
    rows = []
    for clip_id in ("first", "second"):
        audio = tmp_path / f"{clip_id}.wav"
        with wave.open(str(audio), "wb") as handle:
            handle.setnchannels(1)
            handle.setsampwidth(2)
            handle.setframerate(16000)
            handle.writeframes(b"\x00\x00" * 100)
        rows.append({"id": clip_id, "audio_path": audio.name, "reference_raw": "ciao",
                     "reference_final": "Ciao.", "category": "it_general", "profile": "default",
                     "technical_terms": [], "is_silence": False})
    manifest = tmp_path / "manifest.jsonl"
    manifest.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")

    class Engine:
        def __init__(self, directory):
            assert directory == model
        def load(self):
            pass
        def transcribe(self, pcm):
            return "Ciao."

    output = tmp_path / "baseline.json"
    stt_eval_windows_baseline.run_baseline(model, manifest, output, stt_factory=Engine)
    report = json.loads(output.read_text(encoding="utf-8"))
    assert [clip["id"] for clip in report["clips"]] == ["first", "second"]
    assert report["engine"]["backend"] == "cpu"
    assert report["engine"]["model_id"] == "sherpa-onnx:model"
    assert report["clips"][0]["raw"]["hypothesis"] == "Ciao."
    assert report["clips"][0]["raw"]["score"]["wer"] == 0
