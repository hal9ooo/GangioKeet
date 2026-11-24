# Changelog

<!-- sprint-flow:changelog — i giorni li scrive ogni notte il timer di sprint-flow
     (proxmox_doctor, deploy/sprint-flow) dai commit: non modificarli a mano. -->

Cosa è cambiato, giorno per giorno, e chi l'ha fatto. Ogni argomento indica gli
agenti che hanno fatto i commit (trailer `Agent:`); «Dal backlog» elenca le voci
tolte da `BACKLOG.md` perché fatte. Il file parte dal 2026-11-13: per la storia
precedente valgono `git log` e le note delle release.

<!-- sprint-flow:inizio 3c0aca9faeb563151d97870a3582723a4f6fb10e -->
<!-- sprint-flow:giorni -->
<!-- sprint-flow:ultimo bf546139b5ae2ad3a1927663beb23696c052a553 -->

## 2026-11-13
<!-- sprint-flow:giorno commit=10 testo=prod-default -->

**Nuovo flusso sprint e backlog a quattro classi** — claude
- BACKLOG.md ora tiene lo sprint corrente in cima e classifica le voci in quattro classi: Prioritario, Importante, Minore, Da valutare.
- CHANGELOG.md passa al formato Keep a Changelog, ricostruito dalle note di rilascio v0.1.0–v0.2.7 e aggiornato ogni notte con i temi del giorno.
- Aggiunte docs/sprints/ per i record giornalieri di sprint e docs/LEZIONI.md per le lezioni apprese.
- Le regole del flusso stanno in AGENTS.md e vengono importate da CLAUDE.md, così gli strumenti di sviluppo seguono le stesse regole.

**Statistiche dettatura aggregate dal manifest** — opencode
- Nuova classe RecordingStats che calcola le statistiche delle dettature partendo dal manifest.
- La lettura del manifest è stata estesa per fornire tutti i dati necessari all'aggregazione.
- Aggiunti test per l'aggregazione e per la lettura completa del manifest.

**Dashboard dettatura nelle impostazioni** — opencode
- La finestra delle impostazioni mostra una dashboard delle dettature con dettaglio comprimibile.
- I valori della dashboard vengono riempiti leggendo il manifest.

**Glossario tecnico applicato a ogni dettatura** — opencode
- Il glossario unito ora viene applicato anche dopo una pulizia riuscita, non solo quando il cleaner fallisce: prima le phonetic_overrides erano di fatto inutilizzate.
- L'eval runner misura la stessa post-elaborazione usata dall'app, così i risultati riflettono il comportamento reale.
- Le correzioni che riscrivevano parole comuni (per esempio 'psicologo') ora scattano solo in contesti riconoscibili, mentre le chiavi fonetiche impossibili in italiano restano sempre attive.

**Hash della selezione modelli aggiornati** — opencode
- La selezione dei modelli punta al nuovo hash della cleanup policy.
- Aggiornato il manifest del pacchetto con l'hash corretto, così l'installazione usa i modelli giusti.

**Dal backlog**
- Riconoscere dal contesto i nomi ambigui che il glossario non puo risolvere (`Cloud` vs `Claude`, `Brain`): serve un resolver contestuale addestrato su `reference_final`, oggi tutte le 779 righe sono `unreviewed`. (L)… — opencode
- Registro delle dettature nelle impostazioni (`RecordingLogItem`, `RecordingStore`, `SettingsWindow`): completare e committare. (M) (lavoro non committato nel checkout, 13/11) — opencode

<!-- sprint-flow:giorni-fine -->

## Prima di sprint-flow

