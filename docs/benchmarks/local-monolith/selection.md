# Selezione della coppia locale

Data: 2026-10-27. Stato: **provvisoria sul corpus pubblico**. La voce e il
lessico personale dell'utente non sono ancora misurati.

## Metodo e artefatti congelati

Runner immutabile della valutazione:

- `GangioKeet.Eval.dll` SHA256 `de142b0f4885bcbccd1713032e7a9c991d15254623dda14fc5b23d451252b705`
- `GangioKeet.Inference.dll` SHA256 `03f77e39ba761f1188a45dc50bc38798f4b987e7de296324f1234f809cd2d51c`
- apphost `GangioKeet.Eval.exe` SHA256 `d08bc20ae223451488b69d7bd1f7db047f3d171dd7670db2a0f7ce7b1e9c78d0`

Corpus held-out: FLEURS `it_it` test, revisione
`70bb2e84b976b7e960aa89f1c648e09c59f894dd`, 200 clip, seed 10792947,
2.884,5 secondi di audio. Manifest canonico UTF-8 LF SHA256
`8bf86d88673deabcc780c8c400bfd5fdf7474dd75fdce2dc979893c39c2c3677`.
Bucket: 36 clip fino a 10 s, 161 da oltre 10 a 30 s, 3 da oltre 30 a 60 s.
Nessun riferimento contiene `?`.

Protocollo: un warm-up escluso dalle misure, poi una passata held-out per
candidato. Sul development erano state eseguite tre passate per candidato;
la selezione finale usa 200 clip distinti, non tre ripetizioni delle stesse
30 clip. La scelta non è stata fatta sul development e i parametri non sono
stati modificati dopo aver visto l'held-out.

Prompt cleaner: `conservative-local-v3`, SHA256
`e63fa4085c525b203bbd71897a25c03af0984b0a627ee229e6304081a349e907`.
Policy: `cleanup.example.yaml`, hash canonico
`69483c177408d58b62dea1f3a9ff660cb9bee973144ffd1ee5f6fad9e8825740`.

## ASR senza cleaner

WER calcolata sulla reference normalizzata; la punteggiatura è misurata a
parte. Latenze in millisecondi, backend Vulkan, precisione F16.

| Candidato | WER | F1 punteggiatura | 0-10 s p50/p95/max | 10-30 s p50/p95 | Cold start | Picco RAM |
|---|---:|---:|---:|---:|---:|---:|
| Qwen3-ASR-1.7B | **0,0236** | **0,8785** | 1023,8 / 1372,8 / 1377,0 | 1655,1 / 2247,0 | 15,38 s | 5,34 GB |
| Whisper large-v3 | 0,0262 | 0,8444 | 832,4 / 1039,1 / 1090,1 | 1135,6 / 1574,4 | 10,99 s | 4,15 GB |
| Parakeet TDT v3 | 0,0278 | 0,8288 | 83,6 / 98,7 / 99,3 | 117,9 / 162,2 | 5,13 s | 2,16 GB |

Tutti hanno completato 200/200 clip senza errori o fallback. Il cold start
comprende caricamento, verifica hash e prima inferenza; non è la latenza a
caldo. Parakeet è molto più veloce ma meno accurato e con punteggiatura
peggiore. Whisper non è il migliore su questo corpus.

## Cleaner isolato su testo fisso

Stessa trascrizione Parakeet congelata per tutti i cleaner, così la
differenza misura il cleaner e non l'ASR. Il WER finale può quindi risultare
più alto del WER ASR della tabella precedente pur senza errori del cleaner.

| Cleaner | WER finale | F1 punteggiatura | 0-10 s p50/p95 | Righe modificate | Numeri cambiati | Negazioni perse | Domande dirette v3 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Gemma 4 E2B-it Q4_0 | **0,0280** | **0,8465** | 1274,8 / 1472,9 | 40 | 1 (corretto `2` → `due`) | 0 | **6/6** |
| Qwen3-1.7B Q8_0 | 0,0300 | 0,8304 | 1114,1 / 1281,8 | 18 | 0 | 0 | 1/6 |
| Qwen3-4B Q8_0 | 0,0302 | 0,8308 | 2255,7 / 2593,8 | 30 | 0 | 3 | 4/6 |

