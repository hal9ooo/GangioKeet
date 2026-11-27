# Progetto ritirato

**Stato: archiviato. Non più mantenuto.**

ChattyNeko / GangioKeet è stato sviluppato e misurato fino al 27 novembre 2026,
poi ritirato a favore di **Hex**, che copre lo stesso bisogno ed è mantenuto
attivamente.

Non è un ritiro per insuccesso: il programma funziona, i test passano e le
misure sono documentate. È un ritiro per evitare di mantenere due dettatori
locali che fanno la stessa cosa con la stessa qualità.

## Perché

Il confronto è in [`benchmarks/hex-vs-chattyneko-2026-11-27/`](benchmarks/hex-vs-chattyneko-2026-11-27/README.md),
sugli stessi 35 file audio con testo di riferimento:

| Configurazione | WER |
|---|---:|
| ChattyNeko — Parakeet F16 | 3,30% |
| Hex — Parakeet F16 (importato) | 3,68% |
| Hex — Parakeet Q8 (attivo) | 3,93% |

Su 788 parole, 0,6 punti di differenza sono circa 5 parole: rumore. Confrontando
lo stesso modello F16 nei due programmi, gli output sono identici su 34 clip su
35.

Il glossario deterministico — la parte più rifinita del progetto — su 35
dettature lunghe sarebbe intervenuto 5 volte, tutte di sola maiuscola. Era una
toppa per modelli più deboli: con Parakeet non ha più errori da correggere.

## Cosa era stato costruito

- **Dettatura locale Windows**: push-to-talk, modelli selezionabili, nessun
  cloud, nessun abbonamento.
- **Pipeline deterministica**: rimozione filler, glossario tecnico con regole
  contestuali, correzioni di elisione, guardie contro allucinazioni su silenzio
  e contro cleaner che traducono o svuotano il testo.
- **Server HTTP OpenAI-compatibile** dentro lo stesso processo:
  `/v1/audio/transcriptions`, `/v1/dictation`, `/v1/correct`, `/v1/models`,
  `/health`; token obbligatorio su indirizzo di rete; coda serializzata;
  firewall limitato alla tailnet.
- **Telemetria delle dettature** con manifest, analisi del corpus e replay per
  confrontare modelli e pulitori.

Test automatizzati al momento del ritiro: **180** nel progetto Inference,
**472** nel progetto Client, tutti verdi.

## Cosa resta valido

- Il **server OpenAI-compatibile** è indipendente da Hex: se serve dettare da
  altre macchine della rete verso un endpoint compatibile, questo codice lo fa.
  Richiede ChattyNeko in esecuzione.
- La **documentazione del server** in [`api-server.md`](api-server.md).
- Le **misure** in `benchmarks/`, che restano il motivo del ritiro.

## Cosa non deve stare in questo repository

Il repository è pubblico. Non contengono e non devono contenere:

- il token dell'API, che vive solo nella variabile d'ambiente dell'operatore;
- le registrazioni audio e i testi dettati, che restano sulla macchina
  dell'operatore in `%LOCALAPPDATA%\ChattyNeko\recordings\`;
- conversazioni o dati personali.

Le evidenze salvate in `benchmarks/` sono solo aggregate: numeri, configurazioni
e metodo, nessun testo dettato.

## Ripristinare

Il progetto non è cancellato. Se un giorno servisse di nuovo:

- il codice è in questo repository, ultimo commit su `develop`;
- l'autostart dell'app era stato rimosso: il valore originale è in
  `~/.chattyneko-run-backup.txt` (chiave `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`);
- l'installazione resta in `%LOCALAPPDATA%\Programs\ChattyNeko`;
- i modelli usati sono su disco e il catalogo è in `client-windows/models/`.
