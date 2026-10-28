# Selezione stack Mac — M1 8 GB

Data: 2026-10-28. Stato: **gate fallito sul candidato primario; si mantiene lo stack Mac attuale**.

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

## Fallback misurato a caldo

### Parakeet TDT v3 + Gemma 4 E2B QAT text-only

- Pesi: Parakeet ~1,26 GB attivi + Gemma ~2,64 GB; dopo il caricamento: 3,89 GB attivi, 3,89 GB peak.
- Metal peak durante le prove: **4,82 GB** su 5,73 GB di working set; nessun OOM.
- 4 s, 5 ripetizioni: p50 **1,60 s**, max 2,66 s; nessun timeout.
- 20 s, 3 ripetizioni: p50 **6,66 s**, max 8,63 s; **2/3 cleaner in timeout a 5 s** (una prova completa in 5,96 s).
- 60 s: cleaner in timeout a 5 s; STT 9,67 s; nessun testo ripulito.
- Conserva cleaner, prompt e pipeline Windows; **non** lo STT Qwen3-ASR. Per le dettature lunghe il cleaner non è affidabile.

### Stack Mac attuale: Parakeet + Qwen3-1.7B-4bit

- Metal peak: **3,62 GB** su 5,73 GB di working set; nessun OOM.
- 4 s, 5 ripetizioni: p50 **1,83 s**, max 1,84 s; nessun timeout.
- 20 s, 3 ripetizioni: p50 **6,27 s**, max 6,52 s; cleaner 4,56–4,99 s, **nessun timeout**.
- 60 s: cleaner in timeout a 5 s; STT 3,99 s; nessun testo ripulito.
- È la baseline già funzionante sul Mac; non è lo stack Windows: cleaner Qwen3-1.7B invece di Gemma 4 E2B, con qualità delle domande inferiore nella selezione Windows (1/6 vs 6/6).

## Decisione

Il candidato primario è **respinto** per il target 8 GB: non completa 20 s e non può essere dichiarato equivalente a Windows. Il fallback Parakeet + Gemma **non regge** le dettature lunghe: a 20 s va in timeout 2 volte su 3 e a 60 s il cleaner non completa; lo stack attuale è più veloce e più stabile, ma non ha la qualità delle domande di Gemma.

Raccomandazione: **tenere lo stack locale attuale** (Parakeet + Qwen3-1.7B) e allineare prompt/pipeline, a meno che la priorità assoluta sia la qualità delle domande e si accetti che le dettature lunghe restino senza revisione LLM. Per avere lo stack Windows completo fino a 60 s serve un Mac con più memoria (16 GB+).

Opzioni:

1. **Stack Mac attuale** — Parakeet + Qwen3-1.7B; prompt/pipeline allineati, nessuna parità di modelli. È la scelta consigliata per stabilità e latenza.
2. **Parakeet + Gemma QAT** — cleaner Windows, STT diverso; ok sulle frasi brevi, inaffidabile su 20 s e 60 s.
3. **Qwen3-ASR + Qwen3-1.7B** — conserva lo STT, cleaner diverso; memoria probabilmente compatibile, qualità delle domande da misurare.
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