Qwen3-4B elimina per esempio `seppure non troppo bene` da una frase: è una
regressione semantica, non solo una differenza di stile. Qwen3-1.7B è più
veloce di Gemma ma non conserva le domande dirette.

Sul corpus sintetico mirato `tests/fixtures/stt_eval/cleaner_it_v1.jsonl`,
Gemma produce 14/16 testi identici alla reference; i due scarti sono una
`?`/punto finale mancante su un percorso e su un valore numerico, non
cambi di significato. Recupera 6/6 `?`, non cambia numeri o negazioni e non
produce warning. Qwen3-1.7B recupera 1/6 `?`; Qwen3-4B 4/6. Questo corpus è
testo sintetico, non audio umano: dimostra il comportamento del cleaner, non
la risoluzione del difetto personale dell'utente.

## Pipeline reale ASR + Gemma

Le tre coppie usano lo stesso cleaner Gemma e la stessa policy. I motori
sono caricati nello stesso processo; i report espongono device e moduli
nativi caricati.

| Coppia | WER finale | F1 punteggiatura | 0-10 s p50/p95/max | 10-30 s p50/p95 | >4000 ms 10-30 s | Cold start | Picco RAM |
|---|---:|---:|---:|---:|---:|---:|---:|
| **Qwen3-ASR + Gemma** | **0,0276** | **0,8816** | 2388,3 / 2858,0 / 2862,3 | 3214,7 / 4014,2 | 9/161 | 22,36 s | 9,45 GB |
| Parakeet + Gemma | 0,0280 | 0,8465 | 1295,9 / 1478,9 / 1479,1 | 1519,7 / 1773,5 | 0/161 | 13,24 s | 6,39 GB |
| Whisper + Gemma | 0,0333 | 0,8531 | 2145,7 / 2527,9 | 2572,3 / 3069,6 | 0/161 | 18,49 s | 8,41 GB |

Tutte le coppie hanno completato 200/200 clip senza errori, timeout o
fallback. La coppia Qwen+Gemma rispetta l'obiettivo dei 4 s nel bucket breve
(p95 2,86 s) e ha il miglior WER finale e la miglior F1. Nel bucket 10-30 s
9 clip superano 4 s, ma per quel bucket non esiste una soglia imposta.

La revisione LLM non è automaticamente un miglioramento del WER:

- Qwen: 0,0236 → 0,0276 (alcune parole brevi vengono eliminate dal cleaner);
- Parakeet: 0,0278 → 0,0280 (sostanzialmente neutro);
- Whisper: 0,0262 → 0,0333 (regressione netta, causata dalle omissioni del
  cleaner sul testo Whisper).

Qwen+Gemma resta la migliore coppia finale, ma il guadagno di punteggiatura
costa qualche parola. La scelta è quindi esposta come compromesso misurato,
non come superiorità universale.

## Scelta

Coppia selezionata: **Qwen3-ASR-1.7B F16 + Gemma 4 E2B-it Q4_0, Vulkan**.
`client-windows/models/selected.json` contiene ID, revisioni, SHA256 dei
file, runtime, hash della policy e del prompt.

Motivi:

1. miglior WER finale fra le coppie misurate (0,0276);
2. miglior F1 di punteggiatura (0,8816) e 6/6 domande dirette sul corpus
   mirato;
3. p95 2,86 s nel bucket breve, entro l'obiettivo di 4 s;
4. nessun errore o fallback su 200 clip;
5. nessun numero o negazione modificato nella pipeline Qwen+Gemma.

Esclusioni:

- **Whisper+Gemma**: WER finale 0,0333, il peggiore; il cleaner elimina
  contenuto. Whisper non è selezionato nonostante il buon WER ASR grezzo.
