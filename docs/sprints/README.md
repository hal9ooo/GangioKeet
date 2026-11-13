# Resoconti degli sprint

Un file per sprint chiuso: `AAAA-MM-GG-<tema>.md`, con la data di chiusura.
Si genera con la skill `sprint-flow`:

```bash
python ~/.agents/skills/sprint-flow/sprint_flow.py log --title "<tema>" --out docs/sprints/<AAAA-MM-GG>-<tema>.md
```

La parte "Chi ha fatto cosa" e il diario vengono dai commit (trailer `Agent:`
e `Model:`); obiettivo ed esito li scrive l'agente che chiude lo sprint.
