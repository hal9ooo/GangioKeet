# Security Policy

## Supported versions

The project is pre-1.0. Security fixes are applied to the `main` branch.

## Reporting a vulnerability

Do not open a public issue. Use GitHub's private vulnerability reporting
("Report a vulnerability" in the **Security** tab) when available, or contact
the maintainer through GitHub.

Include:

- affected version or commit;
- platform (macOS or Windows);
- reproduction steps;
- expected impact.

Do not include keys, tokens, real audio, or personal transcripts. If you need
an audio sample, use a synthetic fixture.

## Scope

Covered: the macOS server (FastAPI, WebSocket, REST), the Windows client (WPF),
and the deployment scripts. MLX/Hugging Face models and third-party
dependencies should be reported to their respective projects.