- **Parakeet+Gemma**: 0,0280 e F1 0,8465; più veloce, ma qualità inferiore.
  Resta il fallback a bassa latenza e bassa memoria.
- **Qwen3-1.7B cleaner**: 1/6 domande, WER peggiore di Gemma.
- **Qwen3-4B cleaner**: 4/6 domande e perdita di negazioni/contenuto.
- **Backend CPU**: non selezionato; il gate richiede GPU effettiva e i tempi
  CPU del task 4 non rispettano il budget breve. Nessun fallback CPU
  silenzioso.
- **Baseline MLX**: non eseguibile su Windows e non confrontabile come se
  fosse lo stesso artefatto. La baseline GGUF è un porting, non una misura
  del cleaner MLX di produzione.

## Baseline sherpa CPU

Confronto di riferimento, non candidato alla distribuzione: sherpa-onnx
Parakeet v3 int8 su CPU.

- Development, 30 clip: WER 0,02907, mediana ASR 873,27 ms, massimo
  1.517,22 ms (task 4).
- Held-out, 200 clip: WER 0,04475, F1 punteggiatura 0,8317, caricamento
  1,72 s; 0-10 s p50 448,8 ms, p95 547,9 ms; 10-30 s p50 788,9 ms, p95
  1.346,6 ms; nessuna clip oltre 4 s. Il WER held-out è più alto del WER
  development: il corpus è più difficile e la baseline int8 non va usata
  come prova di qualità equivalente alla coppia nativa.

La baseline usa un checkpoint e un runtime diversi dalla coppia nativa; non
dimostra la qualità della pipeline distribuita.

## Gate di fattibilità

Report del replay held-out Qwen+Gemma:

- moduli nativi contemporanei: `ggml*.dll`/`transcribe.dll` e
  `lgml*.dll`/`llama.dll` nello stesso processo;
- ASR su `AMD Radeon(TM) 8060S Graphics`, cleaner su `Vulkan0`, entrambi
  backend `vulkan`;
- warm-up completato prima delle 200 clip; cold start 22,36 s;
- picco working set 9,45 GB; zero errori, zero fallback.

Report del gate di cancellazione (`pair-gate-cancel-cleaner.json`, ultima
esecuzione con il runner del gate ricostruito):

- cancel richiesto 2.000 ms dopo il warm-up, durante il turno;
- stato `cancelled`, exit 3, nessun testo pubblicato;
- entrambi i runtime ancora caricati durante il turno;
- cold start 21,77 s; la chiamata nativa ritorna circa 0,40 s dopo il
  cancel; il dispose completo richiede 1,61 s; cancel-to-exit circa 2,01 s;
- picco working set 9,24 GB;
- nessun processo figlio o server avviato dal runner.

Due esecuzioni del gate hanno dato dispose 1,04 s e 1,61 s: entrambe sotto
il budget operativo di 5 s. La variabilità è riportata, non nascosta.

Il dispose misura la finestra di arresto; resta sotto il budget operativo di
5 s. La cancellazione è cooperativa e non promette l'abort istantaneo a metà
encoder.

## Limiti e dichiarazioni

- Il corpus held-out pubblico non contiene `?`: la gestione delle domande è
  dimostrata solo dal corpus sintetico mirato. Il difetto personale non è
  dichiarato risolto.
- FLEURS non contiene il lessico tecnico dell'utente; i termini del glossario
  sono verificati solo nei casi sintetici.
- La scelta è provvisoria finché non esistono registrazioni personali
  revisionate, con domande e affermazioni di controllo.
- Il WER finale Qwen+Gemma è peggiore del WER ASR grezzo: il cleaner corregge
  punteggiatura e alcuni errori, ma elimina anche parole brevi. Non usare
  questo report per affermare che la revisione LLM migliora sempre il WER.
- Il cold start di Qwen+Gemma è 22,36 s; la UI deve restare disponibile
  durante il caricamento e non accettare dettature prima di `Ready`.
