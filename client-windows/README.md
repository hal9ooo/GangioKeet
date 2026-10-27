# GangioKeet — Windows Client

Windows push-to-talk dictation client. It sends audio to the GangioKeet server
on a Mac (Apple Silicon) and pastes the text into the foreground app. Caps Lock
push-to-talk (Wispr Flow style) or F8, transparent REST fallback,
clipboard-preserving paste, and explicit recovery of the last failed dictation.

- Target: **Windows 11** (Windows 10 not certified).
- Stack: C# / .NET 10, WPF, NAudio 3.3, YamlDotNet 17, Hardcodet.NotifyIcon.Wpf 2.
- Server: primary WebSocket `ws://<MAC_IP>:8000/ws/dictate`, REST fallback
  `POST http://<MAC_IP>:8000/v1/audio/transcriptions`. With
  `server.scheme: https` the client uses `wss://` and `https://`. Several
  endpoints can be listed under `endpoints:`: the client probes them and uses
  the fastest one (see **Endpoints and settings**).

## Quick start

1. Install the .NET 10 SDK.
2. Copy `client_config.example.yaml` to
   `%APPDATA%\GangioKeet\client_config.yaml` and set `server.host` to the Mac's
   IP (or list more than one under `endpoints:`). If the server is behind a TLS
   terminator, also set `server.scheme: https`.
3. If the server requires a token, set the environment variable named by
   `server.api_key_env` (recommended: `GANGIOKEET_API_KEY`). The token must
   **never** be written to the YAML file.
4. Build and run:

```powershell
dotnet build client-windows/GangioKeet.Client.sln
dotnet run --project client-windows/src/GangioKeet.Client
```

The app starts in the tray (no main window). **Hold Caps Lock** to dictate and
release to transcribe; double-tap for hands-free.

## Configuration

Strict schema: unknown keys are rejected. The file lives in
`%APPDATA%\GangioKeet\client_config.yaml`; an explicit path can be passed with
`--config`. All defaults are in `client_config.example.yaml`.

| Section | Main entries |
|---|---|
| `server` | host, scheme (`http` default / `https`), port, transport (`websocket`/`rest`), connect/response/upload timeouts, rest_fallback, health interval 30 s and timeout 2 s, switch margin 25, api_key_env. host/scheme/port apply only when `endpoints` is absent |
| `endpoints` | optional named endpoints: `name`, `role` (at most one `primary`), `host`, `scheme`, `port`; unique names and host:port |
| `hotkey` | mode (`push_to_talk`/`toggle`), key (`capslock` default, f1–f24, mouse_x1), abort_key (escape), double_tap_max_ms 250, double_tap_gap_ms 350 |
| `audio` | input_device (WASAPI endpoint ID or `null` for the default), sample_rate 16000, channels 1, chunk_duration_ms 100, ready_timeout_seconds 3 |
| `paste` | default_combo `ctrl+v`, terminal_combo `ctrl+shift+v`, terminal_apps, clipboard_restore_delay_ms 200, refocus_before_paste |
| `ui` | show_floating_pill, pill_position (`center_bottom` default / `cursor`), sound_feedback |
| `context_profiles` | exe → profile mappings (`code`, `terminal`, `chat`, `email`, `document`) |
| `browser_profiles` | browsers and page-title rules, priority email → chat → code → document |
| `recovery` | retention_seconds 600 |

## Endpoints and settings

- List several STT endpoints under `endpoints:` (for example the direct LAN IP
  at home and Tailscale on the move). The legacy `server.host`/`scheme`/`port`
  keep working: when `endpoints:` is absent they become a single endpoint
  named "Server".
- Every 30 s (`server.health_check_interval_seconds`) each endpoint gets a
  lightweight `/health` probe with a 2 s timeout
  (`server.health_check_timeout_seconds`). The active endpoint is the one with
  the lowest **median of the last 5 successful RTTs**; ties within 1 ms go to
  the primary.
- Hysteresis (`server.switch_margin_percent`, 25) means a challenger must be at
  least 25% faster than the current endpoint to take over, so near-equal
  endpoints do not flap. If every endpoint is offline, the active one stays
  put.
- The endpoint is **pinned for the whole dictation**: a switch decided while a
  turn is running applies from the next one, never mid-sentence.
- RTT history is keyed by `scheme://host:port`: renaming an endpoint keeps its
  history, changing host or port resets it.
- **Settings** opens from the tray menu ("Impostazioni…") or by double-clicking
  the tray icon. Each row has name, role, host, port, scheme, a live
  green/yellow/red dot, median RTT, `/ready` state and the last error; buttons
  add and remove rows.
- **Verifica adesso** probes the rows as typed, including unsaved edits: it
  does not change the active endpoint and writes nothing; the results stay
  visible while the window is open.
- **Salva** validates names, hosts, ports and duplicates (name or host:port;
  inline error, save refused), writes only the `endpoints:` block, and applies
  immediately: the monitor re-probes at once, no restart. Promoting a row to
  `primary` demotes the previous one.
