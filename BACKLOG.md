# Backlog

<!-- Contratto: skill sprint-flow (~/.agents/skills/sprint-flow/SKILL.md).
     La riga "- Inizio:" è il commit da cui parte il resoconto dello sprint. -->

## Sprint corrente: dettatura per gli strumenti degli agenti (da confermare)

- Inizio: `3c0aca9` (2026-11-13)
- Obiettivo: dettare a Claude Code, Codex, OpenCode e alle chat AI senza
  correggere a mano nomi tecnici, punteggiatura e invio. *(Ricostruito da
  Claude Code dal lavoro non committato nel checkout del 13/11: da confermare.)*
- Piano: nessuno in `docs/superpowers/plans/`.

- [ ] B-001 Glossario dei nomi tecnici dettati (Claude Code, Codex, OpenCode,
  DeepSeek, OmniRoute, …) in `cleanup.example.yaml` e `GlossaryNormalizer`
- [ ] B-002 Invio automatico tenendo Ctrl al rilascio di Caps Lock
  (`auto_enter_delay_ms`; `auto_enter` resta per compatibilità)
- [ ] B-003 Punteggiatura con modello ONNX (`OnnxPunctuationCleaner`)
- [ ] B-004 Registro delle dettature nelle impostazioni (`RecordingLogItem`)
- [ ] B-005 Profilo "chat" esteso: WhatsApp desktop e chat AI (Claude, ChatGPT,
  Perplexity, DeepSeek, Gemini, Copilot)

## Prossimi

<!-- in ordine di priorità: il primo è il candidato del prossimo sprint -->

## Idee

<!-- non ancora valutate; un agente che scopre un bug o un'idea la scrive qui -->

## Fatto

- [x] B-000 Backlog, sprint a tema e changelog con la skill `sprint-flow` —
  2026-11-13, claude
