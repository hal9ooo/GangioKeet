# Security Policy

## Supported versions

The project is pre-1.0. Security fixes are applied to the `main` branch.

## Reporting a vulnerability

Do not open a public issue. Use GitHub's private vulnerability reporting
("Report a vulnerability" in the **Security** tab) when available, or contact
the maintainer through GitHub.

Include:

- affected version or commit;
- platform (Windows);
- reproduction steps;
- expected impact.

Do not include keys, tokens, real audio, or personal transcripts. If you need
an audio sample, use a synthetic fixture.

## Scope

Covered: the Windows client (WPF/.NET) and the Windows build, evaluation, and
packaging scripts. This includes local microphone capture, the local ASR/LLM
pipeline, clipboard/paste handling, the media pause service, and the per-user
configuration loader.

Not covered: model weights and third-party dependencies, which should be
reported to their respective projects.

## Native runtimes

The native inference runtimes are pinned by
`client-windows/native/runtime-lock.json`; model files are pinned by the
selection JSON. When updating either, change version, commit and SHA-256 values
together, then run `scripts/build_native_windows.ps1` and
`scripts/verify_windows_package.ps1`. Review upstream advisories for
transcribe.cpp, llama.cpp/ggml and LLamaSharp before bumping.

## Data handling

- The runtime opens no HTTP listener and sends no audio, transcripts, window
  titles, or telemetry to a network service.
- **Model provisioning is the only outbound network access**: on first run or
  when the selected stack changes, the client downloads missing model files
  from `huggingface.co` over HTTPS. Hugging Face sees the client IP address and
  the requested model/revision; it never receives audio or text.
- Logs contain codes and technical details, never audio, transcripts, window
  titles, or API keys.
- Completed dictations are stored on disk only when `recording.enabled` is
  explicitly turned on. The default directory is
  `%LOCALAPPDATA%\GangioKeet\recordings`; `recording.directory` can override it.
- The clipboard is read only to back up text and DIB, and restored only if
  nobody copied something else in the meantime. Clipboard contents are never
  logged or transmitted. If a paste is blocked by UIPI or `SendInput` fails, the
  dictated text may remain on the clipboard and can be captured by Windows
  Clipboard History or cloud clipboard if those features are enabled.
- `recording.store_context` is accepted for backward compatibility but has no
  effect: selected text is never stored.
- The media pause service reads session metadata from Windows GSMTC but logs
  only counts and error codes, never titles, artists, or URLs.
