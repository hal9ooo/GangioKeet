# Server API compatibile OpenAI

ChattyNeko può esporre la propria dettatura come API HTTP con la forma di quella di
OpenAI. Il server gira **dentro** l'app di tray, sullo stesso processo della pipeline:
una dettatura chiesta da un altro programma costa il modello già caricato, non una
seconda copia dei pesi. È spento per impostazione predefinita e, acceso senza
configurazione, ascolta solo sul loopback.

Il server non è un servizio: vive quanto l'app. Se l'app è chiusa, la porta è chiusa.

## Attivare

Nella configurazione utente, `%APPDATA%\ChattyNeko\client_config.yaml`:

```yaml
api:
  enabled: true                  # senza questo il server non parte
  listen: ["0.0.0.0"]            # tutte le interfacce; il predefinito è ["127.0.0.1"]
  port: 5099                     # 1024-65535; sotto 1024 servono privilegi di amministratore
  model_alias: "gangio-keet"     # l'unico nome che i client possono chiedere
  token_env: "GANGIOKEET_API_TOKEN"  # nome della variabile, non il token
  max_request_bytes: 26214400    # 25 MB di corpo HTTP
  max_audio_seconds: 300         # 5 minuti di audio per richiesta
  wait_seconds: 120              # attesa massima che i modelli diventino pronti
```

La cartella è `ChattyNeko`, non `GangioKeet`: `Platform/Branding.DataFolderName` vale
`ChattyNeko` e `ConfigLoader.DefaultPath` costruisce il percorso da lì. Una cartella
`%APPDATA%\GangioKeet` resta solo come punto di partenza della migrazione una tantum del
rebrand: un file scritto lì dopo la migrazione non viene letto da nessuno, e l'app sembra
semplicemente non accorgersi della configurazione.

Ogni valore ha il proprio motivo, e i predefiniti sono già quelli del file di esempio
`client-windows/client_config.example.yaml`:

| chiave | predefinito | perché |
| --- | --- | --- |
| `enabled` | `false` | un'installazione pulita non apre nessuna porta |
| `listen` | `["127.0.0.1"]` | solo la macchina; per la tailnet serve `"0.0.0.0"` |
| `port` | `5099` | sopra 1024, così non servono privilegi per il socket |
| `model_alias` | `gangio-keet` | il client non sceglie il modello: c'è un solo motore |
| `token_env` | `GANGIOKEET_API_TOKEN` | il token vive solo nell'ambiente, mai nel file |
| `max_request_bytes` | `26214400` | 25 MB: oltre, Kestrel risponde 413 prima di bufferizzare |
| `max_audio_seconds` | `300` | cinque minuti: un audio più lungo terrebbe ferma la dettatura locale per tutta la sua durata |
| `wait_seconds` | `120` | attesa massima del caricamento modelli; oltre, meglio un `503 model_loading` che una richiesta appesa |

Con `listen` diverso dal loopback il token è **obbligatorio**: senza, l'app registra
`api_refused` e prosegue **senza server** (la dettatura locale continua a funzionare).
È il caso in cui la voce tray dice `Server: spento`.

Il file di configurazione viene letto all'avvio: cambiare `api` richiede di riavviare
l'app.

## Il token

Il token **non si scrive nella configurazione**. Lì c'è solo il *nome* della variabile
(`api.token_env`); il valore si mette nell'ambiente del processo prima di avviare l'app.

Il valore va letto, non digitato: un comando incollato in una shell finisce nella history,
e con lui il token. Questi esempi lo chiedono senza mostrarlo e senza lasciarne traccia.

PowerShell, solo per la sessione corrente (il metodo più semplice per provare). `Read-Host
-AsSecureString` non mostra i caratteri, e la conversione a testo avviene dentro la
parentesi: nella command line non compare il valore, solo il nome della variabile.

```powershell
$env:GANGIOKEET_API_TOKEN = [System.Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Token API')).Password
```

PowerShell, persistente per l'utente (l'app lo trova a ogni avvio, da una nuova
sessione in poi):

