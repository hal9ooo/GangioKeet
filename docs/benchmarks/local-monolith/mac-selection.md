# Selezione stack Mac — M1 8 GB

Data: 2026-10-28. Stato: **gate fallito sul candidato primario; decisione sospesa**.

## Hardware e criterio

- MacBook Air M1, 8 GB.
- `max_recommended_working_set_size`: 5.726.633.984 byte (~5,33 GiB).
- Il gate non è l'RSS: sono richiesti Metal peak entro il working set, nessuna crescita dello swap e completamento di dettature brevi, 10–30 s e 60 s.
- Candidata primaria scelta dall'utente: Qwen3-ASR-1.7B 4-bit + Gemma 4 E2B QAT text-only 4-bit.

## Candidato primario

Pesi: Qwen 1,60 GB + Gemma 2,64 GB = 4,24 GB.

| Misura | Risultato |
|---|---:|
| Load dei due modelli | 5,16 s |
| Dettatura 4 s: STT | 0,92 s |
| Dettatura 4 s: cleaner | 1,92 s |
| Dettatura 4 s: totale | **2,84 s** |
| Metal peak (4 s) | **5,00 GB** |
| Working set | 5,73 GB |
| Swap prima/dopo (4 s) | 5,37 GB / 5,37 GB |
| Dettatura 20 s | **Metal OOM**: `Insufficient Memory` durante `mlx_lm.generate` |
| Benchmark 5×4 s + 3×20 s + 1×60 s | interrotto dopo ~4m37s sotto pressione di memoria; 60 s non completato |

La dettatura breve funziona e rispetta il budget dei 4 s, ma il picco Metal è già all'87% del working set. Con 20 s di audio il processo supera la memoria disponibile e la GPU fallisce. Il caso 10–30 s è quindi **non supportato** da questa coppia su 8 GB.

## Evidenza di fallback (non ancora una scelta)

### Parakeet TDT v3 + Gemma 4 E2B QAT text-only

- Pesi: Parakeet ~1,26 GB attivi + Gemma ~2,64 GB; dopo il caricamento: 3,89 GB attivi / 3,89 GB peak.
- 20 s con PCM sintetico ripetuto: nessun OOM, Metal peak 4,80 GB.
- Misura a freddo: STT 23,6 s, cleaner 139 s. Il primo run include compilazione shader e va rimisurato a caldo; il dato non è conclusivo e non dimostra il budget dei 4 s.
- Conserva cleaner e pipeline Windows, ma **non** lo STT Qwen3-ASR.

### Stack Mac attuale: Parakeet + Qwen3-1.7B-4bit

- È la baseline già funzionante sul Mac; il README riporta Metal peak ~3,1 GB e 1,04 s totali su 3,9 s.
- Non è lo stack Windows: cleaner Qwen3-1.7B invece di Gemma 4 E2B, con qualità delle domande inferiore nella selezione Windows (1/6 vs 6/6).

## Decisione

Il candidato primario è **respinto** per il target 8 GB: non completa 20 s e non può essere dichiarato equivalente a Windows. Non è stata scelta una coppia alternativa in silenzio.

Opzioni da discutere:

1. **Parakeet + Gemma QAT** — massima vicinanza alla pipeline Windows (cleaner e prompt), STT diverso; richiede benchmark a caldo di 20/60 s e verifica dei 4 s.
2. **Qwen3-ASR + Qwen3-1.7B** — conserva lo STT, cleaner diverso; memoria probabilmente compatibile, qualità delle domande da misurare.
3. **Stack Mac attuale** — Parakeet + Qwen3-1.7B; allineare solo prompt/pipeline, nessuna parità di modelli.
4. **Ottimizzazioni di memoria** (chunking Qwen3-ASR, scaricare lo STT prima del cleaner, KV cache quantizzata) — richiede lavoro aggiuntivo e cambia la pipeline; nessuna garanzia sui 4 s.
5. **Accettare swap/lentezza** — sconsigliato: il 20 s va in OOM e il 60 s non è stato completato.

## Riproduzione

```bash
# manifest e hash
.venv-stt-eval/bin/python scripts/fetch_mlx_models.py \
  --candidates models/mac-candidates.json --root models --verify-only

# dettatura breve (4 s): completa
.venv-stt-eval/bin/python scripts/benchmark_mac_stack.py \
  --candidates models/mac-candidates.json --pair primary \
  --audio tests/fixtures/it_short.pcm \
  --output reports/local-monolith/mac-primary-short.json \
  --short-runs 1 --medium-runs 0 --long-runs 0

# 20 s: riproduce il Metal OOM
.venv-stt-eval/bin/python scripts/benchmark_mac_stack.py \
  --candidates models/mac-candidates.json --pair primary \
  --audio tests/fixtures/it_short.pcm \
  --output reports/local-monolith/mac-primary-medium.json \
  --short-runs 0 --medium-runs 1 --long-runs 0
```

I report JSON e i log completi sono in `reports/local-monolith/` e nella workspace SDD; non vengono committati.
