# Risultati local monolith: coppia selezionata

Data: 2026-10-27. Coppia: **Qwen3-ASR-1.7B F16 + Gemma 4 E2B-it Q4_0**,
backend Vulkan, policy `conservative-local-v3`.

Questo report misura l'inferenza e il ciclo di vita; non è una misura
dell'incolla su una finestra desktop reale. La latenza release-to-paste è
coperta separatamente dalla logica `TurnTiming` e dal gate di arresto.

## Artefatti e corpus

- Runner congelato: `GangioKeet.Inference.dll` SHA256
  `03f77e39ba761f1188a45dc50bc38798f4b987e7de296324f1234f809cd2d51c`,
  `GangioKeet.Eval.dll` SHA256
  `de142b0f4885bcbccd1713032e7a9c991d15254623dda14fc5b23d451252b705`.
- Manifest held-out: FLEURS `it_it` test, revisione
  `70bb2e84b976b7e960aa89f1c648e09c59f894dd`, seed 10792947, 200 clip,
  2.884,5 s, SHA256 canonico
  `8bf86d88673deabcc780c8c400bfd5fdf7474dd75fdce2dc979893c39c2c3677`.
- Bucket: 36 clip fino a 10 s, 161 da oltre 10 a 30 s, 3 da oltre 30 a 60 s.
- Modelli e hash: `client-windows/models/selected.json`; runtime:
  `client-windows/native/runtime-lock.json`.
- Policy: hash canonico
  `69483c177408d58b62dea1f3a9ff660cb9bee973144ffd1ee5f6fad9e8825740`.
- Prompt: `conservative-local-v3`, SHA256
  `e63fa4085c525b203bbd71897a25c03af0984b0a627ee229e6304081a349e907`.

## Passate held-out

| Passata | Clip | Cold start | Picco RAM | Errori | Fallback |
|---|---:|---:|---:|---:|---:|
| 1 | 200 | 22,36 s | 9,45 GB | 0 | 0 |
| 2 | 200 | 21,50 s | 9,43 GB | 0 | 0 |

Le due passate sono deterministiche sul testo: WER finale 0,0276154 e F1
0,8816 identici. La seconda passata è leggermente più veloce; la differenza
è riportata come variabilità osservata, non come garanzia.

Una passata = un warm-up escluso + 200 clip distinti a caldo. Il development
aveva tre passate sulle stesse 30 clip; l'held-out usa 200 clip indipendenti
per la scelta finale. Non sono state fatte tre ripetizioni delle stesse 200
clip per non confondere la distribuzione delle latenze con il costo GPU.

## Latenza della coppia

Millisecondi, misurati dal runner nativo, senza incolla.

| Bucket | Clip | p50 (1/2) | p95 (1/2) | max (1/2) | >4000 ms (1/2) |
|---|---:|---:|---:|---:|---:|
| 0-10 s | 36 | 2388,3 / 2238,4 | 2858,0 / 2752,1 | 2862,3 / 2787,2 | 0 / 0 |
| 10-30 s | 161 | 3214,7 / 3008,2 | 4014,2 / 3841,4 | 4310,8 / 4453,3 | 9 / 4 |
| 30-60 s | 3 | 6211,2 / 5873,3 | 6488,1 / 6005,0 | 6488,1 / 6005,0 | 3 / 3 |

L'obiettivo di 4 s per le frasi brevi è rispettato in entrambe le passate:
p95 2,86 s e 2,75 s, massimo 2,86 s e 2,79 s. Nel bucket 10-30 s la prima
passata ha p95 4,01 s e 9/161 clip oltre 4 s; la seconda ha p95 3,84 s e
4/161. Per quel bucket il piano non impone una soglia. Il bucket 30-60 s è
riportato senza una soglia inventata.

## Confronto e qualità

La tabella completa è in `selection.md`. Sintesi:

- WER finale Qwen+Gemma **0,0276**, F1 punteggiatura **0,8816**.
- Parakeet+Gemma: WER 0,0280, F1 0,8465, p95 breve 1,48 s.
- Whisper+Gemma: WER 0,0333, F1 0,8531, p95 breve 2,53 s.
- Qwen3-ASR grezzo: WER 0,0236; il cleaner peggiora il WER di circa 0,004
  ma migliora la punteggiatura e recupera 6/6 domande dirette sul corpus
  sintetico.
