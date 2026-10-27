# Release check: Windows local monolith

Data: 2026-10-27. Questo report copre il payload e l'installer; non sostituisce
la prova su una macchina Windows pulita, che non è stata eseguita.

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
- File: **542**; dimensione **7,742 GB**.
- Modelli inclusi: solo i due file della coppia scelta.
- Runtime: `native/transcribe` (transcribe.cpp 0.4.1) e `native/llama`
  (LLamaSharp 0.30.0 patchato, namespace `lgml`). Le copie non patchate sotto
  `runtimes/` prodotte dal publish NuGet sono state rimosse.
- Runtime C++ Microsoft 14.51.36247.0 app-local: `msvcp140.dll`,
  `vcruntime140.dll`, `vcruntime140_1.dll`, `vcomp140.dll`, copiati nella
  radice e nelle due directory native.
- Licenze: `LICENSE`, `THIRD_PARTY_NOTICES.md`, `licenses/llama`,
  `licenses/dotnet`, `licenses/msvc`, `licenses/models` (testo Apache-2.0 e
  attribuzione dei due modelli), più le licenze già incluse in
  `native/transcribe/licenses`.
- Esclusioni verificate: nessun `.py`, nessun `.pdb`, nessun
  `GangioKeet.Eval`, nessun `tools`, nessun `candidates.json`, nessun modello
  non selezionato, nessuna DLL nativa di inferenza fuori da
  `native/transcribe`/`native/llama`, nessun `runtimes/`.

## Comandi eseguiti

```powershell
.\scripts\package_windows.ps1 -SkipInstaller
.\scripts\package_windows.ps1 -Iscc <ISCC.exe portabile>
```

Il primo comando termina con `Verified payload` e non richiede Inno Setup. Il
secondo ricostruisce lo stesso payload, lo verifica e compila l'installer.

## Probe negativo

Su una copia hardlink della directory temporanea è stato rimosso
`models/qwen3_asr_1_7b/Qwen3-ASR-1.7B-F16.gguf`. `verify_windows_package.ps1`
è terminato con exit 1 e:

```
missing: models/qwen3_asr_1_7b/Qwen3-ASR-1.7B-F16.gguf
```

La copia negativa è stata eliminata; nessun file della directory reale è stato
toccato.

## Installer

- Directory: `artifacts/installer`.
- `GangioKeet-Setup-0.1.0-local.exe` più le slice
  `GangioKeet-Setup-0.1.0-local-*.bin`.
- Dimensione totale: **6.453.570.728 byte** (6,4536 GB / 6,01 GiB).
- Compilazione Inno: **720,125 s**.
- SHA-256 (il file `SHA256SUMS.txt` nella directory dell'installer contiene la
  stessa lista):

| File | Byte | SHA-256 |
|---|---:|---|
| `GangioKeet-Setup-0.1.0-local.exe` | 2.328.092 | `5002d6c30a88ca17f8c4ed88db0105be8fc16f5add77668a1db9ba595d781d7d` |
| `GangioKeet-Setup-0.1.0-local-1.bin` | 1.997.671.424 | `27795a2b536946854160e94eb49441965d8e6c138cb158fc20c3ba84dc3cf5fd` |
| `GangioKeet-Setup-0.1.0-local-2.bin` | 2.000.000.000 | `631e0709223b827f4bbaf60213ba9dd708ff7cbfe14e7ea91859d4f64b2c3d4f` |
| `GangioKeet-Setup-0.1.0-local-3.bin` | 2.000.000.000 | `c342b91f6917dac792002dec890e2084503ffc7283a6b6db33035d26f626053b` |
| `GangioKeet-Setup-0.1.0-local-4.bin` | 453.571.212 | `3abddfb062a6657781f0bb2a0419637a29348a045e78f9b879a101b967a8d4de` |

- Inno Setup non può creare un singolo `Setup.exe` oltre 4,2 GB. Il payload di
  7,742 GB supera quel limite, quindi l'installer usa il disk spanning
  supportato da Inno: una sola procedura di installazione per utente, ma più
  file da tenere nella stessa directory. Il setup può chiedere la slice
  successiva durante l'installazione.
- Compressione `zip`: i pesi GGUF sono già ad alta entropia e LZMA2 richiedeva
  molti minuti per un guadagno limitato. La build avviene in
  `artifacts/installer.staging`; il packager rifiuta slice vuote e sostituisce
  l'installer precedente solo dopo una compilazione e una verifica riuscite.
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

- `dotnet test client-windows/GangioKeet.Client.sln --no-restore`:
  **435 passati, 20 saltati** (test opt-in reali).
- `uv run --extra onnx pytest -m 'not integration' -q`: **420 passati,
  14 saltati, 4 deselezionati**.
- Test lifecycle reale opt-in: stop normale **1.479,0 ms**; stop durante il
  caricamento nativo **3.927,7 ms**; layout del payload **1.592,9 ms**.
- Probe negativo del packager: modello rimosso -> exit 1; `runtimes/` estraneo
  -> exit 1.

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
