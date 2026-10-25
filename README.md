# MLX Dictation Server

Demone locale per macOS Apple Silicon che tiene in memoria un modello STT
Parakeet e un LLM quantizzato MLX, ed espone una pipeline di dettatura su
WebSocket con fallback REST compatibile con l'API OpenAI Audio.

- Un solo processo, una sola `asyncio.Lock`: STT e LLM non usano Metal
  contemporaneamente.
- KV cache del system prompt congelata a boot e riusata a ogni richiesta.
- Warm-up di entrambi gli engine prima del bind HTTP.
- Nessun file temporaneo nel percorso principale: il PCM viaggia in RAM.
- `mx.clear_cache()` dopo ogni richiesta completata, annullata o fallita.

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
 "fast_path": false,
 "metrics": {"stt_ms": 123.4, "llm_ms": 456.7, "total_ms": 580.1}}
```

`fast_path` è `true` quando l'LLM è stato saltato (testo corto, nessun trigger,
oppure `bypass_llm: true`). Gli errori di protocollo non chiudono la
connessione:

```json
{"type": "error", "code": "protocol", "message": "..."}
```

Audio binario fuori da `recording`, JSON non valido, messaggi sconosciuti e
transizioni di stato non valide producono `code="protocol"`. Un errore interno
della pipeline produce `code="internal"` e riporta lo stato a `idle`.

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
 "fast_path": false,
 "metrics": {"stt_ms": 123.4, "llm_ms": 456.7, "total_ms": 580.1}}
```

`response_format=text` restituisce `text/plain` con il solo `text`.

## Configurazione

`config.yaml`:

```yaml
server:
  host: "0.0.0.0"
  port: 8000
  stt_model: "mlx-community/parakeet-tdt-0.6b-v3"
  llm_model: "mlx-community/Qwen3-1.7B-4bit"

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
di circa 3,9 s (PCM16 mono 16 kHz). È stata generata con la voce macOS
italiana e `ffmpeg`; per rigenerarla:

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
- TTFT LLM (tempo al primo token, prefill del suffisso incluso);
- RSS dopo warm-up, dopo richieste brevi e dopo una richiesta lunga;
- picco di memoria Metal.

Esce con codice 1 se `p95 >= 400 ms`, TTFT `p95 >= 25 ms` oppure RSS
`>= 2,8 GB`. Con `--url ws://...` contatta un server già caldo: in quel caso
TTFT e RSS del processo remoto non sono disponibili (usare `--rss-pid` per il
solo RSS). Il default in-process è il modo corretto per i gate.

### Risultati misurati

Run del 25 ottobre 2026, server in-process dopo warm-up, 30 campioni per
modalità e 2 warm-up per modalità:

- MacBook Air Apple M1, 8 GB, macOS 27.0.1, Python 3.12.15
- mlx 0.32.4, mlx-lm 0.32.2, parakeet-mlx 0.5.3
- STT `mlx-community/parakeet-tdt-0.6b-v3`, LLM
  `mlx-community/Qwen3-1.7B-4bit`
- audio `tests/fixtures/it_short.pcm`: 3,90 s (124.848 byte), chunk da
  3200 byte
- warm-up: 1,71 s

| Metrica (ms) | p50 | p95 | max |
|---|---:|---:|---:|
| WebSocket `stop` → `result` (LLM) | 1196,0 | 1268,4 | 1271,1 |
| WebSocket `stop` → `result` (fast path) | 647,9 | 674,0 | 686,3 |
| Pipeline `total_ms` (LLM) | 1188,6 | 1260,7 | 1263,6 |
| STT `stt_ms` (modalità LLM) | 641,6 | 692,2 | 697,0 |
| LLM `llm_ms` (modalità LLM) | 547,5 | 572,0 | 572,9 |
| LLM TTFT, prefill del suffisso incluso | 257,0 | 265,9 | 274,2 |
| Pipeline `total_ms` (fast path) | 644,9 | 671,0 | 681,8 |
| STT `stt_ms` (fast path) | 631,6 | 658,9 | 666,6 |

Memoria:

- RSS misurata con `ps`: 286 MB dopo il warm-up, 61 MB dopo le richieste
  brevi, 46 MB dopo la richiesta lunga, picco 935 MB.
- Metal: picco 2986 MB dopo il warm-up, 3113 MB durante il benchmark,
  2234 MB attivi al termine. Su memoria unificata la RSS di `ps` non
  rappresenta la memoria realmente impegnata da MLX; il dato Metal è quello
  significativo.

Gate della run:

| Gate | Esito | Valore |
|---|---|---|
| WebSocket p95 LLM < 400 ms | **FAIL** | 1268,4 ms |
| WebSocket p95 fast path < 400 ms | **FAIL** | 674,0 ms |
| RSS < 2,8 GB | **PASS** | 935 MB |
| LLM TTFT p95 < 25 ms | **FAIL** | 265,9 ms |

Il benchmark esce con codice 1. I target di latenza **non sono raggiunti su
questo hardware**: anche con un audio di 3,9 s lo STT ha un costo fisso di
circa 630–690 ms, e il TTFT misurato include il prefill del suffisso utente
(circa 77 token), non solo il riuso della KV cache del system prompt. Il
fast path elimina l'LLM ma resta dominato dallo STT. I numeri sopra sono
quelli reali, senza escludere il prefill o cambiare la definizione della
metrica.

## Limiti di compatibilità

- Il server è pensato per la rete locale: non implementa autenticazione,
  TLS, rate limiting o multi-tenancy.
- Un solo processo e un solo lock Metal: le richieste concorrenti vengono
  serializzate.
- L'audio di input deve essere PCM16 mono 16 kHz; il REST converte solo se
  `ffmpeg` è disponibile.
- Il campo `model` del REST deve corrispondere al modello STT configurato;
  non è un proxy per modelli OpenAI.
- Nessun streaming parziale dei risultati: il client riceve un solo
  `result` dopo `stop` (o nessun risultato dopo `abort`).
- I target di latenza sono gate sperimentali e dipendono da chip, RAM,
  modello e durata della dettatura; vedere la sezione misurata.
