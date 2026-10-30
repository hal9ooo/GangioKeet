# GangioKeet — Windows Client

Windows push-to-talk dictation client with local ASR and local LLM cleanup. It
captures WASAPI audio, transcribes and cleans it in-process, and pastes the text
into the foreground app. Caps Lock push-to-talk (Wispr Flow style) or F8,
clipboard-preserving paste, explicit recovery of a failed dictation, an
always-copyable last result, and automatic media pause/resume while dictating.

- Target: **Windows 11** (Windows 10 not certified).
- Stack: C# / .NET 10, WPF, NAudio 3.3, YamlDotNet 18.1, Hardcodet.NotifyIcon.Wpf 2.
- Target framework: `net10.0-windows10.0.19041.0` (GSMTC/WinRT projections).
- No HTTP server and no Python at runtime.

## Quick start

1. Install the .NET 10 SDK.
2. Copy `client_config.example.yaml` to
   `%APPDATA%\GangioKeet\client_config.yaml`.
3. Build and run:

```powershell
dotnet build client-windows/GangioKeet.Client.sln
dotnet run --project client-windows/src/GangioKeet.Client
```

The app starts in the tray (no main window). **Hold Caps Lock** to dictate and
release to transcribe; double-tap for hands-free. The first run reads the model
selection and provisions missing artifacts on demand.

A source checkout does not contain the installed `native/` runtime or the
selected model bytes. The packaged installer supplies that layout; for a
source-only run, set `GANGIOKEET_TRANSCRIBE_DIR`, `GANGIOKEET_LLAMA_DIR` and
`GANGIOKEET_RUNTIME_LOCK` (and a model root containing the selected files).

## Configuration

Strict schema: unknown keys are rejected. The file lives in
`%APPDATA%\GangioKeet\client_config.yaml`. All defaults are in
`client_config.example.yaml`.

| Section | Main entries |
|---|---|
| `inference` | `model_selection_path`, `cleanup_policy_path`, `backend` (`vulkan`/`cpu`) |
| `recording` | `enabled`, `directory` |
| `hotkey` | mode (`push_to_talk`/`toggle`), key (`capslock` default, f1–f24, mouse_x1), abort_key (escape), double_tap_max_ms 250, double_tap_gap_ms 350 |
| `audio` | input_device (WASAPI endpoint ID or `null` for the default), sample_rate 16000, channels 1, chunk_duration_ms 100, ready_timeout_seconds 3 |
| `paste` | default_combo `ctrl+v`, terminal_combo `ctrl+shift+v`, terminal_apps, clipboard_restore_delay_ms 200, refocus_before_paste |
| `ui` | show_floating_pill, pill_position (`center_bottom` default / `cursor`), sound_feedback |
| `context_profiles` | exe → profile mappings (`code`, `terminal`, `chat`, `email`, `document`) |
| `browser_profiles` | browsers and page-title rules, priority email → chat → code → document |
| `recovery` | retention_seconds 600 |

Legacy keys (`server`, `endpoints`, `inference.mode`) are removed from the
YAML representation **in memory** before deserialization. An existing file keeps
loading as a local configuration and is not rewritten; every other unknown key
is still rejected. `recording.store_context` is accepted for compatibility but
has no effect: selected text is never stored.

On first run or after a model-combination change, missing model files are
downloaded from `huggingface.co` over HTTPS. No audio, transcript, window title,
or telemetry is sent; the request exposes the client IP and the requested
model/revision.

## Model combinations

The active choice lives in `%APPDATA%\GangioKeet\selection.json`; the
installer ships a seed in `models/selection.json` and the app copies it on
first run:

```json
{"schema_version":2,"stt_id":"parakeet_v3","cleaner_id":null,"refinement_enabled":false}
```

The settings window exposes an **ASR** selector, a **Refinement** checkbox, and
a **cleaner** selector. Any ASR can be paired with any cleaner, or refinement
can be off. Changes apply hot, without restarting the client; a dictation
already being processed completes on the old engines.

`models/candidates.json` is the single source for artifact paths and hashes and
is pinned by `models/integrity.json`. Legacy `selected.json` and
`selected-parakeet-qwen.json` files are migrated on first run. Real-voice tests
favour **Parakeet + Nessuno** for fidelity/latency and **Parakeet + Gemma** for
questions and technical terms.

## Media pause

