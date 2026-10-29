# Release check: Windows local monolith

Data: 2026-10-27; aggiornato: 2026-10-29. Questo report copre il payload e
l'installer; non sostituisce la prova su una macchina Windows pulita, che non è
stata eseguita.

## Ambiente di build

- Windows 11 Home 10.0.26300.
- CPU AMD Ryzen AI MAX+ 395, GPU AMD Radeon(TM) 8060S Graphics, driver
  `32.0.31041.5005`.
- .NET SDK 10.0.403; runtime `Microsoft.WindowsDesktop.App` 10.0.14.
- Inno Setup 6.7.3, usato in modalità portabile da una directory temporanea.
  `ISCC.exe` SHA-256
  `0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7`;
  installer ufficiale SHA-256
  `9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732`.
  Nessuna installazione system-wide, nessun servizio, nessuna regola firewall.

## Coppia e artefatti

- Coppia: Qwen3-ASR-1.7B F16 + Gemma 4 E2B-it Q4_0, Vulkan.
- `client-windows/models/selected.json` SHA-256
  `e57502e28f87e89b17b398366aa4288623c21d882b6666583a304e3fdf6f8d73`.
- `client-windows/native/runtime-lock.json` SHA-256
  `e7ff49f052c9a37b917d2e844f8185215cda8a60d01e1eaa5bf6e925b1436846`.
- Policy `cleanup.yaml` hash canonico
  `69483c177408d58b62dea1f3a9ff660cb9bee973144ffd1ee5f6fad9e8825740`.
- Prompt `conservative-local-v3` SHA-256
  `e63fa4085c525b203bbd71897a25c03af0984b0a627ee229e6304081a349e907`.
- Modelli: `Qwen3-ASR-1.7B-F16.gguf` e `gemma-4-E2B_q4_0-it.gguf`, hash
  verificati contro `selected.json`.

## Payload

- Directory: `artifacts/package`.
- File: **549**; dimensione **326.281.071 byte** (0,304 GiB).
- Modelli: provisioning on demand; **nessun GGUF nel payload**. I file sono
  scaricati dal client e verificati contro `models/selected.json`.
- Runtime: `native/transcribe` (transcribe.cpp 0.4.1) e `native/llama`
  (LLamaSharp 0.30.0 patchato, namespace `lgml`). Le copie non patchate sotto
  `runtimes/` prodotte dal publish NuGet sono state rimosse.
- Runtime C++ Microsoft 14.51.36247.0 app-local: `msvcp140.dll`,
  `vcruntime140.dll`, `vcruntime140_1.dll`, `vcomp140.dll`, copiati nella
  radice e nelle due directory native.
- Licenze: `LICENSE`, `THIRD_PARTY_NOTICES.md`, `licenses/llama`,
  `licenses/dotnet`, `licenses/msvc`, `licenses/models` (Apache-2.0,
  CC-BY-4.0 e attribuzione delle due coppie di modelli), `licenses/managed`
  (testi MIT di NAudio, Hardcodet.NotifyIcon.Wpf, YamlDotNet e
  CommunityToolkit.HighPerformance), più le licenze già incluse in
  `native/transcribe/licenses`.
- Esclusioni verificate: nessun `.py`, nessun `.pdb`, nessun
  `GangioKeet.Eval`, nessun `tools`, nessun `candidates.json`, nessun GGUF,
  nessuna DLL nativa di inferenza fuori da
  `native/transcribe`/`native/llama`, nessun `runtimes/`.

## Comandi eseguiti

```powershell
.\scripts\package_windows.ps1 -SkipInstaller
.\scripts\package_windows.ps1 -Iscc <ISCC.exe portabile>
```

Il primo comando termina con `Verified payload` e non richiede Inno Setup. Il
secondo ricostruisce lo stesso payload, lo verifica e compila l'installer.

## Probe negativo

Il probe con modello rimosso appartiene alla vecchia modalità con modelli
inclusi e non è più applicabile: ora i GGUF non fanno parte del payload e
`verify_windows_package.ps1` verifica layout, runtime, licenze ed esclusioni.

## Installer

- Directory: `artifacts/installer`.
- `GangioKeet-Setup-0.1.0-local.exe` (nessuna slice).
- Dimensione: **72.828.546 byte** (69,4 MiB).
- SHA-256 (l'hash autorevole è `artifacts/installer/SHA256SUMS.txt`; il
  `package-manifest.json` non contiene l'hash dell'installer per evitare
  autoreferenza):

| File | Byte | SHA-256 |
|---|---:|---|
| `GangioKeet-Setup-0.1.0-local.exe` | 72.828.546 | `e1cc0df9b34968d55c69dad494d2e9322d58acd6dddd38d0e71b8c701c5e2e5b` |

- Compressione `lzma2/max`, nessun disk spanning. La build avviene in
  `artifacts/installer.staging`; il packager sostituisce l'installer precedente
  solo dopo una compilazione e una verifica riuscite.
- Installazione per utente in `%LOCALAPPDATA%\Programs\GangioKeet`, nessun
  servizio, nessun task pianificato, nessuna regola firewall.
- Disinstallazione: rimuove i file installati; le registrazioni in
  `%LOCALAPPDATA%\GangioKeet\recordings` non vengono toccate.
- L'installer e `GangioKeet.Client.exe` non sono firmati: Windows SmartScreen
  può mostrare un avviso.

## Smoke test del layout confezionato

Il test opt-in `LocalLifecycleTests` è stato eseguito puntando direttamente al
payload (`artifacts/package/models`, `native/transcribe`, `native/llama`,
`native/runtime-lock.json`) invece che alla build di sviluppo. La coppia si è
caricata e `StopAsync` è terminato in **1.592,9 ms**, senza `runtimes/`. Questo
verifica che la rimozione delle copie NuGet non impedisca il caricamento del
runtime patchato; non sostituisce la prova su una macchina pulita.

## Gate

- `dotnet test client-windows/GangioKeet.Client.sln --no-build -c Debug`:
  **324 passati, 15 saltati** (269 Client + 55 Inference; gli skip sono test
  opt-in reali).
- `.venv\Scripts\python -m pytest -q`: **36 passati**.
- Test lifecycle reale opt-in: stop normale **1.479,0 ms**; stop durante il
  caricamento nativo **3.927,7 ms**; layout del payload **1.592,9 ms**.
- Il packager rifiuta DLL native fuori dalle directory pinnate e artefatti di
  sviluppo nel payload (`verify_windows_package.ps1`).

## Verifiche non eseguite

- Installazione e avvio su una macchina Windows pulita, senza SDK/Python e
  offline dopo l'installazione.
- Upgrade da una configurazione remota esistente.
- Scorciatoia, singola istanza, errore GPU comprensibile e uscita senza
  residui dalla build installata.
- Latenza reale release-to-paste su una finestra desktop controllata.
- Corpus personale revisionato.

Queste voci restano aperte e non vanno conteggiate come verdi. La build
dimostra che il payload e l'installer si producono e che gli hash attesi sono
presenti; non dimostra il comportamento su una macchina diversa da questa.
Nessuna release è stata pubblicata.
