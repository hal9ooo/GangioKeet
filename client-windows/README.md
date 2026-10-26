# GangioKeet — Client Windows

Client di dettatura vocale per Windows che invia audio al server GangioKeet su Mac
(Apple Silicon) e incolla il testo nell'app in primo piano. Push-to-talk con Caps Lock
(come Wispr Flow) o F8,
fallback REST trasparente, paste protetto dalla clipboard e recupero esplicito
dell'ultima dettatura fallita.

- Target: **Windows 11** (Windows 10 non certificato).
- Stack: C# / .NET 10, WPF, NAudio 3.3, YamlDotNet 17, Hardcodet.NotifyIcon.Wpf 2.
- Server: WebSocket primario `ws://<MAC_IP>:8000/ws/dictate`, REST fallback
  `POST http://<MAC_IP>:8000/v1/audio/transcriptions`. Con
  `server.scheme: https` il client usa `wss://` e `https://`.

## Avvio rapido

1. Installa il .NET 10 SDK.
2. Copia `client_config.example.yaml` in `%APPDATA%\GangioKeet\client_config.yaml`
   e imposta `server.host` con l'IP del Mac. Se davanti al server c'è un
   terminatore TLS, imposta anche `server.scheme: https`.
3. Se il server richiede un token, imposta la variabile d'ambiente indicata da
   `server.api_key_env` (consigliata `GANGIOKEET_API_KEY`). Il token **non** va mai
   scritto nel file YAML.
4. Compila e avvia:

```powershell
dotnet build client-windows/GangioKeet.Client.sln
dotnet run --project client-windows/src/GangioKeet.Client
```

L'app parte in tray (nessuna finestra principale). **Tieni premuto Caps Lock** per
dettare e rilascia per trascrivere; doppio tap per hands-free.

## Configurazione

Schema strict: chiavi sconosciute sono rifiutate. Il file vive in
`%APPDATA%\GangioKeet\client_config.yaml`; si può indicare un percorso esplicito
con `--config`. Tutti i default sono in `client_config.example.yaml`.

| Sezione | Voci principali |
|---|---|
| `server` | host, scheme (`http` default / `https`), port, transport (`websocket`/`rest`), timeout connect/response/upload, rest_fallback, health interval, api_key_env |
| `hotkey` | mode (`push_to_talk`/`toggle`), key (`capslock` default, f1–f24, mouse_x1), abort_key (escape), double_tap_max_ms 250, double_tap_gap_ms 350 |
| `audio` | input_device (ID endpoint WASAPI o `null` per il default), sample_rate 16000, channels 1, chunk_duration_ms 100, ready_timeout_seconds 3 |
| `paste` | default_combo `ctrl+v`, terminal_combo `ctrl+shift+v`, terminal_apps, clipboard_restore_delay_ms 200, refocus_before_paste |
| `ui` | show_floating_pill, pill_position (`center_bottom` default / `cursor`), sound_feedback |
| `context_profiles` | mappe exe → profilo (`code`, `terminal`, `chat`, `email`, `document`) |
| `browser_profiles` | browser e regole per titolo, priorità email → chat → code → document |
| `recovery` | retention_seconds 600 |

## Come funziona

1. **Hotkey Caps Lock** (PTT, stile Wispr Flow): tieni premuto e parla, rilascia per
   trascrivere. Doppio tap rapido entra in hands-free; il tap successivo ferma. Un
   tap singolo senza secondo tap **non produce nulla** (nessuna dettatura vuota).
   Caps Lock resta disabilitato mentre l'app è aperta: non attiva il maiuscolo. F8
   resta selezionabile con `hotkey.key: "f8"`. In modalità `toggle` il primo keydown
   avvia e il secondo ferma (i keyup non fermano).
2. **Audio**: WASAPI shared, conversione/resampling a PCM16 LE mono 16 kHz,
   chunk da 100 ms (3200 byte). L'audio è tenuto in RAM in un buffer bounded
   (massimo 67108864 byte per dettatura, ~70 minuti a 16 kHz mono).
3. **Trascrizione**: WebSocket con un solo sender e un solo receiver. Se
   l'upgrade WS non è disponibile (404/405/426/200 senza upgrade) e nessun byte
   è stato ancora inviato, la registrazione continua in locale e allo stop viene
   inviata via REST. Dopo l'invio di `start`/audio non c'è fallback automatico:
   si usa il recupero esplicito.
4. **Paste**: il testo viene messo in clipboard, si ricontrolla la finestra target,
   si invia Ctrl+V (o Ctrl+Shift+V nei terminali configurati) e si ripristina la
   clipboard solo se nessuno ha copiato altro nel frattempo. Se il paste fallisce,
   il testo resta recuperabile.
