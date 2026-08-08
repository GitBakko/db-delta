# Smoke-test results — live SQL Server endpoints

Sanitised summary of M13 smoke runs against the two real PcrmV2Pl
databases. Raw HTML / JSON / SQL outputs live in `scripts/smoke/` and
are git-ignored because they carry production schema names.

## 2026-08-08 — prova di UPGRADE della MSI: 1.0.0-rc5 → 1.0.1

La prima release definitiva sarà **1.0.1** e non 1.0.0, così `MajorUpgrade`
scatta sopra le RC (che hanno tutte `ProductVersion` numerica 1.0.0) invece di
costringere ogni utente a disinstallare a mano. Nessuno aveva mai eseguito un
upgrade di questo prodotto, quindi è stato provato prima di deciderlo per buono:
MSI `1.0.1` costruita in locale e installata **sopra** la rc5, senza
disinstallarla.

| Verifica | Prima | Dopo |
|----------|-------|------|
| Voci in Installazione applicazioni | 1 (1.0.0) | **1** (1.0.1) — non due |
| `DbDelta\cli` nel PATH di macchina | ×1 | **×1** — non duplicata, non persa |
| `DisplayIcon` | vuota | `C:\Program Files\DbDelta\DbDelta.App.exe` |
| `InstallLocation` | vuota | `C:\Program Files\DbDelta\` |
| app + CLI + collegamento | ok | ok, entrambi riportano `1.0.1` |

`msiexec` esce 0 senza alcuna disinstallazione manuale. Le due cose che potevano
rompersi sono proprio quelle due righe di mezzo: una seconda voce ARP fantasma,
e la voce PATH scritta dal nuovo prodotto e poi portata via dalla disinstallazione
del vecchio. Non succede perché `MajorUpgrade` programma
`RemoveExistingProducts` subito dopo `InstallValidate` — verificato nel log, non
dedotto:

```
1. InstallValidate
2. RemoveExistingProducts   <-- il vecchio esce qui
3. InstallFiles             <-- il nuovo entra dopo
4. InstallFinalize
```

La correzione ARP di `4b36cb4` viaggia con questa MSI, ed è ciò che popola le due
righe centrali della tabella. La macchina è stata riportata alla **rc5
rilasciata** dopo la prova: una build locale stampata `1.0.1` avrebbe un pill di
versione che punta a un'ancora inesistente sul sito.

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