```powershell
[Environment]::SetEnvironmentVariable('GANGIOKEET_API_TOKEN',
  [System.Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Token API')).Password, 'User')
```

Bash (WSL, Git Bash, o un'altra macchina della tailnet), senza finire nella history:

```bash
read -rs GANGIOKEET_API_TOKEN && export GANGIOKEET_API_TOKEN
```

Se il token sta in un password manager, si legge da lì invece di chiederlo: per esempio
`export GANGIOKEET_API_TOKEN="$(op read 'op://Homelab/gangio-keet/token')"`. Va bene
qualunque comando che restituisca il valore su stdout: quello che non va bene è il
letterale scritto nel comando.

Gli esempi `curl` più sotto sono scritti in stile bash (`$GANGIOKEET_API_TOKEN`). In
PowerShell si usa `$env:GANGIOKEET_API_TOKEN` al posto di `$GANGIOKEET_API_TOKEN`.

Un token casuale, se non ne hai uno:

```powershell
-join ((48..57) + (65..90) + (97..122) | Get-Random -Count 40 | ForEach-Object { [char]$_ })
```

Regole che il codice rispetta e che valgono anche per chi lo installa:

- il token non compare mai nei log dell'app, nemmeno quando la richiesta è rifiutata
  (nel log va l'indirizzo di chi ha chiamato, non l'intestazione ricevuta);
- non va messo in un file di configurazione, in uno script versionato o in un comando
  salvato nella history;
- si presenta come `Authorization: Bearer <token>` oppure come `X-Api-Key: <token>`.

## Firewall: aprire la porta solo alla tailnet

Windows blocca le connessioni in ingresso finché una regola non le ammette.
`scripts/api_firewall.ps1` crea **una** regola, limitata all'intervallo CGNAT di
Tailscale (`100.64.0.0/10`), mai a qualsiasi indirizzo:

Lo script vive nel **checkout del repository** (`<repo>/scripts/api_firewall.ps1`), non
in un'installazione: `scripts/package_windows.ps1` non spedisce la cartella `scripts/`.
I comandi qui sotto vanno quindi lanciati dalla radice del repository (o passando il
percorso completo dello script), non da una copia installata dell'app.

```powershell
# aprire (chiede l'elevazione se non sei amministratore)
powershell -ExecutionPolicy Bypass -File scripts/api_firewall.ps1 -Action Add -Port 5099

# controllare
powershell -ExecutionPolicy Bypass -File scripts/api_firewall.ps1 -Action Status

# chiudere
powershell -ExecutionPolicy Bypass -File scripts/api_firewall.ps1 -Action Remove
```

Comportamento:

- la regola si chiama `ChattyNeko API (Tailscale)`, TCP in ingresso, `Action Allow`,
  profilo `Any`, porta `$Port`, sorgente `100.64.0.0/10`;
- `-RemoteAddress` accetta **solo** `100.64.0.0/10`: un altro valore viene rifiutato
  dal parametro stesso, non applicato;
- `Add` è idempotente: se la regola esiste ed è già quella giusta non tocca niente, se
  esiste con porta o sorgente diverse la sostituisce;
- `Add` **rifiuta** di creare la regola se l'interfaccia `Tailscale` non ha un indirizzo
  IPv4 (Tailscale spento o non installato): una regola per un'interfaccia che non esiste
  non ammetterebbe niente e sembrerebbe invece configurata;
- `Status` funziona anche senza elevazione e mostra regola e indirizzo Tailscale.

Le righe sopra descrivono cosa fa lo script, non una sessione eseguita: `Add` e `Remove`
richiedono l'elevazione e cambiano il firewall della macchina, quindi sono stati verificati
leggendo il codice, non eseguendoli. Il primo `Add` dell'operatore è la prova vera, e la
prova da un'altra macchina più sotto è il controllo che la regola faccia il suo lavoro.

Il firewall non è l'autenticazione: `listen: ["0.0.0.0"]` apre il socket su tutte le
interfacce, e chi arriva dalla tailnet deve comunque presentare il token. La regola
serve a non far arrivare nulla dalla LAN.

## Verificare che risponda

Con un token configurato, `/health` è l'unica rotta senza token: risponde senza rivelare
audio, testo o percorsi. Senza token (il predefinito, solo loopback) il server non
installa affatto il controllo: tutte le rotte sono aperte sul loopback, ed è proprio per
questo che l'app rifiuta di aprire un indirizzo di rete quando il token manca.

```bash
curl http://127.0.0.1:5099/health
```

```json
{"status":"ok","ready":true,"warming_up":false,"model":"gangio-keet"}
```

`ready` è falso finché i modelli non sono caricati; `warming_up` è vero durante il
caricamento. Le altre rotte aspettano i modelli per al massimo `api.wait_seconds` e poi
rispondono `503 model_loading`; se il caricamento non è nemmeno in corso, il `503` arriva
subito, senza far aspettare il chiamante per una finestra che non si chiuderà.

## `curl`: trascrizione

`POST /v1/audio/transcriptions`, multipart con il campo `file`, come l'API OpenAI.

```bash
curl -s http://127.0.0.1:5099/v1/audio/transcriptions \
  -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -F model=gangio-keet \
  -F file=@detta.wav
```

```json
{"text":"Questo è il testo dettato."}
```

Con `response_format=text` la risposta è `text/plain` con il solo testo, senza JSON:

```bash
curl -s http://127.0.0.1:5099/v1/audio/transcriptions \
  -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -F model=gangio-keet -F response_format=text -F file=@detta.wav
```

Se il motore non ha prodotto testo, il JSON aggiunge `"no_speech": true` (il campo
compare solo quando è vero, come nella forma normale di OpenAI). La condizione è quella
della dettatura locale: il testo grezzo dell'ASR è vuoto, anche quando il guardiano del
silenzio ha azzerato un'eco su audio muto. Un finale vuoto con un grezzo pieno non è
silenzio.

## `curl`: dettatura completa

`POST /v1/dictation` accetta l'audio come **corpo binario** (con `Content-Type`
`audio/wav` o quello del file) oppure come multipart con `file`. Restituisce anche il
testo grezzo dell'ASR, il testo del cleaner e le latenze.