5. **Recupero**: un solo slot in RAM, scadenza 600 s. Dalla tray: «Riprova ultima
   dettatura» (sempre REST, nuova richiesta), «Copia ultimo testo», «Scarta recupero».

## Limiti e sicurezza

- **Nessuno storico su disco**: audio e trascrizioni vivono solo in RAM. I log
  giornalieri in `%LOCALAPPDATA%\GangioKeet\logs\` contengono solo codici e
  messaggi diagnostici, mai audio, testo dettato, titoli di finestra o chiavi.
- **TLS opzionale**: `server.scheme: https` usa `https://` e `wss://`. Il server
  non termina TLS: serve un reverse proxy o un tunnel. Con `http` (default)
  token, audio e titoli delle finestre viaggiano in chiaro.
- **Nessun invio automatico**: il client non preme mai Enter e non invia mai.
- **UIPI**: se la finestra target è elevata (o lo stato non è verificabile),
  l'iniezione è bloccata e il testo non viene incollato. Il paste non tenta
  elevazioni o bypass.
- **Clipboard**: vengono preservati testo e immagine DIB. Non sono preservati
  HTML/RTF/file. Se l'app target impiega più del delay configurato a leggere la
  clipboard, il testo può andare perso (nessun delayed-render).
- **Nessun Ctrl+C automatico**: la selezione non viene copiata né letta.
- **REST fallback ≠ modalità offline**: l'host irraggiungibile produce un errore
  immediato, non una promessa di trascrizione differita.

## Test

Suite logica (fakes, nessun hardware):

```powershell
dotnet test client-windows/GangioKeet.Client.sln --filter "Category!=Integration"
```

Test gated, da eseguire solo quando le risorse sono disponibili:

| Variabile | Copre |
|---|---|
| `RUN_DESKTOP_TESTS=1` | clipboard reale, finestra in primo piano, audio device, hook, autostart |
| `RUN_REAL_SERVER=1` | roundtrip /health, WS PCM, REST PCM, abort contro il Mac (`GANGIOKEET_HOST`, default `192.168.1.167:8000`) |

```powershell
$env:RUN_DESKTOP_TESTS="1"
dotnet test client-windows/GangioKeet.Client.sln --filter "Category=Integration"
```

## Microfono

- La tray ha un sottomenu **Microfono**: «Predefinito di Windows» oppure uno dei
  dispositivi di input attivi. La scelta vale dalla dettatura successiva, viene
  salvata in `client_config.yaml` e sopravvive al riavvio.
- `audio.input_device: null` usa il microfono predefinito di Windows, risolto a
  ogni registrazione.
- Un ID esplicito che non esiste è un errore: nessun cambio mic silenzioso.
- Se Windows nega l'accesso al microfono, il client segnala
  `microphone_permission_denied` e la tray offre «Apri impostazioni microfono»
  (`ms-settings:privacy-microphone`).
- Dispositivo scollegato a metà: la registrazione si interrompe, l'audio già
  catturato resta recuperabile, nessun passaggio automatico ad altro mic.

## Pill

- Compatta (136×28), non attivabile, in basso al centro del monitor sotto il
  puntatore, 24 px sopra la work area (taskbar inclusa nel calcolo).
- `ui.pill_position: "cursor"` la fa apparire vicino al puntatore.
- Su monitor con DPI diversi o origini negative la posizione è clampata alla work
  area del monitor corrente.

## Publishing

```powershell
dotnet publish client-windows/src/GangioKeet.Client/GangioKeet.Client.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

L'output contiene `GangioKeet.Client.exe` più gli asset nativi WPF/NAudio
(`D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`,
`vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`): non è un singolo file autosufficiente.

## Struttura

```
client-windows/
  src/GangioKeet.Client/
    Audio/            cattura WASAPI, chunker, buffer, livelli
    Configuration/    schema YAML strict, credenziale da ambiente
    Context/          profili, finestra in primo piano, UIPI
    Controller/       orchestrazione dettatura (generation ID, recovery)
    Hotkey/           gesture PTT/double-tap/toggle, hook globali, soppressione
    Paste/            snapshot clipboard, SendInput, transazione paste
    Platform/         single-instance, autostart, logger, reload, privacy
    Protocol/         DTO wire, trasporto WS, sessione, REST, health
    Recovery/         slot unico con scadenza
    Ui/               pill non attivabile, suoni sintetici, tray
  tests/GangioKeet.Client.Tests/
```

## Riferimenti

Il contratto wire e i profili sono descritti in questo README e nel codice
`Protocol/`; le note di progetto interne non sono incluse nel repository
pubblico.

## Licenza

MIT — vedi [../LICENSE](../LICENSE).