Notable changes for people who use ChattyNeko (formerly GangioKeet). Format:
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow
[SemVer](https://semver.org/). Each entry ends with `(agent, YYYY-MM-DD)`;
`n/a` marks work done before commits recorded the agent (2026-11-12). The
day-by-day record of who did what is in [`docs/sprints/`](docs/sprints/).

## [Unreleased]

## [0.2.7] - 2026-11-12

### Added
- Live loading and warm-up feedback in Settings: download and hash check,
  weights loading, pipeline warm-up, hot-swap completion. (n/a, 2026-11-12)

### Fixed
- Selecting Nemotron no longer crashes the app (cross-thread progress updates
  into WPF). (n/a, 2026-11-12)
- Native `RecognitionOptions` aligned with the upstream NeMo-Speech C ABI.
  (n/a, 2026-11-12)

### Changed
- Model loading and teardown run off the UI thread, so Settings stays
  responsive during large model swaps. (n/a, 2026-11-12)

## [0.2.4] - 2026-11-07

### Changed
- Splash: a realistic black cat peeking from the bush, MEEEOW bubble moved off
  the title, warmer typography (Candara, larger sizes). (n/a, 2026-11-07)

## [0.2.3] - 2026-11-07

### Changed
- New comic splash art: a crowd of singing cats, the black cat and a reporter
  in the bushes; softer cutout and a smaller title scrim. (n/a, 2026-11-07)

## [0.2.2] - 2026-11-07

### Fixed
- The installer and uninstaller close the running tray-only app through a named
  event instead of failing on locked files; no more Restart Manager dialog; a
  legacy `GangioKeet.Client.exe` is closed too. (n/a, 2026-11-07)

## [0.2.1] - 2026-11-07

### Changed
- Friendlier mascot (new app icon, tray icon and hero image) and a splash with
  the product name and payoff. (n/a, 2026-11-07)

## [0.2.0] - 2026-11-06

### Changed
- Renamed to ChattyNeko: new executable, install folder and data folders;
  existing configuration, recordings and microphone calibration move
  automatically on first run. (n/a, 2026-11-06)
- New brand identity: black cat, golden eyes, midnight blue, cyan and ivory.
  (n/a, 2026-11-06)

### Removed
- The animated parakeet mascot; the README hero is a static image.
  (n/a, 2026-11-06)

## [0.1.5] - 2026-11-06

### Added
- Configurable models folder (`inference.models_root`) with a folder picker in
  Settings. (n/a, 2026-11-06)
- Deterministic elision corrections in refinement (`la gente` → `l'agente`),
  also with Pulizia leggera. (n/a, 2026-11-06)

### Changed
- Settings page rebuilt: readable Italian labels, model display names, dark
  controls, two-column layout. (n/a, 2026-11-06)
- Catalog metadata moved to `catalog\`, outside `models\`. (n/a, 2026-11-06)

### Fixed
- Models are read through the junction target and the app starts unelevated.
  (n/a, 2026-11-06)

## [0.1.0] - 2026-11-02

### Added
- First public release: local push-to-talk dictation for Windows (hold Caps
  Lock, talk, release). Local ASR (Parakeet v3, Qwen3-ASR 1.7B, experimental
  Nemotron), optional refinement (Pulizia leggera, Gemma 4 E2B, Qwen3), floating
  pill with live waveform and autogain, media pause/resume, resumable model
  downloads, start with Windows, recovery of failed dictations. (n/a, 2026-11-02)

[Unreleased]: https://github.com/hal9ooo/GangioKeet/compare/v0.2.7...HEAD
[0.2.7]: https://github.com/hal9ooo/GangioKeet/compare/v0.2.4...v0.2.7
[0.2.4]: https://github.com/hal9ooo/GangioKeet/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/hal9ooo/GangioKeet/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/hal9ooo/GangioKeet/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/hal9ooo/GangioKeet/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/hal9ooo/GangioKeet/compare/v0.1.5...v0.2.0
[0.1.5]: https://github.com/hal9ooo/GangioKeet/compare/v0.1.0...v0.1.5
[0.1.0]: https://github.com/hal9ooo/GangioKeet/releases/tag/v0.1.0