```bash
curl -s http://127.0.0.1:5099/v1/dictation \
  -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -H "Content-Type: audio/wav" \
  -H "X-Dictation-Profile: code" \
  --data-binary @detta.wav
```

```json
{"text":"Testo finale.","raw_text":"testo grezzo","cleaner_text":null,"no_speech":false,"warnings":[],"latency":{"stt_ms":1200,"llm_ms":0,"total_ms":1200}}
```

Il profilo di pulizia è facoltativo e vale `default`. Precedenza: campo multipart
`profile`, poi intestazione `X-Dictation-Profile`, poi `?profile=`. Valori ammessi:
`default`, `code`, `terminal`, `chat`, `email`, `document`. Un altro valore è
`400 unsupported_profile`, non un ripiego silenzioso. Con l'audio multipart valgono gli
stessi rifiuti di `/v1/audio/transcriptions`: `model` deve essere l'alias, e ogni altro
campo (`language`, `prompt`, `temperature`, `response_format`, …) è
`400 unsupported_parameter`. I campi ammessi qui sono `file`, `profile` e `model`.

**Registrazioni.** Con `recording.enabled: true` ogni turno API riuscito, su entrambe le
rotte, viene scritto nel corpus locale come una dettatura qualsiasi; al posto del titolo
della finestra porta l'indirizzo di chi ha chiamato. Un server aperto alla tailnet
raccoglie quindi anche l'audio che arriva da lì: se non lo si vuole, le registrazioni si
spengono.

## `curl`: correzione di un testo

`POST /v1/correct` prende un testo, non un audio, e restituisce lo stesso testo passato
dalle correzioni deterministiche della dettatura locale (filler, glossario, elisioni,
nello stesso ordine). Serve alle app che dettano con un **proprio** motore — Hex — e non
possono usare le rotte audio: condividono il glossario senza un secondo processo.

