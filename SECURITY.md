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

## Data handling

- The runtime opens no HTTP listener and sends no audio or text to a network
  service.
- Logs contain codes and technical details, never audio, transcripts, window
  titles, or API keys.
- Completed dictations are stored on disk only when `recording.enabled` is
  explicitly turned on, under `%LOCALAPPDATA%\GangioKeet\recordings`.
- The media pause service reads session metadata from Windows GSMTC but logs
  only counts and error codes, never titles, artists, or URLs.