At dictation start, `WindowsMediaPlaybackPause` obtains the GSMTC session
manager, pauses every session whose playback status is `Playing`, and records
only the sessions that accepted the pause. At key release (or on abort, failure,
or disposal) it resumes only those sessions that are still `Paused`. A session
the user resumed manually is left alone; a session that refused the pause is
never played on release.

- Pause budget: 750 ms. If the manager or an app does not answer, capture starts
  anyway.
- Release during an in-flight pause: the controller marks the turn stopped,
  skips capture, and resumes immediately.
- Resume is fire-and-forget on the normal release path; disposal waits at most
  750 ms.
- No setting: the behavior is always on.

## How it works

1. **Caps Lock hotkey** (PTT, Wispr Flow style): hold and speak, release to
   transcribe. A quick double-tap enters hands-free; the next tap stops it. A
   single tap without a second tap **does nothing** (no empty dictation).
   Caps Lock stays disabled while the app is running: it does not turn on
   uppercase. F8 remains selectable with `hotkey.key: "f8"`. In `toggle` mode,
   the first keydown starts and the second stops (keyups do not stop).
2. **Audio**: WASAPI shared, conversion/resampling to PCM16 LE mono 16 kHz,
   100 ms chunks (3200 bytes). Audio is held in RAM in a bounded buffer
   (maximum 67,108,864 bytes per dictation, ~70 minutes at 16 kHz mono).
3. **Media pause**: GSMTC before capture, resume at release.
4. **Local inference**: `LocalDictationTurn` feeds the buffered PCM to the
   selected ASR engine, then optionally to the local cleaner, and publishes a
   `DictationOutcome`.
5. **Paste**: the text is put on the clipboard, the target window is checked
   again, Ctrl+V (or Ctrl+Shift+V in configured terminals) is sent, and the
   clipboard is restored only if nobody copied something else in the meantime.
   If paste fails, the text remains recoverable.

## Recovery

One failure slot in RAM, 600 s expiry, plus the last result for copy. From the
tray:

- **Riprova ultima dettatura** sends the buffered PCM to the active local
  backend again; if only the text is available it copies it. Enabled when a
  failed dictation is recoverable.
- **Copia ultimo testo** copies the recovered text, or the last successful
  result, without pasting; enabled after any dictation with text.
- **Scarta recupero** clears the failure slot; enabled only when a failure is
  present.

If the target window disappears or loses focus during paste, the result is
offered to recovery instead of being dropped. A successful paste keeps the last
text copyable for the rest of the session, so a focus change cannot lose it.

## Settings

The tray menu opens the settings window. It shows:

- the local runtime state and the active backend;
- the ASR/refinement selectors (applied immediately, no restart);
- the benchmark recording toggle and the recorded samples list;
- read-only telemetry: last dictation duration, local start time, OK/error/retry
  counters, last backend and last error.

There is no mode selector.

## Tests

```powershell
dotnet test client-windows/GangioKeet.Client.sln
```

Coverage includes the controller lifecycle, media pause/resume ordering,
configuration compatibility, hotkeys, audio conversion, paste, recording, and
hot model-combination switching. The inference tests additionally validate the prompt hash
against `prompts/conservative-local-v3.txt`.

## Packaging

```powershell
powershell -File scripts/package_windows.ps1 -Configuration Release
powershell -File scripts/verify_windows_package.ps1 -Payload artifacts/package -Selection client-windows/models/selected.json
```

The package contains the client, native runtimes, `cleanup.yaml`, and the
selection JSON. GGUF model files are provisioned on demand and do not ship in
the installer payload.

## Layout

```text
src/GangioKeet.Client/
  Audio/          WASAPI capture, resampling, bounded RAM buffer
  Configuration/  strict YAML schema, legacy-key tolerance, line-based writer
  Context/        foreground window and profile resolution
  Controller/     dictation lifecycle, local backend, turn timing
  Hotkey/         low-level hooks, gestures, Caps Lock reset
  Paste/          clipboard preservation and input simulation
  Platform/       runtime, model provisioner, GSMTC media pause, logging
  Protocol/       dictation DTOs and in-RAM telemetry
  Recovery/       last-failure slot and last-result retention
  Ui/             pill, settings, splash, tray

tests/GangioKeet.Client.Tests/
tests/GangioKeet.Inference.Tests/
```
