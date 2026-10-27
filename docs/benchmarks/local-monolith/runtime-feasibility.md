# Probe runtime Windows / Radeon 8060S

Stato iniziale: transcribe.cpp 0.4.1, commit
`4d7c1ccefd39e5a9ea36cca5f8fefc6dcc14b99b`, pacchetto ufficiale
Windows x86_64 CPU/Vulkan. Archivio, header e file del payload sono fissati
in `client-windows/native/runtime-lock.json`.

Il runner C# carica la DLL nello stesso processo, senza Python o server.
Il comando `devices` ha rilevato la Radeon 8060S, backend Vulkan, UMA,
FP16/BF16 e cooperative matrix. Memoria totale riportata dal dispositivo:
109.756.350.464 byte; è un valore del backend, non VRAM dedicata.

Primo probe Parakeet v3 F16: modello verificato con SHA256,
audio FLEURS italiano di 11,04 s, caricamento incluso hash 3335,83 ms,
prima inferenza 1131,26 ms, backend effettivo Vulkan sulla Radeon.
Il decoder usa anche operazioni CPU. Il risultato non è un benchmark a
caldo ripetuto né include cleaner/incolla; non dimostra il budget end-to-end.

La cancellazione della libreria è cooperativa tra passi di decode; manca
l'abort a metà encoder e durante caricamento. L'arresto deve attendere il
ritorno nativo prima di liberare sessione/modello. I relativi tempi reali e
la convivenza con il runtime LLM rimangono verifiche obbligatorie.

L'ABI 0.4.1 supporta anche Whisper tramite lo stesso percorso; il piano
iniziale proponeva Whisper.net. Si usa il motore unificato per il confronto
ASR, mantenendo i checkpoint richiesti ed evitando una seconda copia di
ggml nell'ASR. Questa scelta non elimina la verifica delle collisioni con
il cleaner llama.cpp.

## Risultati del gate ASR

Stesso campione FLEURS italiano di 11,04 s, precisione F16, prima
inferenza dopo caricamento. Millisecondi; il caricamento comprende SHA256.

| Candidato | Load CPU | ASR CPU | Load Vulkan | ASR Vulkan |
|---|---:|---:|---:|---:|
| Parakeet v3 | 2036,08 | 581,29 | 3335,83 | 1131,26 |
| Qwen3-ASR 1.7B | 5721,82 | 4973,25 | 8045,19 | 3151,31 |
| Whisper large-v3 | 4266,34 | 8411,92 | 5950,81 | 2233,39 |

I testi CPU/GPU del singolo campione coincidono per ogni candidato.
Questi dati non selezionano ancora una coppia: servono ripetizioni a caldo,
qualita su piu clip e convivenza con il cleaner. Le conversioni GGUF e le
revisioni esatte sono nel catalogo; gli artefatti originali sono verificati
contro SHA256 pubblicati da Hugging Face.

Un input sintetico di **60 s di silenzio**, distinto dal benchmark di
parlato naturale, completa su Vulkan con tutti e tre i modelli. Con abort
richiesto dopo 25 ms, il ritorno e rilascio sicuro richiedono circa 1289 ms
per Parakeet, 1216 ms per Qwen e 226 ms per Whisper. Il processo termina
con exit 3 e report `cancelled`, senza pubblicare una trascrizione parziale.
L'abort non interrompe a meta l'encoder: non promettere arresto immediato.

Baseline Windows esistente: sherpa-onnx Parakeet v3 int8, 30 clip del corpus
smoke, un warm-up escluso, WER 0,02907, mediana ASR 873,27 ms, massimo
1517,22 ms. I file del bundle locale sono identificati con hash; il
report non attribuisce una revisione remota non verificata. Non confrontare
questa mediana con la singola prima inferenza della tabella.

I JSON grezzi rimangono in `reports/local-monolith/` (ignorato da Git).
Test e baseline sono riproducibili dal runner C# e da
`scripts/stt_eval_windows_baseline.py`; nessun runtime Python entra nell'app.