- Baseline sherpa-onnx int8 CPU sugli stessi 200 clip: WER 0,04475, F1
  0,8317, p50 breve 448,8 ms, p95 breve 547,9 ms.

Il WER del cleaner è un compromesso misurato, non una dimostrazione che la
revisione LLM migliori sempre la trascrizione. La coppia resta la migliore
per WER finale e punteggiatura fra quelle provate.

## Release-to-paste

`TurnTiming` registra il timestamp al primo Stop e quello dell'incolla
riuscito; un secondo Stop non azzera il primo timestamp. Senza incolla
riuscito il valore è `null` e il motivo è separato (`focus_lost`,
`uipi_blocked`, `empty_result`, `aborted`, ecc.).

Test con clock finto:

- 60 s di registrazione + 3.500 ms dopo il rilascio → 3.500 ms, non 63.500;
- secondo Stop non azzera il timestamp;
- incolla fallito non produce una latenza di successo;
- bucket 10 s incluso nel breve, 10,001 s nel medio.

La misura reale release-to-paste su una finestra controllata non è stata
eseguita in questa sessione. Il replay del file misura solo l'inferenza: non
va presentato come latenza del prodotto completa.

## Ciclo di vita e processo

Gate reale Qwen+Gemma:

- entrambi i runtime residenti (`ggml*.dll`/`transcribe.dll` e
  `lgml*.dll`/`llama.dll`) nello stesso processo;
- cancel a 2.000 ms dal warm-up, stato `cancelled`, exit 3, nessun testo
  pubblicato;
- ritorno nativo circa 0,40 s dopo il cancel, dispose 1,61 s,
  cancel-to-exit circa 2,01 s; due esecuzioni hanno dato dispose 1,04 s e
  1,61 s, entrambe sotto il budget di 5 s;
- picco working set 9,24 GB durante il gate;
- nessun server, processo figlio o porta di ascolto avviata dal runner.

Il test opt-in `LocalLifecycleTests` carica la coppia reale e misura
`StopAsync`; viene eseguito solo con `RUN_LOCAL_LIFECYCLE=1` e non fa parte
della suite portabile. Esecuzioni reali del 2026-10-27: stop dopo caricamento e
warm-up riusciti in **1.479,0 ms** (una misura precedente: 845,2 ms); stop
durante il caricamento nativo, dopo che `transcribe.dll` è stato caricato, in
**3.927,7 ms**. Entrambi restano sotto il budget di 5 s. Il test misura solo lo
stop: l'assenza di listener e processi figli è verificata dalla composizione
locale e dalla scansione del payload, non da questo test.

## Domande, voce personale e limiti

- FLEURS held-out non contiene `?`: il comportamento sulle domande è
  verificato solo dal corpus sintetico `cleaner_it_v1` (Gemma 6/6). Non è
  una prova su audio umano e non dichiara risolto il difetto personale.
- Non ci sono registrazioni personali revisionate: la scelta resta
  provvisoria rispetto a voce, lessico tecnico e autocorrezioni dell'utente.
- Il corpus pubblico non misura termini tecnici del glossario; il caso
  sintetico `FastAPI`/`Docker` è l'unica verifica.
- Alimentazione, temperatura e driver GPU non sono stati strumentati in
  questa sessione; il device riportato è la Radeon 8060S e il backend è
  Vulkan. Nessuna dichiarazione di conformità termica.
- Il test reale di incolla su finestra controllata non è stato eseguito.
  Non usare questo report per affermare che l'end-to-end è sotto 4 s su una
  UI reale: l'inferenza breve lo è, l'incolla va misurato a parte.

## Riproduzione

```powershell
dotnet test client-windows/GangioKeet.Client.sln --no-restore
uv run --extra onnx pytest -m 'not integration' -q
```

Replay della coppia:

```powershell
.\scripts\eval_local_monolith.ps1 `
  -Runner .\.superpowers\sdd\2026-10-27-local-monolith\eval-conservative-v3\GangioKeet.Eval.exe `
  -Stage heldout
```

Il comando completo esegue tutti i candidati; per la sola coppia usare il
runner con `--phase pipeline --stt qwen3_asr_1_7b --cleaner gemma4_e2b`.
