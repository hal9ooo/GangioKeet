# MLX Dictation Server

Demone locale per macOS Apple Silicon che tiene in memoria un modello STT
Parakeet e un LLM quantizzato MLX, ed espone una pipeline di dettatura su
WebSocket con fallback REST compatibile con l'API OpenAI Audio.

- Un solo processo, una sola `asyncio.Lock`: STT e LLM non usano Metal
  contemporaneamente.
- KV cache del system prompt congelata a boot e riusata a ogni richiesta.
- Warm-up di entrambi gli engine prima del bind HTTP.
- Nessun file temporaneo nel percorso principale: il PCM viaggia in RAM.
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

## LaunchAgent

```bash
./deploy/install_launchd.sh
```

Lo script:

1. risolve `uv` dal `PATH`;
2. crea `~/Library/Logs/mlx-dictation/`;
3. sostituisce `__UV_PATH__` nel template e copia il plist in
   `~/Library/LaunchAgents/com.gangio.mlx-dictation.plist`;
4. rimuove un eventuale caricamento precedente e fa `bootstrap`, con
   fallback a `launchctl load`.

Log:

```bash
tail -f ~/Library/Logs/mlx-dictation/stdout.log
tail -f ~/Library/Logs/mlx-dictation/stderr.log
```

Il template usa `WorkingDirectory=/Users/gangio75/GangioKeet` e un `PATH` che
include `/opt/homebrew/bin` e `/usr/local/bin`, così `ffmpeg` è visibile anche
sotto launchd. Se il progetto viene spostato, aggiornare il plist.

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
accodati in RAM; il limite complessivo è `64 MiB`. Oltre il limite il server
invia `{"type":"error","code":"payload_too_large"}` e chiude con codice 1009.

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
| `language` | Accettato solo assente o vuoto (no-op); un valore non vuoto restituisce `400 unsupported_parameter`. |
| `prompt` | Accettato solo assente o vuoto (no-op); un valore non vuoto restituisce `400 unsupported_parameter`. |
| `temperature` | Accettato assente/`null` o `0`; qualunque altro valore restituisce `400 unsupported_parameter`. |
| `stream` | Accettato assente/`false`; `true` restituisce `400 unsupported_parameter`. |
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

`config.yaml` (estratto: il file completo contiene 20 termini canonici e 13
override fonetici):

```yaml
server:
  host: "0.0.0.0"
  port: 8000
  stt_model: "mlx-community/parakeet-tdt-0.6b-v3"
  llm_model: "mlx-community/Qwen3-1.7B-4bit"

security:
  api_key_env: null  # oppure "GANGIOKEET_API_KEY"

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

Run del 25 ottobre 2026, server in-process dopo warm-up, 30 campioni per
modalità e 2 warm-up per modalità:

- MacBook Air Apple M1, 8 GB, macOS 27.0.1, Python 3.12.15
- mlx 0.32.4, mlx-lm 0.32.2, parakeet-mlx 0.5.3
- STT `mlx-community/parakeet-tdt-0.6b-v3`, LLM
  `mlx-community/Qwen3-1.7B-4bit`
- audio `tests/fixtures/it_short.pcm`: 3,90 s (124.848 byte), chunk da
  3200 byte
- caricamento modelli + freeze: 7,34 s; warm-up: 1,60 s

| Metrica (ms) | p50 | p95 | max |
|---|---:|---:|---:|
| WebSocket `stop` → `result` (LLM) | 1122,4 | 1166,2 | 1247,0 |
| WebSocket `stop` → `result` (fast path) | 625,0 | 637,0 | 644,6 |
| Pipeline `total_ms` (LLM) | 1120,6 | 1156,7 | 1237,9 |
| STT `stt_ms` (modalità LLM) | 595,5 | 614,8 | 670,6 |
| LLM `llm_ms` (modalità LLM) | 518,9 | 541,5 | 566,7 |
| LLM TTFT (`generate_step` → primo token) | 247,8 | 262,4 | 265,7 |
| Pipeline `total_ms` (fast path) | 621,7 | 634,0 | 640,9 |
| STT `stt_ms` (fast path) | 602,3 | 615,5 | 636,1 |

Memoria:

- RSS misurata con `ps`: 204 MB dopo il warm-up, 50 MB dopo le richieste
  brevi, 52 MB dopo la richiesta lunga, picco 699 MB.
- Metal: picco 2986 MB dopo il warm-up, 3113 MB durante il benchmark,
  2234 MB attivi al termine. Su memoria unificata la RSS di `ps` non
  rappresenta la memoria realmente impegnata da MLX; il dato Metal è quello
  significativo.

Gate della run:

| Gate | Esito | Valore |
|---|---|---|
| WebSocket p95 LLM < 400 ms | **FAIL** | 1166,2 ms |
| WebSocket p95 fast path < 400 ms | **FAIL** | 637,0 ms |
| RSS < 2,8 GB | **PASS** | 699 MB |
| LLM TTFT p95 < 25 ms | **FAIL** | 262,4 ms |

Il benchmark esce con codice 1. I target di latenza **non sono raggiunti su
questo hardware**: anche con un audio di 3,9 s lo STT ha un costo fisso di
circa 600–615 ms, e il TTFT misurato include il prefill del suffisso utente
(circa 77 token), non solo il riuso della KV cache del system prompt. Il
valore esclude il clone della cache e il rendering del template, conteggiati
in `llm_ms`. Il fast path elimina l'LLM ma resta dominato dallo STT. I numeri
sopra sono quelli reali, senza escludere il prefill o cambiare la definizione
della metrica.

## Limiti di compatibilità

- Il server è pensato per la rete locale: l'autenticazione Bearer è opzionale
  e, se attiva, il token viaggia in chiaro su HTTP. Non implementa TLS, rate
  limiting o multi-tenancy; usare una rete fidata o un reverse proxy TLS.
- Un solo processo e un solo lock Metal: le richieste concorrenti vengono
  serializzate da uno scheduler FIFO a worker singolo. La coda e il budget
  audio in byte sono condivisi fra WebSocket e REST; `limits` e `timeouts`
  nel file di configurazione limitano sessioni, coda e durate.
- I log contengono solo codici, durate, byte, profondità di coda e stato di
  drain: testo dettato, prompt, nomi file, titoli delle finestre e token non
  vengono mai registrati.
- L'audio di input deve essere PCM16 mono 16 kHz; il REST converte solo se
  `ffmpeg` è disponibile.
- Il campo `model` del REST deve corrispondere al modello STT configurato;
  non è un proxy per modelli OpenAI.
- Nessun streaming parziale dei risultati: il client riceve un solo
  `result` dopo `stop` (o nessun risultato dopo `abort`).
- I target di latenza sono gate sperimentali e dipendono da chip, RAM,
  modello e durata della dettatura; vedere la sezione misurata.
