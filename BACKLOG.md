# Backlog

<!-- sprint-flow:regole (le aggiorna il timer notturno di sprint-flow: non modificarle qui) -->
Le voci stanno in quattro classi. L'**importanza** decide l'ordine: alta =
correttezza, perdita di dati, sicurezza, installazione bloccata, errori visibili
a chi usa il progetto; poi la comodità di chi ci lavora, poi l'estetica. La
**complessità** decide il modo: **(S)** fino a mezza giornata, **(M)** 1-2
giorni, **(L)** serve un piano o un ramo dedicato.

- **Prioritario**: importanza alta, complessità S. Si fa subito, anche dentro il lavoro in corso.
- **Importante**: importanza alta, complessità M o L. Va messo in calendario.
- **Minore**: importanza bassa, complessità S o M. A pacchetti, o quando si tocca quel codice.
- **Da valutare**: importanza bassa o incerta e complessità L, oppure decisione di altri o dato che manca. Parcheggiato: si rivede o si chiude.

Dentro una classe le aree con più voci vengono prima; dentro un'area prima le
più importanti e, a parità, le più semplici. «(verifica dell'utente)» indica una
prova da fare a mano.

### Come aggiungere o chiudere una voce (anche per gli agenti)

1. Ogni voce nuova va nella classe giusta e sotto l'area `###` esistente più vicina: mai in una sezione datata in fondo al file. Se la classe è incerta, va in «Da valutare».
2. Formato: testo autosufficiente con i nomi esatti di file, classi e comandi, poi la complessità `(S)`, `(M)` o `(L)` e l'origine con la data fra parentesi, es. «(review del piano X, 13/11)».
3. Un'area `###` nuova solo se nessuna di quelle esistenti va bene.
4. **Una voce fatta si toglie da qui nello stesso commit del lavoro.** Il changelog la riporta da solo, con data e agente.
5. Mentre si aggiunge una voce non si riordinano né si riscrivono le voci degli altri.
<!-- sprint-flow:regole-fine -->

## Prioritario

### Dettatura per gli strumenti degli agenti

- Invio automatico tenendo Ctrl al rilascio di Caps Lock (`auto_enter_delay_ms` in `ClientConfig`; `auto_enter` resta solo per compatibilità con i file di configurazione vecchi): completare e committare. (S) (lavoro non committato nel checkout, 13/11)
- Profilo «chat» esteso a WhatsApp desktop e alle chat AI (Claude, ChatGPT, Perplexity, DeepSeek, Gemini, Copilot) in `ClientConfig`: completare e committare. (S) (lavoro non committato nel checkout, 13/11)

## Importante

### Dettatura per gli strumenti degli agenti

- Punteggiatura con un modello ONNX (`OnnxPunctuationCleaner`, test `OnnxPunctuationCleanerTests` e `OnnxPunctuationIntegrationTests`): completare, misurare la latenza e committare. (M) (lavoro non committato nel checkout, 13/11)
- `POST /v1/dictation` valida solo il ramo multipart: con l'audio nel corpo binario, `?model=whisper-1` e `?language=it` nella query string vengono ignorati e la risposta è 200, quindi il chiamante crede di aver scelto un modello. Stessa regola del resto del server: rifiutare, mai ignorare. Riusare `DictationContract` sul ramo non-multipart, con test. (S) (review finale server API, 27/11)

## Minore

### Dettatura per gli strumenti degli agenti

- `DictationControllerTests.ResultWithUipiBlockedAtPaste_OffersRecoveryWithoutPasting` è instabile: con la suite intera fallisce circa una volta su quattro, da solo passa sempre (3/3). Trovare la condivisione di stato tra test, non allentare l'asserzione. (S) (pipeline hardening, 26/11)
- `DictationControllerTests.Success_RecordsTelemetryForActiveBackend` è instabile con la suite intera (visto una volta su tre giri del 27/11, verde da solo e al giro dopo): il test legge `TelemetryStore.Snapshot()` appena lo stato torna `Idle`, e la telemetria può essere scritta subito dopo. Aspettare la condizione di telemetria invece dello stato, come fanno i test vicini. (S) (final fix server API, 27/11)
- `SettingsWindow._onModelPairSaved` è assegnato e mai invocato: il percorso impostazioni applica la scelta con `ApplyModelSelectionAsync`, quindi `App.SwitchLocalModelPairAsync` è codice morto. O si collega, o si toglie col campo. (S) (review finale server API, 27/11)

## Da valutare

- Il `cleanup.yaml` installato in `%LOCALAPPDATA%\Programs\GangioKeet\` ha 57 righe contro le 103 di `client-windows/cleanup.example.yaml`: mancano glossario tecnico e correzioni contestuali, quindi l'app installata non applica quelle correzioni. Serve una procedura di aggiornamento della policy a installazione avvenuta. (M) (pipeline hardening, 26/11)
- `ApiConfig.Port` documentato come "0 = porta scelta dal sistema" nel server, ma `ConfigLoader.ValidateApi` rifiuta ogni porta sotto 1024: quel valore funziona solo nei test, che costruiscono `ApiConfig` a mano. Decidere se ammettere 0 anche da file, o togliere la promessa dalla doc. (S) (review finale server API, 27/11)

- Riconoscere dal contesto i nomi ambigui che il glossario non puo risolvere (`Cloud` vs `Claude`, `Brain`): serve un resolver contestuale addestrato su `reference_final`, oggi tutte le 779 righe sono `unreviewed`. (L) (analisi delle dettature, 13/11)
