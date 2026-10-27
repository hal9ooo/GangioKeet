# Cleaner locale: protocollo e prima prova

I tre candidati girano tramite LLamaSharp 0.30.0 / llama.cpp Vulkan nello
stesso processo C# dell'ASR. Revisioni GGUF, SHA256, precisioni e licenze
sono in `client-windows/models/candidates.json`; runtime e modifiche in
`client-windows/native/runtime-lock.json`. Nessun server di inferenza.

## Compatibilita e riproducibilita

La copia privata del runtime rinomina i riferimenti ai moduli `ggml` in
`lgml`, mantenendo invariati lunghezze, offset e nomi delle funzioni
`ggml_*`. Comprende DLL native e ModuleRef/stringhe del loader managed.
La prima prova con sole DLL native rinominate ha provocato un crash: il
loader managed enumerava device dell'altro ABI. La correzione ha un test
RED/GREEN, hash originali/modificati e gate reale di convivenza. Le cache
NuGet restano intatte. `scripts/build_native_windows.ps1` prepara e verifica
gli artefatti; l'app non esegue questi script.

LLamaSharp compila contro il pacchetto originale e riceve la copia privata
all'output della build. Il loader verifica anche l'hash dell'assembly
effettivamente caricato prima di inizializzare il backend. Nessun fallback
CPU automatico: viene richiesto `Vulkan0` e tutte le layer disponibili.
I log reali mostrano offload completo, incluso 36/36 layer per Gemma E2B.

Qwen usa il chat template del GGUF e la continuazione non-thinking
`<think>\n\n</think>\n\n`. Il template canonico Gemma 4 non e riconosciuto
dal renderer builtin di questa versione. La specializzazione solo testo,
un messaggio system e uno user, senza tools/storia/thinking, coincide con
il rendering Jinja2 del template nel GGUF fissato (SHA256 del template
`c0e1a4e78b51cbe4924a195b1939a1e0728e86054736d92f370692d1709abb4c`).
Il tokenizer aggiunge BOS: il literal iniziale e omesso soltanto a quel
confine, evitando il doppio BOS rilevato nella prima prova.

Ogni turno crea un contesto KV nuovo. Sampler, contesto e pesi hanno una
vita esplicita; cancellazione/dispose attendono il ritorno nativo. Raggiungere
il limite token, testo vuoto, tag thinking o delimitatori del prompt causa
rifiuto dell'output, senza pubblicare una revisione parziale.

## Input e lettura dei risultati

`tests/fixtures/stt_eval/cleaner_it_v1.jsonl` contiene 16 casi sintetici
di sviluppo: domande, affermazioni di controllo, numeri, negazioni,
identificatori, autocorrezioni e istruzioni da trattare come dettato.
Gli expected sono **sintetici**, non annotazioni umane di audio: non
certificano qualita o risoluzione del problema personale dell'utente.
Il set pubblico FLEURS e il futuro corpus personale restano separati.

Il probe accetta `--text-corpus`, `--cleanup`, `--llama-dir`,
`--runtime-lock` e facoltativamente `--asr-candidate`/`--wav`/`--native-dir`.
Con ASR residente trascrive prima e dopo la prova LLM, conservando entrambi
i risultati e il confronto. L'hash della policy e la versione prompt
identificano ogni report. I JSON grezzi sono locali in
`reports/local-monolith/`, esclusi da Git.

Prima baseline: prompt Python congelato come `python-baseline-local-v1`,
temperatura 0, seed 42, timeout 5 s; nessun bypass per frasi corte.

| Candidato | Precisione | Domande con `?` / 6 | Tempi osservati dei 16 casi |
|---|---|---:|---|
| Qwen3 1.7B | Q8_0 | 3/6 | 699-1220 ms |
| Gemma 4 E2B instruction-tuned | QAT Q4_0 | 6/6 | 725-954 ms |
| Qwen3 4B | Q8_0 | 4/6 | 1203-1643 ms |

Tutti hanno mantenuto numeri e negazioni dei casi presenti. Esistono pero
regressioni: Gemma elimina `perche` da una causale e `cosi` da una frase;
Qwen 1.7B modifica `cosi` e non risolve un'autocorrezione; Qwen 4B elimina
`cosi` e aggiunge virgolette a un identificatore verbale. Anche il casing
dei comandi viene modificato. Quindi nessuna coppia e ancora promossa a
default sulla base di questa prova. Servono prompt conservativo, analisi
su corpus pubblico piu ampio e conferma personale.

La prima esecuzione Gemma con shader non ancora pronti ha superato 5 s;
e riportata separatamente, non rimossa come outlier. I valori in tabella
sono del protocollo corretto (BOS/input UTF-8), prima del warm-up di
prodotto. Non includono ASR o incolla e non dimostrano il budget di 4 s.

Fonti fissate: [LLamaSharp v0.30.0](https://github.com/SciSharp/LLamaSharp/tree/d03b6cbca3c66357e2ed9ee9d0324f5eaeabfd4f),
[Gemma E2B QAT GGUF](https://huggingface.co/google/gemma-4-E2B-it-qat-q4_0-gguf/tree/675cff42a74c774d6cb76f76d8eacb49b48c9b93),
[Qwen 1.7B GGUF](https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/tree/90862c4b9d2787eaed51d12237eafdfe7c5f6077),
[Qwen 4B GGUF](https://huggingface.co/Qwen/Qwen3-4B-GGUF/tree/bc640142c66e1fdd12af0bd68f40445458f3869b).
