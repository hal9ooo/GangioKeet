# Corpus italiano per il monolite locale

Il corpus v2 conserva `transcription` come riferimento ASR e
`raw_transcription` originale come riferimento punteggiato. Non ricostruisce
la punteggiatura quando il secondo campo manca: `reference_status=raw_only`
esclude quella clip dalle metriche finali, mantenendola utile per WER ASR.

Ogni clip registra l'indice originale della riga, il path pubblico sorgente,
ID univoco e SHA256 dei byte PCM16 decodificati. Il manifest ha codifica UTF-8
senza BOM e LF; la verifica dell'hash su checkout Windows normalizza soltanto
CRLF in LF. Non si normalizzano altri contenuti.

FLEURS: <https://huggingface.co/datasets/google/fleurs>, subset `it_it`,
licenza CC-BY-4.0. Citazione: Conneau et al., *FLEURS: Few-shot Learning
Evaluation of Universal Representations of Speech*, 2022,
<https://arxiv.org/abs/2205.12446>.

Protocollo: 30 clip di validation per smoke/development e almeno 200 clip
di test per held-out finale. La selezione è deterministica; provenienza
contiene revisione dataset, split, scopo, seed, conteggio e hash manifest.
Non usare il test per scegliere prompt o parametri. La vecchia selezione
`fleurs_it` resta v1: non sovrascriverne manifest o provenienza.

Gli audio, manifest di lavoro e report grezzi restano locali e ignorati da
Git. Solo provenienza, licenza e report sintetici sono pubblicabili. Un
benchmark personale richiede correzioni umane esplicite e resta separato.

## Prima acquisizione verificata

Il 2026-10-27 sono stati recuperati e verificati 30 WAV della validation alla
revisione `70bb2e84b976b7e960aa89f1c648e09c59f894dd`, 417,36 secondi totali.
Tutti i 30 riferimenti finali sono presenti; nessuno contiene `?`: questo
smoke set non basta per valutare il riconoscimento delle domande.
Provenienza: `tests/fixtures/stt_eval/public/fleurs_it_v2/provenance.json`.

Download nel solo ambiente eval: datasets 5.1.0, soundfile 0.14.0,
huggingface-hub 2.3.0, pyarrow 26.0.0, numpy 2.5.4.
L'importatore carica soltanto Parquet della revisione fissata e decodifica
con soundfile, senza eseguire script remoti o richiedere TorchCodec.