```bash
curl -s http://127.0.0.1:5099/v1/correct \
  -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"text":"ehm fammi provare ciper inference","profile":"default"}'
```

```json
{"text":"fammi provare Cheaper Inference"}
```

Il corpo ammette solo `text` (obbligatorio, massimo 100 000 caratteri) e `profile`
(facoltativo, stessi sei valori di `/v1/dictation`, `default` se assente). Risposta:
esattamente `{"text":"..."}`. Un testo di soli spazi restituisce `{"text":""}`: il
chiamante ha già deciso che non c'era niente da correggere.

La rotta **non tocca i modelli**: non consuma la coda di inferenza, non aspetta una
trascrizione in corso e risponde anche mentre i pesi caricano (dove `/v1/dictation`
sarebbe un `503 model_loading`). Se `cleanup.yaml` non si carica, l'app registra
`api_policy_failed` e non avvia il server: una correzione con una policy vuota
riscriverebbe il testo secondo regole che non esistono, e la dettatura locale continua a
funzionare comunque.

## Formati accettati

La decodifica passa da Media Foundation, che su Windows sa aprire:

| contenitore | esito |
| --- | --- |
| WAV (PCM, qualsiasi frequenza/canali) | sì, i 16 kHz mono non vengono ricampionati |
| MP3 | sì |
| M4A / AAC | sì |
| FLAC | sì |
| WebM / Opus | sì |
| **OGG / Vorbis** | **no: `415 unsupported_audio_format`** |

**Il limite documentato è OGG/Vorbis.** Su un'installazione Windows standard il
decoder Vorbis di Media Foundation non c'è e il file viene rifiutato con:

```json
{"error":"unsupported_audio_format","message":"Formato audio non supportato: usa WAV, MP3, M4A, FLAC o WebM.","status":415}
```

Il limite non è fissato da un test automatico: è stato verificato a mano con un OGG
vero (vedi «Verifica locale»). Il test sui byte non decodificabili
(`AudioPcmDecoderTests.RejectsBytesThatAreNotDecodableAudio`) pinna il messaggio
d'errore, non il codec: un fixture sintetico non distinguerebbe «codec assente» da
«fixture non valido», quindi non può dimostrare il limite OGG. Se un giorno il decoder
sarà disponibile, va tolta questa nota. Nel frattempo il file si converte, una volta,
con ffmpeg:

```bash
ffmpeg -i detta.ogg -ar 16000 -ac 1 detta.wav
```

Un file senza campioni (WAV valido ma vuoto) è anch'esso `415`: il motore ASR
rifiuterebbe il PCM vuoto e diventerebbe un 500.

## Un solo modello

`model` deve valere `gangio-keet` (o essere omesso: c'è un solo modello, e un client
minimale non merita un 400). Qualunque altro valore, compreso `whisper-1`, è:

```json
{"error":"model_not_found","message":"Il modello 'whisper-1' non è disponibile: l'unico ammesso è 'gangio-keet'.","status":400}
```

La scelta è una decisione di prodotto, non una dimenticanza: i pesi caricati sono quelli
scelti dall'operatore, e un nome diverso non ne caricherebbe altri.

Allo stesso modo i parametri che cambierebbero il comportamento (`language`, `prompt`,
`temperature`, `timestamp_granularities`, …) sono **rifiutati** con
`400 unsupported_parameter`, mai ignorati: un client che manda `language=it` e riceve
200 crederebbe di aver controllato una cosa che il server non applica. Campi ammessi su
`/v1/audio/transcriptions`: `file`, `model`, `response_format`. Su `/v1/dictation`, dove
il multipart è facoltativo: `file`, `model`, `profile`.

## Errori

Il corpo è sempre lo stesso: `{"error":"<codice>","message":"<messaggio>","status":<http>}`.

