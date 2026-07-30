# DbDelta undo/rollback — staged implementation roadmap

## Read first: Stage 0 is already ~90% built

Before designing anything I traced the execute path. The atomicity the owner is afraid of losing **already exists** and is already integration-tested. The design below therefore does not build a transaction subsystem — it closes three specific holes and then spends its budget on the thing that genuinely does not exist (Stage 1+).

---

## Stage 0 — transactional safety (what already works, and the exact 3 gaps)

### The mechanism, honestly

`GO` is a **client-side** directive, not T-SQL. SqlClient cannot submit a multi-batch string in one command, so `SqlExecutor.SplitOnGo` splits it and `ExecuteAsync` loops `ExecuteNonQueryAsync` once per batch (`SqlExecutor.cs:84-91`). Two consequences that dictate everything:

1. **Batch-local scope.** Variables and temp tables do not survive a `GO`. That is why `DeploymentScriptWriter.WriteVerdict` keeps `DECLARE @Success` and its `IF` in the *same* final batch — an undo can never be expressed as "one variable threaded through the script".
2. **Session-local transaction.** All batches run on **one** `SqlConnection`, so a `BEGIN TRANSACTION` issued in batch 4 is session state that spans every later batch. This is exactly why the design works: `DeploymentScriptWriter.WritePreamble` emits `SET XACT_ABORT ON` + `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE` + `BEGIN TRANSACTION` (`DeploymentScriptWriter.cs:18-26`), and both real callers pass `useOwnTransaction: false` so `SqlExecutor` starts no competing client transaction (`tx = null`, `SqlExecutor.cs:78-80`).

Batch 7 of 20 fails → `XACT_ABORT ON` dooms the server-side transaction → the exception breaks the loop → `await using SqlConnection` disposes → pool reset (`sp_reset_connection`) rolls back anything still open. **The database is not half-migrated.** Proven by `tests/DbDelta.Providers.LiveDb.IntegrationTests/DeployErrorHandlingTests.cs` and `tests/DbDelta.Persistence.IntegrationTests/Sql/SqlExecutorTests.cs`.

Do **not** "improve" this by flipping the app to `useOwnTransaction: true`. A `SqlTransaction` plus the script's own `BEGIN TRANSACTION` gives `@@TRANCOUNT = 2`; the script's `COMMIT` becomes a bare decrement and the client `Commit()` then commits work the script believed it had verdict-gated. The two modes are mutually exclusive by design — the existing doc comment at `SqlExecutor.cs:36-43` is right and the code matches it.

### The three gaps

| Gap | Where | Fix |
|---|---|---|
| Rollback is **implicit and unobservable** — it happens via connection dispose, so `SqlBatchResult` cannot state whether the target was left clean. | `SqlExecutor.cs:96-104` | In the catch, before returning, issue `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;` on the still-open connection (short timeout, best-effort) and add `bool RolledBack` to `SqlBatchResult`. Under `XACT_ABORT` this is a no-op (already doomed) — it earns its keep in the **timeout and cancellation** cases, where the transaction really is still open. |
| **60 s hard ceiling** per batch, and `EmitRebuild` emits the whole `CREATE _tmp / INSERT…SELECT / DROP / sp_rename` as one batch → a 30M-row rebuild is undeployable. | `SqlExecutor.cs:23, 86-88` | `int commandTimeoutSeconds = 60` parameter, `0` = unlimited. Thread from a `--command-timeout` option in `ApplyCommand` and a 32 px numeric field in `ConfirmExecuteDialog`. |
| **`dbdelta apply` on a foreign script has no transaction at all** — the only genuine half-migration hole in the product. | `ApplyCommand.cs:67` | If the script contains no line-start `BEGIN TRAN`, use `useOwnTransaction: true` (the client transaction legitimately spans the batches on the one connection); add `--no-transaction` to opt out. Fix the XML doc at `ApplyCommand.cs:8-11`, which currently claims the opposite of what the code does. |

**Effort: S.** Files: `src/DbDelta.Persistence/Sql/SqlExecutor.cs`, `src/DbDelta.Cli/Commands/ApplyCommand.cs`, `src/DbDelta.App.Avalonia/ViewModels/{MainWindowViewModel,ConfirmExecuteViewModel}.cs`.

