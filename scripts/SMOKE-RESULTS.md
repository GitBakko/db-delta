# Smoke-test results — live SQL Server endpoints

Sanitised summary of M13 smoke runs against the two real PcrmV2Pl
databases. Raw HTML / JSON / SQL outputs live in `scripts/smoke/` and
are git-ignored because they carry production schema names.

## 2026-08-01 — commit `710394e` — **primo giro chiuso: deploy completo + convergenza a zero**

Source `192.168.3.243` / `PcrmV2Pl_test` → target stesso server / `PcrmV2Pl_test2`,
eseguito dall'app (non dalla CLI: il dialogo di backfill esiste solo lì).

| Passo | Esito |
|-------|------:|
| Primo script, seleziona-tutto | 279 oggetti, 20.261 righe |
| Esecuzione sulla destinazione | riuscita, pochi secondi, zero errori |
| Riconfronto automatico | **33 differenze residue** |
| Correzione `710394e` + Aggiorna | **0 differenze** |

Cosa ha esercitato per la prima volta su dati veri:

- **Backfill (Msg 4901)** — nessun dialogo, correttamente: gli unici due ADD di
  colonna NOT NULL portavano un `DEFAULT` dichiarato dalla sorgente. Zero
  `ADD … NOT NULL` senza default in tutto lo script.
- **`SET QUOTED_IDENTIFIER OFF`** attorno a 4 moduli, tutti convergiti.
- **`DATA_COMPRESSION`** — `REBUILD` su `WebhookDeliveries` e `WebhookOutbox`,
  3 `ALTER INDEX … REBUILD`, 1 indice creato già compresso. Esattamente gli
  oggetti che il confronto Redgate aveva segnalato.
- **Distruttivo:** un solo `DROP TABLE` reale (`Tenants_Corrieri_TipiDocumenti`),
  più 3 procedure, 1 vista, 1 indice, 1 FK.

**I 33 residui erano il difetto peggiore della giornata** — un diff che il
proprio script non appiattiva. Diagnosi e correzione in
`docs/review/2026-07-31-handoff-post-wave.md`, sezione «Il diff che il proprio
script non appiattiva».

Nessun timeout: i sei REBUILD sono passati abbondantemente sotto il limite, alzato
a 10 minuti per batch (`d7f09d4`) prima di partire.

## 2026-05-25 — commit `354e9e0` (M13 wave 1 + DRY.4 done)

Source endpoint: `192.168.3.243` / `PcrmV2Pl_test2`
Target endpoint: `192.168.3.242` / `PcrmV2Pl`

### `dbdelta report --html --json` — exit 1 (differences found)

| Status | Count |
|--------|------:|
| Different | 44 |
| OnlyInB (only on target) | 28 |
| OnlyInA (only on source) | 3 |
| Identical | 605 |
| **Total compared** | **680** |

Kind distribution (JSON parsed):

| Kind | Count |
|------|------:|
| Table | 341 |
| View | 172 |
| Procedure | 151 |
| Function | 12 |
| TableType (UDTT) | 4 |
| Schema-only kinds (Sequence / Synonym / UDT alias / User / Role / Permission / Trigger) | 0 in this dataset |

Output files:
- `scripts/smoke/smoke-2026-05-25.html` — 99 KB self-contained HTML.
- `scripts/smoke/smoke-2026-05-25.json` — 177 KB pretty JSON, camelCase
  shape matching `ComparisonResultDto`.

### `dbdelta script --out <file>` — exit 0 (script written)

- `scripts/smoke/smoke-2026-05-25.sql` — 162 KB / 4 691 lines.
- Header wrapper: `SET XACT_ABORT ON; BEGIN TRANSACTION; GO`.
- Verified content shapes seen at the top of the script:
  - `DROP TABLE` for OnlyInB targets (e.g. `dbo.__LogFatturazioniMolteplici`).
  - `ALTER TABLE … ADD COLUMN …` for additive column deltas.
  - Full `CREATE TABLE` with inline `PRIMARY KEY` + `DEFAULT` constraints
    for OnlyInA tables.

### Confirmations

- **M13-FIX.4 TableType reader** — 4 UDTTs surfaced from the live DB
  catalogues, proving `sys.table_types` JOIN works against a real
  SQL Server instance.
- **M13-FIX.1 orphan emitters** — none of those kinds were divergent
  in this dataset, so the live run did not exercise the new prologue
  / epilogue DDL paths. Coverage stays on the 24 unit + integration
  tests; rerun the smoke once a divergent dataset becomes available.
- **CLI script verb (M13-FIX.2)** — completed in seconds, produced
  the expected GO-batched migration shape.
- **No exceptions, no warnings.**

## Replay

Run from the repo root with two connection strings (passwords kept
out of the repo — supply at the shell):

```bash
dotnet src/DbDelta.Cli/bin/Release/net10.0/dbdelta.dll report \
  --source "Server=…;Database=PcrmV2Pl_test2;User Id=sa;Password=…;TrustServerCertificate=True;Encrypt=False" \
  --target "Server=…;Database=PcrmV2Pl;User Id=sa;Password=…;TrustServerCertificate=True;Encrypt=False" \
  --html scripts/smoke/smoke-<date>.html \
  --json scripts/smoke/smoke-<date>.json
```