- Read-only telemetry: last dictation duration, handshake time, OK/error/retry
  counters, last endpoint and last error.

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
3. **Transcription**: WebSocket with one sender and one receiver. If the WS
   upgrade is unavailable (404/405/426/200 without upgrade) and no bytes have
   been sent yet, recording continues locally and is sent via REST on stop.
   After `start`/audio has been sent there is no automatic fallback: use the
   explicit recovery flow.
4. **Paste**: the text is put on the clipboard, the target window is checked
   again, Ctrl+V (or Ctrl+Shift+V in configured terminals) is sent, and the
   clipboard is restored only if nobody copied something else in the meantime.
   If paste fails, the text remains recoverable.
5. **Recovery**: one slot in RAM, 600 s expiry. From the tray: retry the last
   dictation (always REST, new request), copy the last text, or discard the
   recovery.

## Limits and security

- **No on-disk history**: audio and transcripts live only in RAM. Daily logs in
  `%LOCALAPPDATA%\GangioKeet\logs\` contain only codes and diagnostic messages,
  never audio, dictated text, window titles, or keys.
- **Optional TLS**: `server.scheme: https` uses `https://` and `wss://`. The
  server does not terminate TLS: use a reverse proxy or a tunnel. With `http`
  (default), tokens, audio, and window titles travel in clear text.
- **No automatic send**: the client never presses Enter and never sends.
- **UIPI**: if the target window is elevated (or its state cannot be verified),
  injection is blocked and the text is not pasted. Paste never attempts
  elevation or bypasses.
- **Clipboard**: text and DIB images are preserved. HTML/RTF/files are not
  preserved. If the target app takes longer than the configured delay to read
  the clipboard, the text may be lost (no delayed rendering).
- **No automatic Ctrl+C**: the selection is never copied or read.
- **REST fallback ≠ offline mode**: an unreachable host produces an immediate
  error, not a promise of deferred transcription.

## Tests

Logical suite (fakes, no hardware):

```powershell
dotnet test client-windows/GangioKeet.Client.sln --filter "Category!=Integration"
```

Gated tests, run only when the resources are available:

| Variable | Covers |
|---|---|
| `RUN_DESKTOP_TESTS=1` | real clipboard, foreground window, audio device, hooks, autostart |
| `RUN_REAL_SERVER=1` | /health roundtrip, WS PCM, REST PCM, abort against the Mac (`GANGIOKEET_HOST`, default `<MAC_IP>:8000`) |

```powershell
$env:RUN_DESKTOP_TESTS="1"
dotnet test client-windows/GangioKeet.Client.sln --filter "Category=Integration"
```

## Microphone

- The tray has a **Microphone** submenu: "Windows Default" or one of the active
  input devices. The choice applies from the next dictation, is saved to
  `client_config.yaml`, and survives a restart.
- `audio.input_device: null` uses the Windows default microphone, resolved on
  every recording.
- An explicit ID that does not exist is an error: no silent microphone switch.
- If Windows denies microphone access, the client reports
  `microphone_permission_denied` and the tray offers "Open microphone settings"
  (`ms-settings:privacy-microphone`).
- Device disconnected mid-recording: recording stops, already-captured audio
  remains recoverable, and there is no automatic switch to another mic.

## Pill

- Compact (136×28), non-activatable, bottom center of the monitor under the
  pointer, 24 px above the work area (taskbar included in the calculation).
- `ui.pill_position: "cursor"` shows it near the pointer.
- On monitors with different DPI or negative origins, the position is clamped
  to the current monitor's work area.
- While recording, the status text is replaced by a 20-bar quantized waveform:
  samples every 50 ms, fast attack and gradual release, flat after about
  400 ms of silence. It reads the level the capture already computes, so it
  never touches the audio path, and the pill does not resize. The text comes
  back as soon as the turn ends.

## Splash

- At startup a 640×344 cover appears centered, fades/scales in, and closes
  after 3 s. It is not activatable, does not show in the taskbar, and does not
  delay startup: the tray icon is already up while it is visible.
- If the configuration is invalid, the splash closes before the error dialog.

## Publishing

```powershell
dotnet publish client-windows/src/GangioKeet.Client/GangioKeet.Client.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output contains `GangioKeet.Client.exe` plus native WPF/NAudio assets
(`D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`,
`vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`): it is not a self-contained single
file.

## Structure

```
client-windows/
  src/GangioKeet.Client/
    Audio/            WASAPI capture, chunker, buffer, levels
    Configuration/    strict YAML schema incl. endpoints, credential from environment
    Context/          profiles, foreground window, UIPI
    Controller/       dictation orchestration (generation ID, recovery)
    Hotkey/           PTT/double-tap/toggle gestures, global hooks, suppression
    Paste/            clipboard snapshot, SendInput, paste transaction
    Platform/         single-instance, autostart, logger, reload, privacy
    Protocol/         wire DTOs, WS transport, session, REST, probe, selection, monitor
    Recovery/         single slot with expiry
    Ui/               pill + waveform, splash, settings window, tray
  tests/GangioKeet.Client.Tests/
```

## References

The wire contract and profiles are described in this README and in the
`Protocol/` code; internal project notes are not included in the public
repository.

## License

MIT — see [../LICENSE](../LICENSE).