**The one test:** `tests/DbDelta.Cli.AcceptanceTests/ApplyCommandTests.cs` — a 3-batch envelope-less script whose middle batch fails; assert batch 1's object does **not** exist afterwards. Today that test fails. (Also rename the existing `Applies_script_inside_a_transaction_and_target_picks_up_the_change`, which asserts atomicity it never exercises.)

---

## Stage 1 — inverse script generation ("down.sql")

### The trick: pair inversion, zero new emitters

`DifferencePair` is `(Identity, Status, SideA, SideB)` and **`SideB` is the captured pre-deploy target object**. `ScriptGenerator.Generate` is a pure function of a `ComparisonResult`. So the down script is the up script with the sides swapped:

```csharp
// src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs — new, ~20 lines
public static string BuildInverse(
    IReadOnlyList<DifferencePair> selectedPairs, string src, string tgt,
    DateTime nowUtc, IReadOnlyList<DependencyEdge>? targetDependencies = null)
    => Build([.. selectedPairs.Select(Invert)], tgt, src, nowUtc, targetDependencies);

private static DifferencePair Invert(DifferencePair p) => p with
{
    Status = p.Status switch
    {
        DifferenceStatus.OnlyInA => DifferenceStatus.OnlyInB,
        DifferenceStatus.OnlyInB => DifferenceStatus.OnlyInA,
        _ => p.Status,
    },
    SideA = p.SideB,
    SideB = p.SideA,
};
```

No emitter changes. No model changes. The topological DROP-pass/CREATE-pass structure inverts correctly by construction: what the up script created, the down script drops in reverse-topological order, and vice versa.

**Two prerequisites, both real:**

1. `DeployScriptBuilder.Build` must gain a `dependencies` parameter and pass it to `Generate` — today it omits it, so `ScriptGenerator.cs:60` does `dependencies ??= []` and ordering degenerates to `KindRank`. A down script with broken ordering is worse than none. This also fixes the confirmed `app-script-loses-dependency-edges` bug.
2. The down script needs the **target-side** edges, not the source's. `AppStateViewModel.CompareAsync` currently discards both `Database` objects and keeps only the `ComparisonResult` (`:184-200`) — it must retain `srcRes.Value!.Dependencies` **and** `tgtRes.Value!.Dependencies`.

### Invertibility by object kind

| Kind | OnlyInA (deploy created) | OnlyInB (deploy dropped) | Different |
|---|---|---|---|
| **View, Procedure, Function, Trigger** | ✅ `DROP … IF EXISTS` | ✅ `CREATE OR ALTER` from captured body | ✅ **perfect** — re-applies the captured pre-deploy body; this alone undoes the clobbered-hotfix scenario |
| **Synonym, UserDefinedType, TableType** | ✅ DROP | ✅ CREATE | ✅ DROP+CREATE of the captured shape (TableType is symmetrically lossy — the model never captured its PK either) |
| **Table** | ✅ DROP (rows inserted *after* the deploy are lost — correct semantics, must be stated) | ⚠️ **structure only, zero rows** | see below |
| **Sequence** | ✅ DROP | ✅ CREATE | ❌ **do not invert.** The reader captures `start_value`, never `current_value`, so `RESTART WITH <old start>` rewinds a live counter and causes PK collisions. Emit a comment instead. |
| **User, Role** | ✅ DROP | ⚠️ `CREATE USER` re-maps by login name; a contained user's password is not in the model | ✅ role-member deltas are symmetric |
| **Permission** | — | — | **nothing to invert**: `IgnorePermissions` is in `ComparisonOptions.Default`, so the up script never emits GRANT/REVOKE from the app, and `EmitPermissions` has no `Different` case at all. Symmetric no-op. |
| **Schema** | — | — | not compared at all today; nothing to invert either way |
| **Encrypted module** (any of the four) | ❌ | ❌ | ❌ the emitter produces only a `-- WARNING` comment in both directions — must be flagged as non-invertible |

**Table `Different`, broken out — this is where the honesty lives:**

