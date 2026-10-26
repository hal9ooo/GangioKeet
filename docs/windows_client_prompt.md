# Avvio Sviluppo Client Windows per GangioKeet

Copia il testo sottostante e incollalo all'agente nella sessione di lavoro sull'host Windows per far partire il flusso completo di brainstorming e pianificazione:

---

```markdown
Voglio implementare il client Windows per la dettatura vocale collegato al server GangioKeet (su macOS Apple Silicon).

Fai riferimento alla specifica dettagliata contenuta in:
`docs/superpowers/specs/windows_client_blueprint.md`

Caratteristiche chiave da implementare:
1. Client Windows stile "Wispr Flow": Push-to-Talk a latenza minima con streaming audio su WebSocket (`ws://<MAC_IP>:8000/ws/dictate`) e fallback REST OpenAI-compatible (`POST /v1/audio/transcriptions`).
2. Context-Awareness nativo Windows: identificazione della finestra attiva in primo piano (eseguibile e titolo) e mappatura automatica del profilo di dettatura (`code`, `terminal`, `chat`, `email`, `document`, `default`).
3. Cattura audio microfono 16 kHz mono in RAM e iniezione testo via Win32 SendInput / Clipboard con backup/restore.
4. Feedback visivo discreto (floating pill / capsule borderless) con indicatore di stato (Recording, Processing, Error).

Workflow richiesto:
1. Attiva la skill `brainstorming` per chiarire requisiti specifici, stack tecnologico (Python con uv/pywin32 vs C# .NET) e preferenze di UX.
2. Terminato il brainstorming, attiva la skill `writing-plans` per produrre il piano di implementazione dettagliato con task verificabili.
3. Attendi la mia approvazione del piano prima di scrivere il codice.
```
