# Hex vs ChattyNeko — misure del 27/11/2026

Confronto fra il motore di dettatura di ChattyNeko e quello di Hex, sugli
stessi file audio e con lo stesso testo di riferimento.

**Esito: i due programmi sono equivalenti sulla qualità del riconoscimento.**
La differenza che conta non è nel WER, è nel fatto che Hex è mantenuto e ha più
funzionalità. Per questo il progetto è ritirato: vedi `docs/RITIRATO.md`.

## Metodo

- **Corpus**: 35 registrazioni reali (788 parole) con testo di riferimento
  scritto a mano dall'operatore. I file audio restano sulla macchina e **non
  sono in questo repository**.
- **Metrica**: WER a livello di parola. Punteggiatura e maiuscole ignorate,
  accenti normalizzati (NFKC). Una parola sbagliata su 788 vale 0,13 punti: due
  configurazioni entro ~0,5 punti sono indistinguibili a questa taglia.
- **Hex**: `hex transcribe --json --model <id> <file>`. Il comando restituisce
  il testo **grezzo**, senza le trasformazioni della modalità.
- **ChattyNeko**: `GangioKeet.Eval replay --phase pipeline`, cioè modello +
  rifinitura deterministica (filler, glossario, correzioni), come in produzione.
- Tutti i testi sono stati decodificati in **UTF-8**.

### Un errore di misura, registrato perché non si ripeta

Le prime misure su Hex davano numeri molto peggiori (cohere 9,77%, parakeet
7,74%). La causa non era Hex: `subprocess.run(..., text=True)` decodifica con la
codepage locale (cp1252), quindi `perché` arrivava come `perchÃ©` e ogni
accento contava come errore. Riletto in UTF-8, cohere scende a 6,09% e parakeet
a 3,93%. I numeri sotto sono quelli corretti.

## Risultati

Dati completi in [`scores.json`](scores.json).

| Configurazione | WER | Errori / parole |
|---|---:|---:|
| ChattyNeko — Parakeet F16, grezzo | 3,30% | 26 / 788 |
| ChattyNeko — Parakeet F16 + pulizia/glossario | 3,55% | 28 / 788 |
| Hex — Parakeet F16 importato | 3,68% | 29 / 788 |
| Hex — parakeet_v3 Q8 (attivo) | 3,93% | 31 / 788 |
| ChattyNeko — Whisper large v3 + light | 5,08% | 40 / 788 |
| ChattyNeko — Whisper + 21 termini come prompt | 5,46% | 43 / 788 |
| Hex — cohere_transcribe | 6,09% | 48 / 788 |
| Hex — orukeet | 8,76% | 69 / 788 |
| Hex — Nemotron 3.5 ASR | 9,14% | 72 / 788 |

Tempi indicativi sui 35 clip: Hex 8-13 s, ChattyNeko ~49 s (include il carico
dei modelli a ogni esecuzione).

## Cosa dicono i numeri

1. **Fra ChattyNeko e Hex non c'è una differenza misurabile.** Lo stesso
   Parakeet dà 3,30% (F16, nel nostro processo) e 3,93% (Q8, in Hex). Sul
   confronto F16 contro Q8 i due output sono identici su **34 clip su 35**;
   l'unica differenza è l'ortografia di un nome. Quindi il divario è
   quantizzazione del modello, non pipeline.
2. **La rifinitura deterministica non guadagna niente su questi clip.** Cambia
   1 clip su 35 e il WER peggiora di 0,25 punti, ma quel caso è una correzione
   giusta contro un riferimento che scriveva il nome in due parole.
3. **Il vocabolario come prompt peggiora Whisper**: 5,08% → 5,46%, con un nome
   riscritto male. Non è stato attivato.
4. **Il glossario non serve più.** Su 35 dettature lunghe (1685 parole) con Hex
   sarebbe intervenuto **5 volte**, tutte e sole di maiuscola su nomi già
   sentiti correttamente. Zero correzioni fonetiche, zero elisioni. Il glossario
   era una toppa per modelli più deboli: con Parakeet non ha più nulla da
   correggere.

## Cosa non è stato misurato

- La qualità su audio difficile (fondo rumoroso, più parlanti): il corpus è
  dettatura pulita al microfono.
- La latenza percepita durante la dettatura: i tempi qui sono misure di
  trascrizione di file, non del percorso microfono→incolla.
- Il riferimento è scritto dall'operatore e contiene forme non canoniche (per
  esempio il nome di un prodotto scritto in due parole): la metrica penalizza
  chi lo scrive in una. È un bias noto e piccolo, non corretto.