- added column → inverse drops it → ✅
- **dropped column → inverse re-adds it all-NULL**, and if it was `NOT NULL` without a default the `ADD` *fails* → ❌
- widened type → inverse **narrows** → ❌ truncation/rounding, silently (`SET NUMERIC_ROUNDABORT OFF`, `DeploymentScriptWriter.cs:18`)
- narrowed type → inverse widens → ✅ (structure; the lost precision does not come back)
- constraint / index add-drop → ✅ symmetric, but a re-added `CHECK`/`FK` validates against *current* rows and can fail
- **identity rebuild** → the inverse is another rebuild, which `INSERT…SELECT`s the current rows, so data survives — but it inherits the confirmed `EmitRebuild` bug that silently drops the table's indexes, triggers and outbound FKs. **Do not offer rebuild inversion until that bug is fixed;** classify it non-invertible in the meantime.

### User-visible flow

`ExecuteOnTargetAsync` (`MainWindowViewModel.cs:618`), **before** showing the dialog: create `%LOCALAPPDATA%\DbDelta\deploys\{yyyyMMdd-HHmmss}-{server}-{db}\` and write `up.sql`, `down.sql`, `meta.json` (redacted endpoints via `ConnectionStringRedactor`, object list, DbDelta version, UTC + server clock). `ConfirmExecuteViewModel` gains `string DeployFolder`, `string? DownScriptPath`, `IReadOnlyList<DeployRisk> Risks`. The dialog's idle panel gains one line — *"Script di annullamento: down.sql"* — and the outcome panel a neutral 32 px button **"Carica script di annullamento"** that opens the folder / loads `down.sql` into the script view; the user then runs it through the same execute path (or `dbdelta apply --script down.sql`). One extra click versus nothing today.

Copy must never let the user read "annullamento" as "backup". One sentence, next to the button: *"Ripristina la struttura, non i dati: righe eliminate da DROP TABLE/COLUMN e valori troncati non tornano."*

**Effort: M.** Files: `DeployScriptBuilder.cs`, `ScriptGenerator.cs` (signature only), `AppStateViewModel.cs`, `MainWindowViewModel.cs`, `ConfirmExecuteViewModel.cs`, `ConfirmExecuteDialog.axaml`.

**The one test:** new `tests/DbDelta.Providers.LiveDb.IntegrationTests/DownScriptRoundTripTests.cs`, cloning the harness in `DependencyRoundTripTests.cs`: `LoadAsync(target)` → keep as `before` → apply `up.sql` → apply `down.sql` → `LoadAsync(target)` → `Compare(before, after)` must be all `Identical`, with **no kind filter** for the invertible kinds and an explicit allow-list for the non-invertible ones.

---

## Stage 2 — pre-deploy restore point

### What SQL Server gives you, and what it costs

**`BACKUP DATABASE [db] TO DISK = N'…' WITH COPY_ONLY, INIT, CHECKSUM`** — the only mechanism that undoes **data**. Constraints to design around:
- Requires `db_backupoperator` / `db_owner` / `sysadmin`. DbDelta may well be connected as a schema-only deployer.
- The file is written by the **SQL Server service account**, not the client. Never offer a client-side path picker; read the server default with `SELECT SERVERPROPERTY('InstanceDefaultBackupPath')` and let the user override with a server-visible path.
- **Cannot run inside the deploy transaction.** It must be its own `SqlExecutor.ExecuteAsync` call, completed and verified *before* the deploy script runs, aborting the deploy on failure.
- **Not supported on Azure SQL Database at all.** Detect via `SELECT SERVERPROPERTY('EngineEdition')` — reuse the existing `SqlServerDiscovery.GetServerVersionAsync` shape (`SqlServerDiscovery.cs:261-284`), which already does exactly this kind of probe.
- `COPY_ONLY` is mandatory: it keeps the customer's log chain and differential base intact.

**`CREATE DATABASE X AS SNAPSHOT OF Y`** — tempting, wrong as the primary mechanism: Enterprise/Developer only, same instance only, pins the source database's files, incompatible with FILESTREAM, and reverting drops every other snapshot and breaks the log chain. Offer it, if ever, as an opt-in fast path — never as the default.

**Schema-only snapshot** (serialize `Database` to gzipped JSON next to `down.sql`) restores **nothing** of data. Its real value is different and worth having later: `down.sql` is frozen at generation time, so if the target drifts before the user decides to undo, replaying it may fail or clobber. A retained pre-deploy model lets a *fresh, drift-aware* inverse be generated at undo time. The cost is `[JsonPolymorphic]` discriminators on the `Constraint` hierarchy (`PrimaryKey`/`UniqueConstraint`/`CheckConstraint`/`DefaultConstraint`/`ForeignKey`) — which is the same work the long-planned `.snp` Snapshot provider needs, so do it once for both. **This is Stage 2b, effort L. Not the MVP.**

### What CANNOT be undone by DDL alone

State this list verbatim in the dialog, per affected object, *before* the click:

dropped table rows · dropped column values · a narrowed type (truncated strings, silently rounded decimals) · columns dropped by the rebuild path · a sequence's current value · anything the readers never modelled (columnstore indexes, masking, temporal versioning) and therefore cannot re-emit.

The gate: `ConfirmExecuteViewModel` gains `bool BackupFirst` — pre-checked and **non-clearable** whenever the risk list contains a data-destroying entry. When the instance cannot back up (Azure SQL DB, missing rights, unwritable path), show *"backup non possibile su questa istanza"* and require the user to type the target database name to proceed. `RESTORE` is deliberately out of scope: DbDelta records the backup path in `meta.json` and a DBA acts. A tool that can restore a production database is a different product with a different blast radius.

**Effort: M.** Files: `src/DbDelta.Persistence/Sql/SqlExecutor.cs` (no change — reuse it), new `src/DbDelta.Persistence/Sql/BackupRunner.cs` (~60 lines: capability probe + path resolution + the one `BACKUP` statement), `ConfirmExecuteViewModel.cs`, `ConfirmExecuteDialog.axaml`.

**The one test:** `tests/DbDelta.Providers.LiveDb.IntegrationTests/BackupGateTests.cs` — point the backup at an unwritable path, assert the deploy **never ran** (the object the script would have created is absent) and that the failure message names the backup, not the DDL.

---

## Stage 3 — deployment journal

No database. Copy `JsonRecentProjectsStore` verbatim — it already has the exact shape needed: `schemaVersion`, `JsonSerializerDefaults.Web`, `WriteAtomicAsync` (temp file + `File.Move(overwrite)`), a `CreateDefault()` that computes its own `%LOCALAPPDATA%\DbDelta` subfolder, and a static `Changed` event for live UI refresh.

New file `src/DbDelta.Persistence/Json/JsonDeployJournal.cs`:
- **Index:** `%LOCALAPPDATA%\DbDelta\deploys\index.json` — one entry per run: folder name, UTC + server-clock timestamps, redacted source/target, object count, risk codes, `SqlBatchResult` (success, batches, ms, `RolledBack`), `downScriptAvailable`, `backupPath`. Uncapped (a deploy history that silently forgets is not a history) — cap the *UI* list, not the file.
- **Payload:** the per-run folder from Stage 1 (`up.sql`, `down.sql`, `meta.json`, later `pre-deploy.model.json.gz`).

The `SqlBatchResult` is appended after the dialog closes, so a crash mid-deploy leaves an entry marked incomplete — which is itself the answer to the indeterminate-COMMIT case.

UI: a **"Cronologia deploy"** toolbar button opening a list; each row offers *Apri cartella*, *Vedi up.sql*, and *Annulla questo deploy* (loads `down.sql` into the execute flow, target pre-filled from `meta.json`, with a hard warning when the run being undone is not the most recent one — undoing #N while #N+1 exists is only safe if their object sets are disjoint, and the journal has the object lists to check that cheaply).

**Effort: S** (the store) **+ S** (the dialog). Files: new `src/DbDelta.Persistence/Json/JsonDeployJournal.cs`, new `src/DbDelta.App.Avalonia/{ViewModels/DeployHistoryViewModel.cs, Views/DeployHistoryDialog.axaml}`, `MainWindowViewModel.cs`, `MainWindow.axaml`.

**The one test:** `tests/DbDelta.Persistence.UnitTests/Json/JsonDeployJournalTests.cs`, mirroring `JsonConnectionStoreTests` — write two runs, reload, assert newest-first order, that a corrupt `index.json` degrades to empty rather than throwing (the pattern at `JsonRecentProjectsStore.cs:100-103`), and that the resolved `down.sql` path exists.

---

## Deployment warnings taxonomy

Cross-cutting; **build it first inside Stage 1**, because Stage 2's backup gate and the CLI's `--abort-on-warnings` both consume it. One classifier over the same `selectedPairs` the builder already receives — no re-parsing of emitted SQL, no second source of truth. `docs/01_architecture.md:1154` already specifies this and names the exact severities; `grep AbortOnWarnings src/` returns nothing.

New `src/DbDelta.Core/ScriptGen/DeployRisk.cs`:

`record DeployRisk(ObjectIdentity Target, RiskKind Kind, string Detail, bool Reversible)` and `static IReadOnlyList<DeployRisk> Classify(IReadOnlyList<DifferencePair> pairs)`, walking exactly the branches `TableScriptEmitter.EmitAlter`/`EmitDrop` take:

| RiskKind | Trigger, computed from the pair alone | Reversible by `down.sql`? |
|---|---|---|
| `DropTable` | `OnlyInB` + Kind `Table` | structure yes, **rows no** |
| `DropColumn` | `Different`, a `SideB` column absent from `SideA` | column yes (all-NULL), **values no** |
| `NarrowColumn` | same column, length/precision/scale shrinks (compare via `SqlTypeFormatter`) | **no** |
| `ComputedFlip` | the `EmitAlter` step-3 drop+add branch (`TableScriptEmitter.cs:234-243`) | **no** |
| `TableRebuild` | `RequiresFullRebuild` true (`TableScriptEmitter.cs:289`) | rows yes, **indexes/triggers/outbound FKs no** — the existing bug |
| `AddNotNullNoDefault` | new column, `!IsNullable`, no `DefaultExpression` | n/a — **will fail** on a populated table (Msg 4901) |
| `SequenceRestart` | `Sequence` `Different` with a `StartValue` change | **no** (current value never captured) |
| `EncryptedModule` | module `IsEncrypted` or `Body is null` | **no** — nothing emitted in either direction |
| `DropPrincipal` | `OnlyInB` User/Role | membership yes, SID no |

Keep the classifier **dumb and over-inclusive**: a false "this may lose data" costs a checkbox, a false negative costs a table.

**Pre-flight enrichment (optional, cheap, high-impact):** one read-only `SELECT SUM(rows) FROM sys.partitions WHERE index_id IN (0,1) AND object_id IN (…)` before the dialog turns *"drops `dbo.Archive_2019`"* into *"drops `dbo.Archive_2019` — 12.400.000 righe"*. That number is what makes an operator stop.

**Consumers:** `ConfirmExecuteViewModel.Risks` → a Warning-brush band above the action bar listing each risk (irreversible ones in `DangerBrush`), with `CanExecute` gated on a new `AcknowledgedRisk` checkbox; `BackupFirst` forced on when any risk is irreversible; `ApplyCommand`/`ScriptCommand` gain `--abort-on-warnings <high|medium|low>`.

**Effort: M.** Files: new `src/DbDelta.Core/ScriptGen/DeployRisk.cs`, `MainWindowViewModel.cs`, `ConfirmExecuteViewModel.cs`, `ConfirmExecuteDialog.axaml`, `src/DbDelta.Cli/Commands/{Apply,Script}Command.cs`.

**The one test:** a property test in `tests/DbDelta.Property.Tests/Properties/` over the existing `SchemaArbitraries` generators — *for any generated pair whose emitted script contains `DROP COLUMN`, `DROP TABLE` or `sp_rename`, `Classify` returns a non-empty risk list*. That is the assertion that keeps the classifier honest as the 13th emitter arrives; a hand-written `DeployRiskTests` fact per kind is the cheap companion.

---

## Sequencing and honest budget

**Stage 0 (S) → warnings taxonomy (M) → Stage 1 (M) → Stage 3 journal (S) → Stage 2 backup (M) → Stage 2b model snapshot (L).**

Warnings ship before Stage 1 deliberately: telling the user *"this drops 12M rows and cannot be undone"* is worth more than handing them a `down.sql` that cannot restore those rows anyway. The two together are the whole requirement — the warning prevents the mistake, the down script fixes the recoverable 90%, the backup covers the rest.

Two existing confirmed bugs are hard prerequisites, not nice-to-haves: `DeployScriptBuilder` must pass dependency edges (or `down.sql` is mis-ordered), and `EmitRebuild` must stop silently dropping indexes/triggers/FKs (or `down.sql` *causes* the loss it exists to reverse). Fix both inside Stage 1.