| HTTP | codice | quando |
| --- | --- | --- |
| 400 | `model_not_found` | `model` diverso dall'alias |
| 400 | `unsupported_parameter` | un campo che il server non applicherebbe, o `response_format` non ammesso |
| 400 | `unsupported_profile` | `profile` non fra i sei ammessi |
| 400 | `invalid_request` | multipart illeggibile, campo `file` assente, corpo non multipart su `/v1/audio/transcriptions` (che vuole `multipart/form-data`), oppure su `/v1/correct` un corpo senza `text`, non JSON, con un campo sconosciuto o oltre 100 000 caratteri |
| 401 | `unauthorized` | token assente, malformato o sbagliato (con `WWW-Authenticate: Bearer`) |
| 413 | `audio_too_large` | corpo oltre `api.max_request_bytes`, oppure audio decodificato oltre `api.max_audio_seconds` |
| 415 | `unsupported_audio_format` | contenuto non decodificabile in PCM16 mono 16 kHz (OGG compreso) |
| 503 | `busy` | più di tre richieste ammesse insieme (una in corso, due in attesa): il rifiuto è immediato, prima di decodificare e senza guardare `api.wait_seconds` |
| 503 | `model_loading` | i modelli non sono pronti: subito se il caricamento non è in corso, oppure allo scadere di `api.wait_seconds` se lo è; anche quando l'app si sta spegnendo e il turno in corso è stato annullato (meglio un 503 esplicito di un `200` dal corpo vuoto) |
| 500 | `internal_error` | guasto inatteso (il dettaglio va nel log dell'app, non nella risposta) |

## Puntare un client compatibile OpenAI

I client che accettano una base URL OpenAI personalizzata funzionano senza modifiche,
purché accettino un modello scritto a mano.

| impostazione | valore |
| --- | --- |
| Base URL | `http://100.82.101.125:5099/v1` |
| API key | il token (`GANGIOKEET_API_TOKEN`) |
| Modello | `gangio-keet` |

L'indirizzo `100.82.101.125` è l'IPv4 Tailscale **di questa macchina**; su un'altra
installazione è quello che mostra `tailscale ip -4`. Con la base URL che finisce in
`/v1`, il client chiama `.../v1/audio/transcriptions` come farebbe con OpenAI.

Se il client manda `language`, `prompt` o `temperature` non c'è modo di farlo funzionare:
il server li rifiuta di proposito (vedi sopra).

## Verifica locale

Eseguita su questa macchina il 2026-11-27 con il server avviato da un harness monouso
(poi cancellato) in ascolto su `http://127.0.0.1:5099`, token preso dalla variabile
d'ambiente, e audio vero:

- WAV di 172 s, 16 kHz mono PCM16, copiato da
  `%LOCALAPPDATA%\ChattyNeko\recordings\audio\20261113T0816419796585Z-79c583c767a64d06.wav`;
- lo stesso audio convertito in MP3 (`libmp3lame`, 64 kbps) e in OGG (`libvorbis`, q3)
  con ffmpeg 9.1.

**Cosa questa prova non copre.** Su questa macchina i modelli ASR non sono provisionati
(nessun `.gguf` fra quelli che l'app carica, da `<install>\models` o dalla cartella di
`inference.models_root`), quindi l'harness usava una pipeline finta che restituisce il
testo qui sotto. La prova verifica trasporto HTTP, token, contratto delle risposte e
**decodifica reale dei formati**; non verifica la qualità della trascrizione. L'app
dell'operatore non è stata avviata, fermata né riavviata: il server era un processo
separato.

Comandi (il token era in `$GANGIOKEET_API_TOKEN`, mai stampato). Le risposte qui sotto
sono quelle vere del server, ma sono state raccolte dall'harness C# che ha chiamato le
rotte, non lanciando a mano `curl` e copiando l'output: gli stati e i corpi coincidono con
quelli che questi comandi producono, l'invocazione esatta no.

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5099/health
# 200

curl -s -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -F model=gangio-keet -F file=@verifica.wav \
  http://127.0.0.1:5099/v1/audio/transcriptions
# 200 {"text":"Verifica locale: ricevuti 5528000 byte PCM (172 s), profilo default."}

curl -s -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -F model=gangio-keet -F file=@verifica.mp3 \
  http://127.0.0.1:5099/v1/audio/transcriptions
# 200 {"text":"Verifica locale: ricevuti 5530848 byte PCM (172 s), profilo default."}

curl -s -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -F model=gangio-keet -F file=@verifica.ogg \
  http://127.0.0.1:5099/v1/audio/transcriptions
# 415 {"error":"unsupported_audio_format","message":"Formato audio non supportato: usa WAV, MP3, M4A, FLAC o WebM.","status":415}

curl -s -F model=gangio-keet -F file=@verifica.wav \
  http://127.0.0.1:5099/v1/audio/transcriptions
# 401 {"error":"unauthorized","message":"Token mancante o non valido: usa 'Authorization: Bearer <token>' oppure 'X-Api-Key'.","status":401}

curl -s -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
  -F model=whisper-1 -F file=@verifica.wav \
  http://127.0.0.1:5099/v1/audio/transcriptions
# 400 {"error":"model_not_found","message":"Il modello 'whisper-1' non è disponibile: l'unico ammesso è 'gangio-keet'.","status":400}
```

In più, con lo stesso audio:

```bash
curl -s -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" -H 'X-Dictation-Profile: code' \
  -H 'Content-Type: audio/wav' --data-binary @verifica.wav \
  http://127.0.0.1:5099/v1/dictation
# 200 {"text":"Verifica locale: ricevuti 5528000 byte PCM (172 s), profilo code.","raw_text":"grezzo: 5528000 byte","cleaner_text":null,"no_speech":false,"warnings":[],"latency":{"stt_ms":1,"llm_ms":0,"total_ms":1}}

curl -s -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" http://127.0.0.1:5099/v1/models
# 200 {"object":"list","data":[{"id":"gangio-keet","object":"model","owned_by":"chattyneko"}]}
```

Risultato: tutte le risposte attese, nessuna discrepanza. Sul server, nel log:

```
[api_listening] Server API in ascolto su http://127.0.0.1:5099.
[api_turn] Trascrizione servita in 1 ms (profilo default, 5528044 byte).
[api_unauthorized] Richiesta rifiutata senza token valido da 127.0.0.1.
```

Il log nomina l'indirizzo di chi ha chiamato e non il token presentato.

## Prova da un'altra macchina della tailnet

**Non eseguita da questa macchina: va fatta dall'operatore.** Serve una seconda macchina
con Tailscale attivo e sulla stessa tailnet (`tailscale status` la elenca).

1. Sulla macchina con l'app: `api.enabled: true`, `api.listen: ["0.0.0.0"]`, token in
   `GANGIOKEET_API_TOKEN`, regola firewall aggiunta
   (`scripts/api_firewall.ps1 -Action Add`). Riavviare l'app.
2. Sulla seconda macchina, il comando esatto:

   ```bash
   curl -s -o /dev/null -w '%{http_code}\n' http://100.82.101.125:5099/health
   ```

   **Esito atteso: `200`.**

3. Sempre dalla seconda macchina, una trascrizione vera:

   ```bash
   curl -s -H "Authorization: Bearer $GANGIOKEET_API_TOKEN" \
     -F model=gangio-keet -F file=@detta.wav \
     http://100.82.101.125:5099/v1/audio/transcriptions
   ```

   **Esito atteso:** `200` con `{"text":"…"}`.

4. Controprova del perimetro: senza `Authorization`, lo stesso comando deve dare
   `401 unauthorized`; da una rete **fuori** dalla tailnet (per esempio hotspot del
   telefono, Tailscale spento) la connessione deve fallire, non rispondere 200.

Se il passo 2 va in timeout ma `/health` risponde su `127.0.0.1` dalla macchina con
l'app, il problema è il firewall o `listen`, non il server.
