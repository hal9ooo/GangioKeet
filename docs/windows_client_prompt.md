# Avvio Sviluppo Client Windows per GangioKeet

Copia il testo sottostante e incollalo all'agente nella sessione di lavoro sull'host Windows per far partire il flusso completo di brainstorming e pianificazione:

---

```markdown
Voglio implementare il client Windows per la dettatura vocale collegato al server GangioKeet (su macOS Apple Silicon).

Fai riferimento alla specifica dettagliata contenuta in:
`docs/superpowers/specs/windows_client_blueprint.md`

### Contesto del backend (Già implementato e pronto su Mac):
- Il backend `GangioKeet` è un demone Python nativo completamente implementato, testato (99 unit test) e funzionante su macOS Metal.
- Engine STT: `mlx-community/parakeet-tdt-0.6b-v3` (bfloat16, local attention 256x256).
- Engine LLM: `mlx-community/Qwen3-1.7B-4bit` con KV cache del system prompt prefillata e congelata a boot per TTFT < 25ms.
- Pipeline in RAM: riceve audio PCM 16kHz mono, fast-path per frasi brevi, serializzazione GPU Metal via singolo asyncio.Lock.
- API esposte:
  * WebSocket primario: `ws://<MAC_IP>:8000/ws/dictate` (streaming chunk PCM, controllo `start`/`stop`/`abort`, risposta `result`).
  * REST fallback: `POST http://<MAC_IP>:8000/v1/audio/transcriptions` (compatibile OpenAI Audio con form-data `model="parakeet-tdt-0.6b-v3"` e parametro esteso `profile`).

### Caratteristiche chiave da implementare sul client Windows:
1. Client Windows stile "Wispr Flow": Push-to-Talk a latenza minima con streaming audio su WebSocket (`ws://<MAC_IP>:8000/ws/dictate`) e fallback trasparente REST OpenAI-compatible (`POST /v1/audio/transcriptions`).
2. Context-Awareness nativo Windows: identificazione della finestra attiva in primo piano (eseguibile e titolo) e mappatura automatica del profilo di dettatura (`code`, `terminal`, `chat`, `email`, `document`, `default`).
3. Cattura audio microfono 16 kHz mono in RAM e iniezione testo via Win32 SendInput / Clipboard con backup/restore.
4. Feedback visivo discreto (floating pill / capsule borderless) con indicatore di stato (Recording, Processing, Error).

### Workflow richiesto:
1. Attiva la skill `brainstorming` per chiarire requisiti specifici, stack tecnologico (Python con uv/pywin32 vs C# .NET) e preferenze di UX.
2. Terminato il brainstorming, attiva la skill `writing-plans` per produrre il piano di implementazione dettagliato con task verificabili.
3. Attendi la mia approvazione del piano prima di scrivere il codice.
```
