# MLX Dictation Server

Demone locale per macOS Apple Silicon che tiene in memoria un modello STT
Parakeet e un LLM quantizzato MLX, ed espone una pipeline di dettatura su
WebSocket con fallback REST compatibile con l'API OpenAI Audio.

- Un solo processo, una sola `asyncio.Lock`: STT e LLM non usano Metal
  contemporaneamente.
- KV cache del system prompt congelata a boot e riusata a ogni richiesta.
- Warm-up di entrambi gli engine prima del bind HTTP.
- Nessun file temporaneo nel percorso principale: il PCM viaggia in RAM
  (l'unica eccezione è la modalità opt-in `--recording`).
- `mx.clear_cache()` dopo ogni richiesta gestita dalla pipeline, completata,
  annullata o fallita.

## Requisiti

- macOS su Apple Silicon.
- [uv](https://docs.astral.sh/uv/).
- `ffmpeg` opzionale: serve solo al REST per i formati compressi o i WAV
  non PCM16/mono/16 kHz. Il percorso WebSocket e il PCM grezzo non lo usano.

## Installazione

```bash
uv sync
```

I modelli vengono scaricati al primo avvio (o al primo test gated) e restano
nella cache Hugging Face (`~/.cache/huggingface`). Il default è:

- STT: `mlx-community/parakeet-tdt-0.6b-v3`
- LLM: `mlx-community/Qwen3-1.7B-4bit`

Questi restano i modelli selezionati anche dopo il confronto STT locale del
26 ottobre 2026: quel confronto è **BLOCKED** (nessun corpus reale) e non ha
prodotto un vincitore, quindi non c'è stata alcuna promozione di backend.

## Avvio manuale

```bash
uv run python -m dictation_server.main
# equivalente, con config esplicita:
uv run python -m dictation_server.main --config ./config.yaml
```

Il server ascolta su `0.0.0.0:8000` (configurabile). Non accetta connessioni
finché STT e LLM non sono caricati, il system prompt non è congelato e il
warm-up non è terminato.

Health check:

```bash
curl -s http://127.0.0.1:8000/health
# {"status":"ok"}
```

`/health` è sempre pubblico, non esegue inferenza e serve solo a verificare
che il processo sia vivo.

### Readiness

```bash
curl -s http://127.0.0.1:8000/ready
# {"ready":true,"busy":false,"draining":false,"queue_depth":0,
#  "stt_model":"mlx-community/parakeet-tdt-0.6b-v3",
#  "llm_model":"mlx-community/Qwen3-1.7B-4bit"}
```

`/ready` risponde `200` solo dopo che il warm-up è completato e finché il
server non è in chiusura; durante l'avvio e lo shutdown risponde `503` con
`ready:false`. `busy` (una richiesta è in esecuzione), `draining` (un worker
è in fase di svuotamento) e `queue_depth` (job in coda) sono informativi e
non rendono `ready:false`. Le probe non avviano inferenza né modificano i
contatori di ammissione. Con l'autenticazione attiva `/ready` è protetto,
mentre `/health` resta pubblico.

## Autenticazione opzionale

L'autenticazione è disattivata finché `security.api_key_env` resta `null`.
Per attivarla, indicare il nome della variabile d'ambiente in `config.yaml`:

```yaml
security:
  api_key_env: "GANGIOKEET_API_KEY"
```

e avviare il server con la variabile valorizzata:

```bash
export GANGIOKEET_API_KEY="$(openssl rand -hex 32)"
uv run python -m dictation_server.main
```

Se il nome è configurato ma la variabile è assente o vuota, l'avvio fallisce
prima di caricare o bindare i modelli. La chiave non viene mai scritta nei log.

Con auth attiva `/health` resta pubblico; tutte le altre rotte HTTP e il
WebSocket richiedono l'header:

```bash
curl -s -X POST http://127.0.0.1:8000/v1/audio/transcriptions \
  -H "Authorization: Bearer $GANGIOKEET_API_KEY" \
  -F file=@audio.wav
```

```python
async with websockets.connect(
    "ws://127.0.0.1:8000/ws/dictate",
    additional_headers={
        "Authorization": f"Bearer {api_key}",
    },
) as ws:
    ...
```

L'ambiente di una shell non viene ereditato da launchd. Per il LaunchAgent,
esportare `GANGIOKEET_API_KEY` nella shell da cui si lancia l'installer:
`deploy/install_launchd.sh` legge il valore dall'ambiente e, solo se non è
vuoto, lo aggiunge al plist locale generato
(`~/Library/LaunchAgents/com.gangio.mlx-dictation.plist`, permessi `0600`).
Senza la variabile il campo è omesso. Il valore non viene mai stampato,
passato in argv o scritto nel template versionato; dopo l'installazione si
può fare `unset GANGIOKEET_API_KEY`.

## Accesso remoto via Tailscale

Il server ascolta su `0.0.0.0`, quindi è già raggiungibile anche
sull'interfaccia Tailscale del Mac senza modifiche al codice. Sul Mac:

```bash
tailscale ip -4
curl -s "http://$(tailscale ip -4):8000/health"
```

Sul client Windows, in `%APPDATA%\GangioKeet\client_config.yaml`, impostare
`server.host` all'IP Tailscale (o al nome MagicDNS) del Mac. Il traffico del
tailnet è cifrato da WireGuard, ma il server continua a non implementare TLS:
non esporlo su Internet. Con l'autenticazione attiva il token resta comunque
protetto dal tunnel; senza API key, qualunque dispositivo del tailnet può
usare STT e LLM. Il Mac deve essere sveglio e il processo in esecuzione: per
la sopravvivenza ai riavvii usare il LaunchAgent qui sotto.

## LaunchAgent

```bash
./deploy/install_launchd.sh
```

Per installare il LaunchAgent già in modalità registrazione:

```bash
GANGIOKEET_RECORDING=1 ./deploy/install_launchd.sh
```

La variabile viene usata solo dall'installer: aggiunge `--recording` agli
argomenti del plist locale. Senza la variabile la registrazione resta spenta;
per disattivarla basta rilanciare l'installer senza `GANGIOKEET_RECORDING`.

Lo script:

1. risolve `uv` dal `PATH`;
2. crea `~/Library/Logs/mlx-dictation/`;
3. sostituisce `__UV_PATH__`, `__HOME__` e `__PROJECT_DIR__` nel template e
   copia il plist in `~/Library/LaunchAgents/com.gangio.mlx-dictation.plist`;
4. rimuove un eventuale caricamento precedente e fa `bootstrap`, con
   fallback a `launchctl load`.

Log:

```bash
tail -f ~/Library/Logs/mlx-dictation/stdout.log
tail -f ~/Library/Logs/mlx-dictation/stderr.log
```

Il template usa `WorkingDirectory=__PROJECT_DIR__` e log in
`__HOME__/Library/Logs/mlx-dictation/`; `install_launchd.sh` sostituisce i
placeholder con la root del checkout e con `$HOME`. Il `PATH` include
`/opt/homebrew/bin` e `/usr/local/bin`, così `ffmpeg` è visibile anche sotto
launchd. Se il progetto viene spostato, basta rilanciare l'installer.

## Contratto WebSocket

Endpoint: `ws://<host>:8000/ws/dictate`.

Stati: `idle → recording → processing → idle`. Un `start` è accettato solo in
`idle`; `stop` solo in `recording`; `abort` in `recording` o `processing`.
Dopo `abort` il server cancella e drena il worker prima di accettare un nuovo
`start`, e non invia alcun `result` per la richiesta annullata.

### Messaggi client

```json
{"type": "start", "app_name": "...", "window_title": "...",
 "profile": "code", "selected_text": "", "bypass_llm": false}
```

`profile` è uno di `code`, `terminal`, `chat`, `email`, `document`, `default`.
`selected_text`, `app_name` e `window_title` sono solo metadati: non entrano
nel prompt del post-processore.

Audio: frame binari PCM16 signed little-endian, mono, 16 kHz. I chunk vengono
accodati in RAM; il limite per sessione è `limits.max_audio_bytes` (default
`64 MiB`). Un singolo frame oltre `ws_max_size` (lo stesso valore, passato a
uvicorn) viene chiuso dal layer WebSocket con codice 1009. Se è il buffer
cumulativo a superare il limite, il server invia
`{"type":"error","code":"payload_too_large"}` e chiude con codice 1009.

```json
{"type": "stop"}
{"type": "abort"}
```

### Risposte

```json
{"type": "result", "raw_text": "...", "final_text": "...",
 "fast_path": false, "warnings": [],
 "metrics": {"stt_ms": 123.4, "llm_ms": 456.7, "total_ms": 580.1}}
```

`warnings` è additivo: contiene codici stabili come `llm_timeout` o
`llm_failed` quando il post-processing fallisce e il server usa il testo
normalizzato. `fast_path` resta `false` dopo qualunque tentativo LLM, anche
se il risultato proviene dal fallback.

`fast_path` è `true` quando l'LLM è stato saltato: testo sotto la soglia,
nessun trigger di autocorrezione, nessun override fonetico, oppure
`bypass_llm: true`. In quel caso `metrics.llm_ms` è `0.0`. Gli errori di
protocollo non chiudono la connessione:

```json
{"type": "error", "code": "protocol", "message": "...", "retryable": false}
```

Audio binario fuori da `recording`, JSON non valido, messaggi sconosciuti e
transizioni di stato non valide producono `code="protocol"`. PCM con un
numero dispari di byte produce `code="invalid_audio"` senza chiamare gli
engine e la sessione torna `idle`. Un errore interno della pipeline produce
`code="internal"` con messaggio generico e riporta lo stato a `idle`.

Errori applicativi e codici di chiusura:

| Codice | `retryable` | Chiusura WS |
|---|---|---|
| `unauthorized` | `false` | `1008` prima di `start`/audio |
| `busy` | `true` | `1013` |
| `queue_timeout` | `true` | nessuna: la connessione resta riusabile |
| `processing_timeout` | `true` | `1013`; il drain continua in background |
| `invalid_audio` | `false` | nessuna: la connessione torna `idle` |
| `payload_too_large` | `false` | `1009` |
| `internal` | `false` | nessuna: messaggio generico |

Esempio Python:

```python
import asyncio, json, websockets

async def main():
    async with websockets.connect("ws://127.0.0.1:8000/ws/dictate") as ws:
        await ws.send(json.dumps({
            "type": "start", "app_name": "demo", "window_title": "demo",
            "profile": "chat", "bypass_llm": False,
        }))
        await ws.send(open("audio.pcm", "rb").read())
        await ws.send(json.dumps({"type": "stop"}))
        print(json.loads(await ws.recv()))

asyncio.run(main())
```

## REST OpenAI-compatible

```bash
curl -s -X POST http://127.0.0.1:8000/v1/audio/transcriptions \
  -F file=@audio.wav \
  -F model=mlx-community/parakeet-tdt-0.6b-v3 \
  -F response_format=json
```

Parametri supportati:

| Campo | Note |
|---|---|
| `file` | Obbligatorio (multipart). |
| `model` | Opzionale; deve corrispondere al modello STT configurato o al suo basename. `whisper-1` e altri alias non sono accettati. |
| `response_format` | `json` (default) o `text`. `srt`, `vtt` e `verbose_json` non sono supportati. |
| `profile` | Estensione locale; default `default`. |
| `format` | Estensione locale in query string. `format=pcm` dichiara PCM16 mono 16 kHz grezzo. |
| `language` | Accettato solo assente o vuoto (anche solo spazi, no-op); un valore non vuoto restituisce `400 unsupported_parameter`. |
| `prompt` | Accettato solo assente o vuoto (anche solo spazi, no-op); un valore non vuoto restituisce `400 unsupported_parameter`. |
| `temperature` | Accettato solo assente/vuoto oppure un valore numerico uguale a `0`; il letterale `null` e ogni altro valore restituiscono `400 unsupported_parameter`. |
| `stream` | Accettato assente oppure `false`, `0`, `no`, `off`; `true` restituisce `400 unsupported_parameter`. |
| `timestamp_granularities`, `timestamp_granularities[]` | Accettati solo assenti o vuoti; valori non vuoti restituiscono `400 unsupported_parameter`. |

I campi multipart sconosciuti restituiscono `400 unsupported_parameter`. I
parametri query sconosciuti vengono ignorati per compatibilità. Gli errori di
validazione dei tipi generati dal framework possono mantenere il formato
`detail` a lista di FastAPI.

Selezione del decoder:

- `format=pcm` → passthrough diretto dei byte.
- WAV PCM16, mono, 16 kHz → letto in RAM con `wave`, senza `ffmpeg`.
- Altri WAV (float32, 24 bit, stereo, sample rate diverso) e formati compressi
  (MP3, M4A, ...) → `ffmpeg` via pipe stdin/stdout, nessun file temporaneo.
- Il content type generico `application/octet-stream` non viene mai
  interpretato come PCM grezzo: serve `format=pcm`.
- `ffmpeg` assente per un formato che lo richiede → HTTP 503.
- Formato non decodificabile o WAV strutturalmente invalido → HTTP 422.

Risposta `json`:

```json
{"text": "Testo finale.", "raw_text": "testo grezzo",
 "fast_path": false, "warnings": [],
 "metrics": {"stt_ms": 123.4, "llm_ms": 456.7, "total_ms": 580.1}}
```

`warnings` è additivo e non cambia le chiavi storiche. `response_format=text`
restituisce `text/plain` con il solo `text`; se ci sono warning aggiunge
l'header `X-GangioKeet-Warnings` con i codici separati da virgola, mentre non
aggiunge nulla quando la lista è vuota.

Errori REST (oltre ai `422` di validazione del framework):

| Codice | HTTP | Note |
|---|---|---|
| `unauthorized` | `401` | Auth Bearer attiva. |
| `busy` | `503` | `Retry-After: 1`. |
| `queue_timeout` | `503` | `Retry-After: 1`. |
| `decode_timeout` | `504` | |
| `upload_timeout` | `408` | |
| `processing_timeout` | `504` | |
| `invalid_audio` | `422` | PCM malformato, mai `500`. |
| `payload_too_large` | `413` | |
| `unsupported_parameter` | `400` | |
| `decoder_unavailable` | `503` | |
| `internal` | `500` | Messaggio generico. |

Il corpo è sempre `{"detail": {"code": "...", "message": "...",
"retryable": true|false}}`.

## Configurazione

`config.yaml` (estratto: il file completo contiene 21 termini canonici e 13
override fonetici):

```yaml
server:
  host: "0.0.0.0"
  port: 8000
  stt_model: "mlx-community/parakeet-tdt-0.6b-v3"
  llm_model: "mlx-community/Qwen3-1.7B-4bit"

limits:
  max_audio_bytes: 67108864
  max_http_body_bytes: 68157440
  max_buffered_audio_bytes: 201326592
  max_sessions: 4
  max_pending_jobs: 2

timeouts:
  upload_seconds: 10.0
  decode_seconds: 5.0
  queue_seconds: 2.0
  processing_seconds: 10.0
  llm_seconds: 5.0

security:
  api_key_env: null  # oppure "GANGIOKEET_API_KEY"

recording:
  enabled: false        # oppure avvia con --recording
  directory: "recordings"
  store_context: false  # app_name/window_title; selected_text mai

routing:
  fast_path_max_words: 5
  self_correction_triggers: ["anzi", "scusa", "aspetta", "cioè", "o meglio", "volevo dire"]

glossary:
  canonical_terms: ["FastAPI", "Proxmox VE", "MLX", "WebSocket"]
  phonetic_overrides:
    "fast a pi": "FastAPI"
    "emme elle ics": "MLX"
```

- `routing.fast_path_max_words`: sotto questa soglia l'LLM viene saltato,
  salvo trigger o override fonetici presenti nel testo.
- `routing.self_correction_triggers`: frasi che forzano il post-processing.
- `glossary.canonical_terms` e `glossary.phonetic_overrides` vengono iniettati
  nel system prompt congelato a boot; cambiarli richiede un riavvio.
- Il file è validato con `extra="forbid"`: chiavi sconosciute sono errori.
- Tutti i valori numerici di `limits` e `timeouts` devono essere positivi;
  `max_audio_bytes` non può superare né `max_buffered_audio_bytes` né
  `max_http_body_bytes`.

### Limiti e timeout

I default di `config.yaml` sono:

| Impostazione | Default | Effetto |
|---|---:|---|
| `limits.max_audio_bytes` | 67.108.864 (64 MiB) | PCM massimo trattenuto per sessione WS e audio massimo accettato dal REST, incluso `format=pcm`. |
| `limits.max_http_body_bytes` | 68.157.440 (65 MiB) | Corpo HTTP massimo per `POST /v1/audio/transcriptions`. Controllato prima del multipart e, per richieste chunked, contando i byte ricevuti. |
| `limits.max_buffered_audio_bytes` | 201.326.592 (192 MiB) | Budget di audio trattenuto condiviso fra WebSocket e REST. Esaurito il budget, la richiesta fallisce con `busy`. |
| `limits.max_sessions` | 4 | Sessioni audio contemporanee (WS + REST). Oltre il limite: `busy`. |
| `limits.max_pending_jobs` | 2 | Job in coda oltre a quello attivo. Coda piena: `busy` immediato. |
| `timeouts.upload_seconds` | 10,0 | Tempo per ricevere il corpo REST. Allo scadere: `upload_timeout` (408). |
| `timeouts.decode_seconds` | 5,0 | Decodifica WAV/ffmpeg. Allo scadere: `decode_timeout` (504). |
| `timeouts.queue_seconds` | 2,0 | Attesa in coda prima dello start. Allo scadere: `queue_timeout` (503). |
| `timeouts.processing_seconds` | 10,0 | Limite rigido del job attivo, STT + LLM. Allo scadere: `processing_timeout` (504 lato REST, 1013 lato WS) e drain. |
| `timeouts.llm_seconds` | 5,0 | Deadline cooperativa del cleaner LLM. Allo scadere il server usa il testo normalizzato con warning `llm_timeout`, entro il limite di processing. |

Il limite del corpo HTTP è più alto di quello audio perché comprende
l'overhead multipart. Il PCM grezzo dichiarato con `format=pcm` resta comunque
soggetto a `max_audio_bytes`. `max_audio_bytes` è anche il `ws_max_size`
passato a uvicorn, quindi un frame oltre il limite viene rifiutato prima di
essere accodato.

## Test

Suite default, senza download né caricamento di modelli reali:

```bash
uv run pytest -q
```

Suite gated con Parakeet e Qwen reali (la prima esecuzione può scaricare i
pesi):

```bash
RUN_REAL_MODELS=1 uv run pytest tests/integration -v
```

La fixture `tests/fixtures/it_short.pcm` è una breve frase italiana sintetica
di circa 3,9 s (PCM16 mono 16 kHz). È stata generata su macOS 27.0.1 con la
voce italiana `Alice` e `ffmpeg`; versioni diverse della voce possono produrre
byte diversi. Per rigenerarla:

```bash
say -v Alice -r 230 -o /tmp/it_short.aiff \
  "Ehm, controlla lo stato del servizio sul server, anzi no, solo lo stato."
ffmpeg -y -i /tmp/it_short.aiff -f s16le -ar 16000 -ac 1 tests/fixtures/it_short.pcm
```

## Benchmark

```bash
uv run python scripts/benchmark_mac.py --samples 30 --warmups 2
```

Il benchmark avvia il server in-process (modelli reali, warm-up incluso),
invia il PCM a chunk via WebSocket e misura:

- `stop → result` con LLM e con `bypass_llm=true`;
- metriche interne `stt_ms`, `llm_ms`, `total_ms`;
- TTFT LLM: dal lancio di `generate_step` al primo token; include il prefill
  del suffisso utente ed esclude il clone della cache e il rendering del
  template, che restano dentro `llm_ms`;
- RSS dopo warm-up, dopo richieste brevi e dopo una richiesta lunga;
- picco di memoria Metal.

Esce con codice 1 se `p95 >= 400 ms`, TTFT `p95 >= 25 ms` oppure RSS
`>= 2,8 GB`. Con `--url ws://...` contatta un server già caldo: in quel caso
TTFT e dati di memoria del processo remoto (RSS e Metal) non sono
disponibili; `--rss-pid` abilita il solo campionamento RSS. Il default
in-process è il modo corretto per i gate.

### Risultati misurati

Run del **26 ottobre 2026**, server in-process dopo warm-up, 30 campioni per
modalità e 2 warm-up per modalità:

- MacBook Air Apple M1, 8 GB, macOS 27.0.1 (26A434), Python 3.12.15
- mlx 0.32.4, mlx-lm 0.32.2, parakeet-mlx 0.5.3
- commit di accettazione `568ad49`
- STT `mlx-community/parakeet-tdt-0.6b-v3`, LLM
  `mlx-community/Qwen3-1.7B-4bit`
- audio `tests/fixtures/it_short.pcm`: 3,90 s (124.848 byte), chunk da
  3200 byte
- caricamento modelli + freeze: 6,83 s; warm-up: 1,08 s

Questo è uno **smoke benchmark** su un singolo audio sintetico di 3,9 s: non è
il confronto sul corpus da 60 clip (che resta **BLOCKED**) e non misura
qualità, WER o RTF. I vecchi target di latenza restano **non raggiunti**.

| Metrica (ms) | p50 | p95 | max |
|---|---:|---:|---:|
| WebSocket `stop` → `result` (LLM) | 1041,4 | 1053,6 | 1059,8 |
| WebSocket `stop` → `result` (fast path) | 570,1 | 577,7 | 577,7 |
| Pipeline `total_ms` (LLM) | 1039,1 | 1051,0 | 1057,7 |
| STT `stt_ms` (modalità LLM) | 558,9 | 566,4 | 575,8 |
| LLM `llm_ms` (modalità LLM) | 479,3 | 489,0 | 499,0 |
| LLM TTFT (`generate_step` → primo token) | 228,5 | 233,2 | 235,0 |
| Pipeline `total_ms` (fast path) | 567,5 | 574,9 | 575,4 |
| STT `stt_ms` (fast path) | 558,3 | 563,2 | 565,5 |

Memoria:

- RSS misurata con `ps`: 297 MB dopo il warm-up, 74 MB dopo le richieste
  brevi, 76 MB dopo la richiesta lunga, picco 944 MB.
- Metal: picco 2986 MB dopo il warm-up, 3113 MB durante il benchmark,
  2234 MB attivi al termine. Su memoria unificata la RSS di `ps` non
  rappresenta la memoria realmente impegnata da MLX; il dato Metal è quello
  significativo. **RSS e Metal non vanno sommati.**
- Vecchio gate RSS `< 2,8 GB`: **PASS** con 944 MB.
- Il criterio proposto per un Mac dedicato (8 GiB − 2 GiB per l'OS = 6 GiB)
  resta **non verificato** come gate di footprint: i due picchi osservati
  separatamente sono sotto 6 GiB (RSS ~0,92 GiB, Metal ~3,04 GiB), ma non
  sono stati misurati footprint del processo, memory pressure o delta swap.

Gate della run:

| Gate | Esito | Valore |
|---|---|---|
| WebSocket p95 LLM < 400 ms | **FAIL** | 1053,6 ms |
| WebSocket p95 fast path < 400 ms | **FAIL** | 577,7 ms |
| RSS < 2,8 GB | **PASS** | 944 MB |
| LLM TTFT p95 < 25 ms | **FAIL** | 233,2 ms |

Il benchmark esce con **codice 1**. I target di latenza **non sono raggiunti su
questo hardware**: anche con un audio di 3,9 s lo STT ha un costo fisso di
circa 558–576 ms, e il TTFT misurato include il prefill del suffisso utente
(circa 77 token), non solo il riuso della KV cache del system prompt. Il
valore esclude il clone della cache e il rendering del template, conteggiati
in `llm_ms`. Il fast path elimina l'LLM ma resta dominato dallo STT. I numeri
sopra sono quelli reali, senza escludere il prefill o cambiare la definizione
della metrica.

L'accettazione locale dell'affidabilità è **PASS per il contratto e i test di
breve durata**: suite default `387 passed, 7 skipped`, integrazione con modelli
reali `7 passed`. Lo **smoke lungo con modelli caldi non è stato eseguito**.
Il report completo, inclusi blocker e procedura di rollback, è mantenuto
internamente e non è incluso nel repository pubblico.
Deploy e smoke Windows **non sono stati eseguiti**.

## Benchmark pubblico italiano (STT + cleaner)

Questa sezione è separata dal benchmark di produzione qui sopra. Serve a
confrontare modelli STT e cleaner su **parlato pubblico italiano**, senza
registrare clip dell'utente. È uno screening, non una promozione del modello:
non copre sistematicamente termini tecnici, identificatori, path, numeri,
autocorrezioni, silenzi o il dominio reale. Sette clip su 30 contengono
incidentalmente date o misure; la dettatura numerica/tecnica reale resta da
testare.

Il report completo è mantenuto internamente e non è incluso nel repository
pubblico.

### Setup isolato

```bash
uv venv .venv-stt-eval --python 3.12
uv pip install --python .venv-stt-eval/bin/python \
  -e . -r scripts/stt_eval_requirements.txt
```

`scripts/stt_eval_requirements.txt` è separato dal lock di produzione. Usa
`datasets==3.6.0` perché `datasets` 5 richiede `torchcodec`; non modifica
`pyproject.toml` né `uv.lock`.

### Corpus FLEURS italiano

```bash
.venv-stt-eval/bin/python scripts/stt_eval_ingest_fleurs.py \
  --output-dir tests/fixtures/stt_eval/public/fleurs_it \
  --count 30 --seed 20261026 --split test \
  --revision 70bb2e84b976b7e960aa89f1c648e09c59f894dd
```

Il manifest e la provenienza sono committati; `audio/` è ignorata da git.
Il campione è di 30 registrazioni (21 frasi uniche; 15 corte + 15 medie),
licenza FLEURS CC-BY-4.0. La selezione è deterministica (lowest-id per
genere/durata), non casuale; il seed rompe solo i pareggi di durata. Le
revisioni dei modelli sono in `model_revisions.json`: gli adapter risolvono lo
snapshot cachato di quella revisione e falliscono se manca.

### Esecuzione

Fermare il server live prima del run se tiene già i modelli in memoria.

```bash
# Phase A: STT raw
.venv-stt-eval/bin/python scripts/stt_eval.py \
  --manifest tests/fixtures/stt_eval/public/fleurs_it/manifest.jsonl \
  --candidate <stt_id> --warmups 2 --repeats 3 \
  --output .superpowers/stt-benchmark/raw/<stt_id>.json

# Phase B: cleaner sui transcript raw
.venv-stt-eval/bin/python scripts/stt_eval_cleaners.py \
  --raw .superpowers/stt-benchmark/raw/<stt_id>.json \
  --manifest tests/fixtures/stt_eval/public/fleurs_it/manifest.jsonl \
  --cleaner <cleaner_id> \
  --output .superpowers/stt-benchmark/cleaners/<cleaner_id>__<stt_id>.json

# Analisi riproducibile (WER, CI clip e cluster, latenza, memoria)
python3 scripts/stt_eval_analyze.py \
  --raw-dir .superpowers/stt-benchmark/raw \
  --cleaners-dir .superpowers/stt-benchmark/cleaners \
  --manifest tests/fixtures/stt_eval/public/fleurs_it/manifest.jsonl \
  --output .superpowers/stt-benchmark/summary.json
```

STT disponibili: `parakeet_baseline`, `qwen3_asr_1_7b_8bit`,
`nemotron_3_5_asr_streaming_0_6b`, `whisper_large_v3_turbo`.
Cleaner disponibili: `qwen3_1_7b_4bit`, `qwen2_5_1_5b_instruct_4bit`.
`cohere_transcribe_03_2026` è gated su Hugging Face e `qwen3_5_0_8b_4bit` non
è compatibile con il freeze attuale: entrambi sono `NOT RUN` nel report.

### Risultati sintetici (30 registrazioni FLEURS, M1/8 GB)

| STT | WER raw | p50/p95 ms | Peak RSS/Metal MB |
|---|---:|---:|---:|
| Parakeet 0.6B | 3,88% | 849 / 1060 | 733 / 2128 |
| Qwen3-ASR 1.7B 8bit | **2,01%** | 2251 / 3343 | 1803 / 3415 |
| Nemotron 0.6B | 7,75% | 2867 / 4006 | 1375 / 1940 |
| Whisper turbo fp16 | 2,81% | 1868 / 2077 | 1279 / 2043 |

RSS e Metal non si sommano; il picco Metal include load e warm-up.

I cleaner peggiorano il parlato letto già pulito: Qwen3-1.7B porta Parakeet
da 3,88% a 8,42% e Qwen3-ASR da 2,01% a 9,89%; Qwen2.5 è peggiore su tre STT
su quattro (è leggermente meglio su Whisper). Poiché tutte le clip FLEURS
hanno 16–38 parole, la production non attiva mai il fast-path: production e
forced-cleaner coincidono. Non cambiare il cleaner di produzione basandosi
solo su questo corpus.

## Registrazione della propria voce (opt-in)

La registrazione è **spenta di default** e non salva nulla. Per raccogliere un
corpus delle proprie dettature:

```bash
uv run python -m dictation_server.main --recording
# equivalente: recording.enabled: true in config.yaml
```

Per farla partire anche al boot tramite LaunchAgent:

```bash
GANGIOKEET_RECORDING=1 ./deploy/install_launchd.sh
```

Ogni dettatura completata (WebSocket o REST) scrive:

- `recordings/audio/<id>.wav`: PCM16 mono 16 kHz, permessi `0600`;
- una riga in `recordings/manifest.jsonl`: testo `raw_text`/`final_text`,
  profilo, trasporto, durata, modelli, metriche e SHA-256 dell'audio.

Il manifest è già nel formato del corpus di `scripts/stt_eval.py`.
`reference_raw` e `reference_final` partono vuoti e vanno annotati a mano:
finché restano vuoti la raccolta non produce un WER. Dopo l'annotazione:

```bash
.venv-stt-eval/bin/python scripts/stt_eval.py \
  --manifest recordings/manifest.jsonl \
  --candidate parakeet_baseline --warmups 2 --repeats 3 \
  --output .superpowers/stt-benchmark/raw/parakeet_baseline.json
```

`selected_text` non viene mai salvato; `app_name` e `window_title` solo con
`recording.store_context: true`. La directory è `0700`, i file sono `0600`.
Le dettature annullate o fallite non vengono registrate e un errore di
scrittura non fa fallire la dettatura. Non c'è pulizia automatica: la
retention è manuale. `recordings/` è ignorata da git.

## Limiti di compatibilità

- Il server è pensato per la rete locale: l'autenticazione Bearer è opzionale
  e, se attiva, il token viaggia in chiaro su HTTP. Non implementa TLS, rate
  limiting o multi-tenancy; usare una rete fidata o un reverse proxy TLS.
- Il client Windows supporta `server.scheme: https` per usare `https://` e
  `wss://` dietro un terminatore TLS (reverse proxy o tunnel). Con `http`
  (default), token, audio e titoli delle finestre viaggiano in chiaro.
- Un solo processo e un solo lock Metal: le richieste concorrenti vengono
  serializzate da uno scheduler FIFO a worker singolo. La coda e il budget
  audio in byte sono condivisi fra WebSocket e REST; `limits` e `timeouts`
  nel file di configurazione limitano sessioni, coda e durate.
- Lo scheduler rende visibile il timeout e impedisce l'overlap, ma non
  guarisce un kernel bloccato per sempre: se il kernel non ritorna, il job va
  in `processing_timeout`, il servizio resta `busy`/`draining` e `/health`
  continua a rispondere; serve un riavvio operativo.
- I log contengono solo codici, durate, byte, profondità di coda e stato di
  drain: testo dettato, prompt, nomi file, titoli delle finestre e token non
  vengono mai registrati. Audio e trascrizioni finiscono su disco solo quando
  la modalità `--recording` è attiva.
- L'audio di input deve essere PCM16 mono 16 kHz; il REST converte solo se
  `ffmpeg` è disponibile.
- Il campo `model` del REST deve corrispondere al modello STT configurato;
  non è un proxy per modelli OpenAI.
- Nessun streaming parziale dei risultati: il client riceve un solo
  `result` dopo `stop` (o nessun risultato dopo `abort`).
- I target di latenza sono gate sperimentali e dipendono da chip, RAM,
  modello e durata della dettatura; vedere la sezione misurata.

## Licenza

MIT — vedi [LICENSE](LICENSE). Le trascrizioni FLEURS incluse nel manifest
restano sotto CC-BY-4.0; vedi [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
