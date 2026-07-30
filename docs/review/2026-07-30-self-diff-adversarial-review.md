# Review adversariale del diff `19131d8..ed051a3` (2026-07-30)

Le 15 commit di questa ondata riviste da agent indipendenti a cui NON e stato detto
il razionale delle scelte: solo il diff e i tre requisiti di prodotto. 5 lenti
read-only + 1 agent in worktree isolato che ha **rimosso ogni fix e rieseguito il
suo test** per verificare empiricamente le regressioni dichiarate.

50 finding grezzi -> 46 sopravvissuti alla confutazione (critical: 3, high: 14, medium: 16, low: 13).

Verdetto e piano: sezione finale di questo file.

---

# Verdetto

# Refutation review — `19131d8..ed051a3` (15 commits, 15 files under `src/`)

## 1. Verdict

**SOUND WITH FIXES FIRST — three blockers.**

The pipeline's *shape* is right and the empirical revert pass proves most of this diff is real work: 8 of the 9 targeted fixes have a test that genuinely fails when the fix is removed, and `ScriptGenerator.Generate` already carries the exact `(result, selection)` split that the "rescue an untouched object" logic needs. The ordering rationale is coherent and the FK-drop-up-front restructuring is a genuine correctness win.

But it is not yet a foundation for undo generation, for one structural reason and two data-structure reasons:

- **`DeployScriptBuilder` throws away the full comparison** (`new ComparisonResult(selectedPairs)`, `selection: null`). Both new "scan `result.Differences` unfiltered to rescue an object the user never touched" passes are therefore **dead code on the GUI path** — the only path that executes against a live database. The trigger rescue added in `95313c4` cannot fire from the app under any normal user action. An inverse-script generator needs *exactly the same* full-result view (to know what the forward script destroyed collaterally), so building it on top of `Build(selectedPairs, …)` inherits the blindness on day one.
- **Two brand-new bookkeeping sets are keyed on a bare name** (`forcedIndexRecreates` on index name, `fkDropNames` on constraint name) when neither name is unique in a database. Both silently *skip a DROP the user asked for*. Undo bookkeeping is the same problem — "what did I drop, and on which table" — and a forward pass that cannot distinguish two `IX_TenantId` will produce an inverse script that cannot either.
- **`SqlBatchResult.RolledBack` reports `true` when nothing was rolled back.** That flag is precisely the input to "do I need to run the undo script?".

Everything else on the list is loud-failure or edge-case and can wait. Fix those three and this is a good base.

---

## 2. Must fix before undo work

### B1 — `DeployScriptBuilder` must pass the full comparison, not a synthetic one
`src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs:63`

```csharp
ComparisonResult syntheticResult = new(selectedPairs);
string body = _generator.Generate(syntheticResult, selection: null, …);
```

Inside `Generate`, `result.Differences` **is** the selection. Both unfiltered scans depend on the opposite:
- `ScriptGenerator.cs:350` — Identical triggers on a rebuilt table
- `ScriptGenerator.cs:159` — FKs held by an Identical table pointing at a dropped/rebuilt table

And an Identical pair can never enter the selection: `DifferenceRowViewModel.cs:140` `IsSelectable => !IsIdentical`, `_isSelected` defaults false, `SelectedPairs()` filters on `IsSelected` (`MainWindowViewModel.cs:673`). (The one accidental route is `ReapplySavedSelections` at :317-328, which iterates *all* rows unfiltered — that is a bug, not a feature.)

**Concrete damage today:** target has `dbo.Fatture` + a byte-identical audit trigger `trg_Fatture_Audit`; source adds IDENTITY to `Id` → `RequiresFullRebuild`. User ticks the one Fatture row, presses Esegui. Script emits `DROP CONSTRAINT PK_Fatture` / `CREATE TABLE Fatture_tmp` / `INSERT…SELECT` / `DROP TABLE Fatture` / `sp_rename` — and no trigger batch, because the rescue loop iterated zero times. `SqlBatchResult.Success = true`. Production silently loses an audit trigger.

**Fix (one line, everything already exists):** `AppState.LastComparisonRaw` already holds the full `ComparisonResult` (`AppStateViewModel.cs:246`). Change `Build` to take it and call `_generator.Generate(fullResult, selection: selectedPairs, …)`. The CLI already does exactly this (`ScriptCommand.cs:79-85`) and is correct. **Effort: M** (signature + 2 call sites + one test that goes *through* `DeployScriptBuilder`).

### B2 — `forcedIndexRecreates` must be keyed by (schema, table, index)
`src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:204, 217, 323, 846, 856`

The sibling collection one line above gets it right — `blockingIndexDrops.Add((sideB.Schema, sideB.Name, ix))` — then the restore set stores `ix.Name` alone and the *same global set* is handed to every Different table's `EmitIndexDelta`. Index names are unique per `object_id`, not per database; `IndexReader` excludes PK/UQ-backing indexes, so what remains is exactly the user-named `IX_*` population where `IX_TenantId` / `IX_CreatedAt` on two tables is routine.

- **Loud half:** table B's identical `IX_TenantId` gets `mustRestore = true` → `CREATE INDEX` over an index that still exists → Msg 1913 → whole deploy rolls back.
- **Silent half:** table B's `IX_TenantId` was *removed* in the source → `alreadyDropped.Contains(t.Name)` skips the DROP → the index survives in production, tool reports success, next compare still shows Different.

**Fix:** `HashSet<(string,string,string)>`, and test membership with `(src.Schema, src.Name, name)` inside `EmitIndexDelta`. **Effort: S.** Introduced by `275660a`; no test can see it because every fixture is single-table.

### B3 — `fkDropNames` dedupe, and the false comment justifying it
`src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:132-139`

```csharp
// Constraint names are database-scoped, so the name alone dedupes.
if (fkDropNames.Add(fk.Name)) { fkDrops.Add((schema, table, fk)); }
```

The comment is wrong. Constraints are `sys.objects` rows carrying the parent table's `schema_id`; `dbo.FK_Righe_Testa` and `sales.FK_Righe_Testa` coexist legally. Two Different tables in different schemas that both lost an identically-named FK produce two `AddFkDrop` calls, the second returns false, one `DROP CONSTRAINT` is emitted, and nothing else re-emits it (`EmitFkAdds` only adds). **The FK the source removed survives in production and the tool reports success.**

**Fix:** key on `(schema, table, name)`; delete the comment (and the identical false claim at `TableScriptEmitter.cs:447`). **Effort: S.** Introduced by `ec6eb83`. Fix the comments especially — the undo work will read them as spec.

### B4 — `RolledBack` must not claim a rollback that did not happen
`src/DbDelta.Persistence/Sql/SqlExecutor.cs:189-201`

The `tx is null` branch runs `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;` and returns `true` whenever the *command* executes. One branch, two meanings, no discriminator:
- DbDelta script with its own envelope → `@@TRANCOUNT == 0` means XACT_ABORT already rolled everything back → `true` is correct.
- `--no-transaction` → `@@TRANCOUNT == 0` means there never was a transaction and everything auto-committed → `true` is a lie.

The author's own new acceptance test `No_transaction_opt_out_leaves_the_earlier_batches_applied` creates exactly this state (asserts `dbo.KeptBatch` survives) and never inspects stdout. `dbdelta apply --no-transaction` prints `{"success":false,"rolledBack":true,"transaction":"none"}` for a permanently half-migrated target. The record's own doc says `RolledBack` means "the target is known to be unchanged".

**Fix:** `ExecuteScalar` on `IF @@TRANCOUNT > 0 BEGIN ROLLBACK TRANSACTION; SELECT 1 END ELSE SELECT 0` and return that; return `false` whenever batches ran outside any transaction. **Effort: S.** Assert the JSON field in both new acceptance tests.

---

## 3. Should fix soon

| # | What | Where | Why it matters |
|---|---|---|---|
| S1 | **A DEFAULT-value-only change now drops the PK and every index on the column.** `ColumnShapeEqual` includes `ExpressionsEqual(DefaultExpression)`, so `((0))`→`((1))` puts the column in `touchedColumns`; `DependsOnColumn(PK)` fires and `blockingIndexDrops` fires — while section 3 emits nothing but a no-op `ALTER COLUMN`. Msg 3725 if the PK is FK-referenced; a full clustered rebuild inside a 60 s-capped batch otherwise. **This is a new regression** — pre-diff the same change emitted `DROP CONSTRAINT DF_…` + no-op ALTER + re-add and succeeded in milliseconds. The irony: `DependsOnColumn`'s own doc argues DEFAULTs must be excluded, and a DEFAULT change is what populates the set. | `TableScriptEmitter.cs:354-359`, `:531` | Split `ColumnShapeEqual` into `ColumnRequiresAlterColumn` (type/nullability/collation/computed/identity) for `touchedColumns`, and keep full equality for the comparison side. **S** |
| S2 | **No FK is ever dropped to free a column being retyped.** The up-front pass has three feeders, none keyed on `ColumnsDroppedOrAltered`; `EmitAlter` section 1 `continue`s on FKs; `DependsOnColumn` has no ForeignKey arm. So the canonical `int→bigint` widening dies — Msg 3725 on the new `DROP CONSTRAINT PK_Customer`, or Msg 5074 on the child's `ALTER COLUMN`. Pre-existing, but it is the exact migration `275660a` was written for. Note `EmitFkAdds` skips unchanged FKs, so a bare drop is not a complete fix — you need a `forcedFkRecreates` mirror. | `ScriptGenerator.cs:141-176` | **M** |
| S3 | **DROP pass "reverse-topological" order is vacuous** — every caller supplies *source*-side edges, and an `OnlyInB` object appears in none of them, so it lands in reversed `KindRank` (Function 5 before View 4 before Table 3). `AppStateViewModel.cs:87` already retains `TargetDependencies` and documents it as unused. Also: `DependencyResolver` discards ForeignKey edges and `DependencyReader` never emits any, so two FK-linked target-only tables drop in ordinal name order — a coin flip on Msg 3726. | `ScriptGenerator.cs:263`, `DependencyResolver.cs:62` | **M**. Fix before undo: a wrong forward order becomes a wrong rollback order. |
| S4 | **Two rebuild targets referencing each other** — `pointsAtRebuilt` skips the FK when holder *and* referenced are both rebuild targets, and nothing enforces the DROP TABLE order. `Cliente`/`Fattura` fails; rename them and it works. Drop the `!rebuildTargets.Contains(holder)` guard; an extra `DROP CONSTRAINT` is harmless. | `ScriptGenerator.cs:168` | **S** |
| S5 | **`CREATE SCHEMA` / `DROP SCHEMA` are selection-gated.** Tick only `vendite.Ordine` and the script opens with `CREATE TABLE [vendite].[Ordine]` → Msg 2760, the exact failure `3dd4d09` exists to prevent. Tick only the `legacy` schema row and you get `DROP SCHEMA` on a populated schema → Msg 3729. The generator's comment ("the objects a removed schema held are already gone") is only true for a full selection, which the app never has by default. Also: a target-only `cdc` schema passes `SchemaReader`'s filter and its `is_ms_shipped=1` contents are never dropped. | `ScriptGenerator.cs:230, 455` | **S** for the promotion; **M** with an `IF NOT EXISTS` guard on the DROP. |
| S6 | **A disabled trigger comes back enabled** whenever its body changed. The DISABLE guard was added at the rebuild call site only; `EmitCreateOrAlter` never looks at `IsDisabled`. Textbook one-guard-in-the-shared-function fix: move it into `EmitCreateOrAlter` and delete the duplicate at `ScriptGenerator.cs:361-366`. | `TriggerScriptEmitter.cs:30, 52` | **S** |
| S7 | **Rebuild `_tmp` carries no default at all**, so a source-only `NOT NULL` column with a named default makes the row-copy `INSERT` fail (Msg 515). Deliberate and documented, but the doc only reasons about columns the post-rename re-add covers — not about columns the INSERT needs a value for *before* the rename. | `TableScriptEmitter.cs:624-632` | **S** |
| S8 | **State-only Different trigger on a rebuilt table** emits a bare `ENABLE TRIGGER` against an object `DROP TABLE` just destroyed. The rescue filters `Status == Identical` on the stated grounds that Different triggers are re-emitted by the CREATE pass — `TriggerScriptEmitter.cs:48-52` falsifies that. | `ScriptGenerator.cs:351` | **S** |
| S9 | **`ScriptManagesItsOwnTransaction` false-positives** on any line whose first non-space token is `BEGIN TRAN` — indented module bodies (`CREATE PROCEDURE … BEGIN TRANSACTION;`), block comments, multi-line string literals. That population then runs with **no transaction** and is reported as `transaction:"script"`. Generated scripts are always classified correctly (`DeploymentScriptWriter` puts `BEGIN TRANSACTION` alone on a line), so the cheap correct fix is provenance, not syntax: emit `-- dbdelta:transaction=script` and match that. | `SqlExecutor.cs:265` | **M** |
| S10 | `DROP USER` in the prologue, `DROP SCHEMA` 220 lines later in the epilogue → Msg 15138 for a user owning a target-only schema. Correct order is object drops → schema drops → principal drops. Worth fixing now because undo must reverse this sequence. | `ScriptGenerator.cs:230 vs :455` | **S** |
| S11 | No identifier quoting anywhere — `$"[{schema.Name}]"` with no `]`→`]]` doubling. `grep` for `QuoteName|EscapeIdentifier` over `src/` returns nothing. Systemic, so fixing the new emitter alone buys nothing: add one `static string Q(string id)` and route every `[{…}]` through it. Breaches the stated "no injection through catalog values" requirement. | all emitters | **M** |
| S12 | `PermissionScriptEmitter`'s `_ => null` fallback (was `_ => "DATABASE"`) turns an unresolvable object permission into a **database-scoped grant** — `GRANT CONTROL TO [app_user];`. Reachable: `PermissionReader` LEFT JOINs `sys.objects`, which excludes system objects, so an `OBJECT_OR_COLUMN` grant on a system view resolves to NULL names. Previously a syntax error that aborted the deploy; now a silent privilege widening. | `PermissionScriptEmitter.cs:84` | **S** |
| S13 | `DescribeEndpoint` can print a password fragment as the server name (`Password="a;Server=hunter2"` with the keys reordered). Immune from the setup dialog, reachable from the raw connection-string TextBox and from `ConnectionStoreViewModel.MaterialiseAsync`'s `{PASSWORD}` template replace. Use `SqlConnectionStringBuilder.DataSource`/`InitialCatalog`. | `DeployScriptBuilder.cs:117` | **S** |
| S14 | `JsonRecentProjectsStore`'s future-schema gate returns empty *without* `MoveAside`, so a v2→v1 rollback overwrites the MRU with a one-entry v1 doc and no `.broken-*` copy. Its sibling does move the file aside — the parity claim is wrong on exactly this point. | `JsonRecentProjectsStore.cs:109` | **S** |
| S15 | `forWrite: true` rethrows into four unguarded `async void` / `AsyncRelayCommand` call sites with no global handler (`grep UnhandledException src` → nothing). Pre-existing, not introduced — but the commit message claims it is handled. | `ConnectionManagerDialog.axaml.cs:15/43/79`, `MainWindowViewModel.cs:228/308` | **M** |
| S16 | `Schema` is missing from `DifferenceRowViewModel.KindDisplayName` and `KindOrder`, so the row renders `Schema` / `sales.` and sorts at 99 while `KindCatalog` says `Schemi` / 0. Second instance of the same divergence (`TableType` was already missing) — delete both switches and delegate to `KindCatalog`. Straight DRY violation per the project rules. | `DifferenceRowViewModel.cs:88, 111` | **S** |

---

## 4. Worthless tests

These are the most corrosive items here, because they make a green suite mean less than it appears to. Four were **empirically proven** dead by removing the fix and watching the test pass.

| Test | Status |
|---|---|
`ExecuteAsync_rejects_a_negative_command_timeout` (`SqlExecutorTests.cs:155-167`) | **Proven dead.** `void` method, `act.Should().ThrowAsync<…>()` returns a `Task` that is discarded. Guard deleted → test green. No CS4014 (method isn't `async`), no FluentAssertions.Analyzers, so `TreatWarningsAsErrors` doesn't catch it. Fix: `async Task` + `await`, then grep the whole suite for bare `.ThrowAsync<`/`.NotThrowAsync<`.
`A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end` (`ForeignKeyDropOrderingTests.cs:126`) | **Proven dead.** Passes with the entire `ec6eb83` reordering reverted — the old `EmitFkDelta` emitted DROP then ADD into one late batch, so `addFk > dropFk` was already true. Name and class doc claim it pins the new pass structure; it pins nothing. Anchor it against another pass (`"Dropping foreign keys" < dropFk < "Adding foreign keys"`) or rename it.
`A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk` | **Proven dead** against the pre-fix behaviour. Defensible as a forward guard on the new `droppedTables.Contains(holder)` skip, but it contributes nothing to "4 tests" — effective regression coverage of `ec6eb83` is **2**, not 4.
`Schemas_present_on_both_sides_produce_no_row` (`SchemaEmissionTests.cs:62`) | Passes with `CompareSchemas` deleted from the engine entirely. Not *vacuous* (it would fail if the comparer started emitting Identical pairs), but it cannot catch a refactor that drops the feature. Make it positive-and-negative in one arrange.
Assertion 3 of `Rebuild_recreates_every_index_including_the_identical_ones` (`TableRebuildPkSwapTests.cs:288`) | `NotContain("DROP INDEX [IX_Invoice_Amount]")` — the delta path never emits a DROP for an identical index, so this can't fail. Worse, the justifying comment ("the delta path would have emitted a DROP for it") states **the opposite of what the code does**, and that misreading is exactly why the re-emission pass was needed. Delete both or make the two sides differently shaped.
`ScriptManagesItsOwnTransaction` comment-immunity theory case (`SqlExecutorTests.cs:149-151`) | The comment claims "a mention inside a comment or a name must not count as an opener"; the one case tested (`SELECT 1; -- BEGIN TRANSACTION …`) passes only because `-` is not whitespace. Block comments, indented procedure bodies, and multi-line literals all match. The claim is false and untested.
`Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled` | Not dead — it does fail when the pass is removed. But it builds an input shape the GUI **can never produce**, so it proves the CLI works and says nothing about the path users run. Same for `Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table`.
**Worse than a worthless test:** the headline half of `95549e3` | Deleting `AppState.SourceDependencies` from **both** `MainWindowViewModel` call sites leaves Core 312/312, Headless 52/52, Golden 31/31 green. The commit's whole point was that only the GUI path was broken; that half has **zero** coverage. Cheapest guard: drop the `= null` default on `DeployScriptBuilder.Build`'s `dependencies` parameter so no call site can omit it silently.

The revert pass also established that **no golden test moved for any of the seven reverts**. The goldens do not cover the rebuild temp-table shape, the FK-drop reordering, or the blocking-index pass. The unit tests added by these commits are the only thing pinning emission order.

---

## 5. Overstated claims

> `ec6eb83` — *"drop every foreign key before dropping or altering anything"*; class doc: *"**EVERY** foreign-key drop, before any object is touched … both DROP TABLE and ALTER COLUMN are blocked by one"*; `:236` *"an ALTER COLUMN cannot run while an FK constrains the column"*.

**FALSE.** Three FK classes are never dropped: FKs constraining/referencing a retyped column (no feeder keyed on `ColumnsDroppedOrAltered`; `EmitAlter` `continue`s on FKs; `DependsOnColumn` has no FK arm), FKs held by a table being dropped (skipped by design while DROP TABLE order is FK-blind), FKs between two rebuild targets. The ALTER-COLUMN half of the stated rationale is **unimplemented**. "Before any object is touched" is also loose — the batch follows `CREATE SCHEMA`, `CREATE/DROP USER` and roles.

> `95313c4` — *"identity rebuild no longer silently destroys indexes, triggers and FKs"*

**FALSE as shipped.** Indexes and the rebuilt table's outbound FKs are genuinely fixed (they read `pair.SideA`, always present). Triggers are not: the rescue reads `result.Differences` for `Identical` pairs, and the app can never supply one. Two-thirds true, wrong on the one third whose failure is silent.

> Class doc — *"DROP pass — removed objects in reverse-topological order (dependent-first) so a referenced object is never dropped before its dependents"*

**FALSE.** Edges are always source-side; an `OnlyInB` object appears in none of them, so the order is reversed `KindRank` — Function before View before Table. The pass is never actually dependency-ordered for the only status it handles.

> `ScriptGenerator.cs:134` / `TableScriptEmitter.cs:447` — *"Constraint names are database-scoped, so the name alone dedupes"* / *"SQL Server constraint names are DB-scoped"*

**FALSE.** Schema-scoped. This false premise is load-bearing for `fkDropNames` and `rebuildOrchestratedFkNames`. (The belief probably comes from SQL Server's own misleading in-schema error text, "There is already an object named 'x' in the database".)

> `ed051a3` / `SqlBatchResult` doc — *"True when a rollback was issued and acknowledged, so the target is known to be unchanged … false when the failure left the outcome indeterminate"*; commit body: *"That flag is the point: an operator has to be able to tell 'nothing was applied' from 'we do not know'."*

**FALSE.** The flag cannot make that distinction and gets it wrong in the dangerous direction. The commit's own new acceptance test creates the state it misreports.

> `ed051a3` — *"which was the **only** genuine half-migration hole in the product"*

**OVERSTATED.** The hole reopens on every `ScriptManagesItsOwnTransaction` false positive, and a script generated with `NoTransactions` now gets a client transaction that the script's own `IF @@TRANCOUNT > 0 ROLLBACK` will roll back behind the client's back.

> Class doc order list (9 items)

**OVERSTATED.** There are **ten** passes — the "Dropping indexes on columns being altered" batch at `:250` is absent from the list. Fix before the undo work reads this doc as the spec.

> `3dd4d09` — *"The reader already excludes the system schemas … so no `DROP SCHEMA [db_datareader]` is reachable."*

**OVERSTATED.** True for that literal statement, false as the general safety argument it is used for: it is a 13-name blacklist that misses feature-provisioned schemas whose contents are `is_ms_shipped = 1` (`cdc`), and `NOT LIKE 'db[_]%'` over-excludes a user schema named `db_stage` while the object readers still return its tables — re-opening the Msg 2760 hole the commit exists to close. Use `sys.database_principals.is_fixed_role = 1`, which is the exact check the heuristic approximates.

> `7fb6a8d` — *"That throw surfaces through the existing `LastError` channel."*

**FALSE.** One of five mutating call sites is covered.

> `7fb6a8d` — *"brings the MRU store up to its sibling's hardening"*

**OVERSTATED.** `UnixCreateMode` is exact parity. The future-schema gate is not: the sibling moves the file aside, the MRU store just discards.

Two claims verified **TRUE** and worth keeping: `275660a`'s index/key/CHECK statement is literally accurate and its `ColumnDependencyOrderingTests` assertions are real (every `IndexOf` is guarded with `BeGreaterThan(0)` before comparison). `622e453`'s grammar argument for re-baselining the golden is correct — `DEFAULT` genuinely is absent from the `table_constraint` grammar, and the old `.verified.txt` was invalid T-SQL.

---

## 6. What is genuinely still untested

1. **`DeployScriptBuilder` — the app's only path — with any Identical-object orchestration.** Every new test calls `ScriptGenerator.Generate` directly with a hand-built full result, i.e. the CLI shape. Nothing exercises the shape the GUI actually produces.
2. **The app passing dependency edges.** Deleting both arguments leaves 395 tests green (empirically confirmed).
3. **Anything multi-table with a shared index name**, or **multi-schema with a shared constraint name.** Every fixture is single-table / single-schema, which is exactly why B2 and B3 are invisible.
4. **FK + `ALTER COLUMN` in any combination.** `ColumnDependencyOrderingTests` covers index / PK / UQ / CHECK only.
5. **A DEFAULT-value-only change** on a table with a PK or an index — the trigger for S1.
6. **The JSON `rolledBack` field.** `grep` shows exactly one reader (`ApplyCommand.cs:114`) and zero assertions anywhere.
7. **`ScriptManagesItsOwnTransaction` on a block comment, an indented module body, or a multi-line literal** — the three inputs where it returns the wrong answer.
8. **`PermissionScriptEmitter`'s `_ => null` fallback arm.** Both new permission tests use `ClassDesc = "DATABASE"`.
9. **A non-empty target-only schema** whose contents DbDelta does not model, and **a partial selection that ticks a schema row without its contents** — the two cases that turn `DROP SCHEMA` into a deploy blocker.
10. **`DescribeEndpoint` with a semicolon-bearing quoted password.** The existing test uses `Dev!Secret` / `Pr0d!Secret`.
11. **Rebuild + a new `NOT NULL` column with a named default** (S7), and **rebuild + a state-only Different trigger** (S8).
12. **The rebuild temp-table shape, the FK-drop reordering, and the blocking-index pass are pinned by exactly one unit test each** — the 31 golden tests cover none of them. That is the thinnest possible margin on the most order-sensitive code in the product, and it is the code undo generation will be built against.

---

# Finding integrali

## [critical] EO-1 · emission-ordering

**The new "restore Identical triggers after a rebuild" pass is dead code on the GUI deploy path — the trigger is still silently destroyed**

- file: `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs`:63 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

DeployScriptBuilder.Build: `ComparisonResult syntheticResult = new(selectedPairs); string body = _generator.Generate(syntheticResult, selection: null, ...)`. ScriptGenerator.cs:350 relies on the opposite: `foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Trigger" && x.Status == DifferenceStatus.Identical))` with the comment "Scanning result.Differences unfiltered is the same trick the inbound-FK orchestration uses." In the GUI, result.Differences IS the selection: MainWindowViewModel.cs:673 `SelectedPairs() => [.. Rows.Where(r => r.IsSelected).Select(r => r.Pair)]`, and DifferenceRowViewModel.cs:140 `public bool IsSelectable => !IsIdentical;` (checkbox hidden for Identical rows), with `private bool _isSelected;` defaulting to false. So an Identical pair can never enter selectedPairs, and the unfiltered scan finds nothing.

**Scenario**

GUI: dbo.Invoice gains IDENTITY on Id (Different → RequiresFullRebuild). dbo.trg_Invoice_Audit is byte-identical on both sides, so its row is rendered non-selectable. User ticks the Invoice row and presses Esegui. Emitted script: DROP CONSTRAINT PK_Invoice / CREATE TABLE Invoice_tmp / INSERT / DROP TABLE Invoice / sp_rename / ADD CONSTRAINT PK_Invoice — and nothing else. DROP TABLE took trg_Invoice_Audit with it; the "Re-creating triggers on rebuilt tables" batch is never emitted because rebuildTargets.Count > 0 but the Identical trigger pair is not in result.Differences. Script prints 'The database update succeeded', the next compare reports the trigger Identical (both sides read from... no — the trigger is gone from the target, so it reports OnlyInA), and production has silently lost an audit trigger. The same dead scan also kills case (b) of the FK drop pass (FK held by an Identical table pointing at a dropped/rebuilt table → Msg 3726), which the author's own review already logged as W1-6.

**Fix**

Generate() must receive the full ComparisonResult plus the selection, not a synthetic result built from the selection: pass `AppState.LastComparisonRaw` as `result` and `selectedPairs` as `selection` (the parameter already exists and is already used that way by the CLI). Then `pairs` stays the user's intent while the unfiltered `result.Differences` scans see Identical objects. Add a DeployScriptBuilder test whose ComparisonResult contains an Identical trigger that is NOT in selectedPairs — today's TableRebuildPkSwapTests.Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled builds a shape the GUI can never produce, so it cannot catch this.

**Verifica adversariale**

Reproduced against real code. DeployScriptBuilder.cs:63 builds `new ComparisonResult(selectedPairs)` and passes `selection: null`, so inside Generate both `pairs` and `result.Differences` are the selection. MainWindowViewModel.cs:673 `SelectedPairs() => Rows.Where(r => r.IsSelected)`; DifferenceRowViewModel.cs:32/140 `_isSelected` defaults false and `IsSelectable => !IsIdentical` (checkbox IsVisible binding, ResultsGridView.axaml:200); grep shows no SelectAll/bulk setter anywhere in the app (only reads plus the per-row restore at MainWindowViewModel.cs:325, which restores saved flags for rows the user ticked). So no Identical pair can ever reach the synthetic result, the unfiltered scan at ScriptGenerator.cs:350 yields nothing, and the 'Re-creating triggers on rebuilt tables' batch is never emitted on the GUI path — while EmitRebuild (TableScriptEmitter.cs:488) still emits DROP TABLE. The CLI is fine: ScriptCommand.cs:81 passes the whole ComparisonResult, and every new test (TableRebuildPkSwapTests, ForeignKeyDropOrderingTests) calls Sut.Generate(result) directly, i.e. only the CLI shape; DeployScriptBuilderTests never passes an Identical pair, so nothing guards the GUI path. The secondary claim also holds: the case-(b)/(c) holder scan at ScriptGenerator.cs:159 reads the same `result.Differences`, so an FK held by an Identical table pointing at a dropped/rebuilt table is not dropped in the GUI → Msg 3726. 'regression-from-this-diff: false' is right in substance — the silent trigger loss predates the diff; what is new is a fix that only covers one of the two callers. Severity critical stands: silent destruction of a production trigger with a 'succeeded' verdict, on the primary UI.

---

## [critical] SL-1 · silent-loss

**The app path can never see an Identical pair, so the new rebuilt-table trigger restore is dead code — production triggers are destroyed and the deploy reports success**

- file: `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs`:63 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

DeployScriptBuilder.cs:63  `ComparisonResult syntheticResult = new(selectedPairs);`  →  ScriptGenerator.cs:350-354  `foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Trigger" && x.Status == DifferenceStatus.Identical)) { if (p.SideA is not Trigger trg) continue; if (!rebuildTargets.Contains((trg.ParentSchema, trg.ParentTable))) continue; ... }`  and the app's only two call sites, MainWindowViewModel.cs:603 and :636, pass `SelectedPairs()` = `[.. Rows.Where(r => r.IsSelected).Select(r => r.Pair)]` (:672-673), while DifferenceRowViewModel.cs:140 defines `public bool IsSelectable => !IsIdentical;` — the checkbox is hidden for Identical rows.

**Scenario**

Prod target has `dbo.Fatture(Id int NOT NULL, …)` plus an audit trigger `trg_Fatture_Audit` that is byte-identical on both sides. Source adds IDENTITY to `Id` → `RequiresFullRebuild` = true. In the GUI the user ticks the single `dbo.Fatture` row (Different) and presses Esegui. `DeployScriptBuilder.Build` wraps ONLY that pair in a synthetic `ComparisonResult`, so inside `Generate` `result.Differences` has exactly one element and contains no Trigger pair at all — the restore loop iterates zero times. The emitted script runs `ALTER TABLE … DROP CONSTRAINT [PK_Fatture]` / `CREATE TABLE [dbo].[Fatture_tmp]` / `INSERT … SELECT` / `DROP TABLE [dbo].[Fatture]` / `sp_rename`. `DROP TABLE` takes `trg_Fatture_Audit` with it and nothing re-creates it. `SqlBatchResult.Success = true`; the shell reports success. Every subsequent INSERT into Fatture is unaudited. Because Identical rows are literally unselectable, there is no user action that can make the restore fire — the fix in 95313c4 is unreachable from the application. The same dead scan breaks the ec6eb83 fix: the `if (droppedTables.Count > 0 || rebuildTargets.Count > 0)` holder loop at ScriptGenerator.cs:159 also reads `result.Differences`, so an FK held by an Identical table pointing at a table the user ticked for DROP is never dropped and `DROP TABLE` dies on Msg 3726 — exactly the case `ForeignKeyDropOrderingTests.Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table` claims to cover.

**Fix**

`ScriptGenerator.Generate` already has the right shape: `Generate(ComparisonResult result, IEnumerable<DifferencePair>? selection, …)` where `result` is the FULL comparison and `selection` is what to emit. Change `DeployScriptBuilder.Build` to take the full `ComparisonResult` (the app already holds it as `AppState.LastComparisonRaw`) plus the ticked pairs, and call `_generator.Generate(fullResult, selection: selectedPairs, …)` instead of `new ComparisonResult(selectedPairs)`. Add a regression test that goes through `DeployScriptBuilder` (not `ScriptGenerator` directly) with an Identical trigger on a rebuilt table — every existing test for these orchestrations bypasses the shipping path by handing `ScriptGenerator` a result that contains Identical pairs.

**Verifica adversariale**

Reproduced against the real code. `DeployScriptBuilder.Build` (src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs:63-68) builds `new ComparisonResult(selectedPairs)` and calls `Generate(syntheticResult, selection: null, …)`, so inside `Generate` `result.Differences == selectedPairs`. Both app call sites (MainWindowViewModel.cs:603, :636) feed it `SelectedPairs()` = `Rows.Where(r => r.IsSelected)` (:672-673), and Identical rows have no checkbox (`IsSelectable => !IsIdentical`, DifferenceRowViewModel.cs:140; ResultsGridView.axaml:201 binds IsVisible to it; the select-all loop at MainWindowViewModel.cs:210 filters on IsSelectable). So the new restore loop at ScriptGenerator.cs:347-372, which scans `result.Differences` for `Kind == "Trigger" && Status == Identical`, iterates zero times on the app path while `EmitRebuild` still emits `DROP TABLE`/`sp_rename` (TableScriptEmitter.cs:488-490). The trigger is destroyed and SqlBatchResult.Success is true. The CLI path is fine because ScriptCommand.cs:81-85 passes the full ComparisonResult. Two corrections, neither fatal to the finding: (1) the claim "there is no user action that can make the restore fire" is slightly overstated — `ReapplySavedSelections` (MainWindowViewModel.cs:317-328) iterates ALL Rows, not just selectable ones, so an object that was Different when the project was saved and is Identical on a later compare comes back with IsSelected = true, which is an accidental, not a usable, path; (2) the FK half of the evidence produces a loud Msg 3726 rollback, not silent damage, so it is a lesser problem than the trigger half. Regression flag `false` is right: pre-diff the trigger was destroyed on BOTH paths (there was no restore at all), so the outcome is pre-existing and what this diff shipped is a fix that is dead on the path users actually use. Note the fix is nearly free: `Generate` already takes a `selection` parameter, and the app already holds the full result in `AppState.LastComparisonRaw` — DeployScriptBuilder just never passes it. Critical stands: silent destruction of a production object with a success verdict.

---

## [critical] TQ-01 · test-quality

**Both new "scan result.Differences unfiltered" rescues are dead on the app's deploy path — the tests only exercise ScriptGenerator directly**

- file: `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs`:63 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

DeployScriptBuilder.Build: `ComparisonResult syntheticResult = new(selectedPairs); string body = _generator.Generate(syntheticResult, selection: null, options: OptionsFor(selectedPairs), dependencies: dependencies);`  — so inside Generate, `result.Differences` IS the selection. The two new rescues both depend on it holding Identical pairs: ScriptGenerator.cs:159 `foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Table"))` (FK drops for FKs held by an Identical table) and ScriptGenerator.cs:350 `result.Differences.Where(x => x.Identity.Kind == "Trigger" && x.Status == DifferenceStatus.Identical)`. Every new test calls `Sut.Generate(new ComparisonResult([...]))` with the full result — e.g. ForeignKeyDropOrderingTests.cs:507 `Fk_held_by_an_identical_table_is_still_dropped_...` and TableRebuildPkSwapTests.cs:787 `Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled`. Nothing in tests/ references DeployScriptBuilder together with an Identical pair, and MainWindowViewModel.cs:673 `SelectedPairs() => [.. Rows.Where(r => r.IsSelected).Select(r => r.Pair)]` never yields Identical rows in practice.

**Scenario**

App path. Target has `dbo.Invoice` with an unchanged trigger `trg_Invoice_Audit`; the source flips `Invoice.Id` to IDENTITY. User checks only the Invoice row (the trigger row sits Identical in the collapsed "Identici" group) and clicks "Allinea destinazione". Generated script: rebuild block with `DROP TABLE [dbo].[Invoice]` … `sp_rename`. The trigger pass finds no Identical Trigger pair in the synthetic result, so no re-create is emitted. Deploy reports success; the production trigger is gone with no undo. Second instance, same root cause: source deletes `dbo.Currency`, `dbo.Invoice` is Identical and holds `FK_Invoice_Currency`; user selects the Currency row; the script contains only `DROP TABLE [dbo].[Currency];` → Msg 3726 and the whole deploy rolls back.

**Fix**

Generate already supports the split: `Generate(result, selection, options, dependencies)` filters `pairs` from `selection` while keeping `result.Differences` for the unfiltered scans. Change DeployScriptBuilder.Build to take the full ComparisonResult plus the selected pairs and call `_generator.Generate(fullResult, selection: selectedPairs, …)`; MainWindowViewModel already holds `AppState.LastComparisonRaw`. Then add one test that drives DeployScriptBuilder (not ScriptGenerator) with an Identical trigger/FK-holder present in the full result but absent from the selection.

**Verifica adversariale**

Reproduced. DeployScriptBuilder.cs:63 builds `new ComparisonResult(selectedPairs)`, so inside Generate `result.Differences` IS the selection — the two unfiltered scans (ScriptGenerator.cs:159 and :350) can only see what the user checked. And an Identical row cannot be checked: DifferenceRowViewModel.cs:140 `IsSelectable => !IsIdentical`, ResultsGridView.axaml:201 hides the CheckBox on `IsSelectable`, `_isSelected` defaults false, and SelectedPairs() (MainWindowViewModel.cs:672) filters on IsSelected. ComparisonEngine DOES emit Identical Table/Trigger pairs (ComparisonEngine.cs:331, :619), so the rescues work for `dbdelta script` (ScriptCommand.cs:81 passes the whole result) and for the new unit tests — the app is the only surface where they are dead, and it is the surface the commit messages describe. Trigger scenario verified end to end: EmitRebuild emits `DROP TABLE` (TableScriptEmitter.cs:488), the Identical-only pass at :350 finds nothing, no CREATE OR ALTER is emitted, deploy succeeds, production trigger gone. One correction to the reviewer's absolutism: ReapplySavedSelections (MainWindowViewModel.cs:317-328) iterates ALL Rows unfiltered and assigns `row.IsSelected = sel`, so a stale project selection for an object that has since become Identical CAN push an Identical pair through — an accident, not a guard, and itself worth a look. Regression flag `false` is correct: pre-diff the trigger was lost too (git show 19131d8 has no trigger rescue at all), and pre-diff FK drops lived in the per-table EmitFkDelta so an Identical holder was never covered either. Severity critical stands — silent destruction of a production object with a success report.

---

## [high] EO-2 · emission-ordering

**No foreign key is ever dropped to free a column being retyped or dropped, so the canonical int→bigint widening still fails (Msg 5074 / 3725)**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:141 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

The up-front FK pass has exactly three sources: (a) `foreach ... x.Status == Different` → `if (!stillThere || !ForeignKeyShapeEqual(t, s!)) AddFkDrop(...)`; (b) `pointsAtDropped = droppedTables.Contains(referenced)`; (c) `pointsAtRebuilt = rebuildTargets.Contains(referenced) && ...`. There is no branch keyed on `TableScriptEmitter.ColumnsDroppedOrAltered`. EmitAlter section 1 explicitly cannot help: `foreach (Constraint oldC in oldT.Constraints) { if (oldC is ForeignKey) { continue; } ...}` (TableScriptEmitter.cs:226). grep for "DROP CONSTRAINT" across src/ returns only three emitters: ScriptGenerator.cs:243 (the three cases above), TableScriptEmitter.cs:237 (non-FK only) and :460 (rebuild, non-FK only). Yet the class doc asserts "Foreign-key DROP pass — EVERY foreign-key drop ... both DROP TABLE and ALTER COLUMN are blocked by one" and the inline comment repeats "an ALTER COLUMN cannot run while an FK constrains the column".

**Scenario**

Child side: source widens dbo.Invoice.CustomerId int→bigint; FK_Invoice_Customer(CustomerId)→Customer(Id) is unchanged on both sides. touched={CustomerId}; the index/PK/CHECK machinery fires, the FK does not. Script emits `ALTER TABLE [dbo].[Invoice] ALTER COLUMN [CustomerId] [bigint] NOT NULL;` → Msg 5074 "The object 'FK_Invoice_Customer' is dependent on column 'CustomerId'" + Msg 4922 → SET NOEXEC ON → whole deploy rolls back. Parent side, now newly reachable because of 275660a: source widens dbo.Customer.Id int→bigint; PK_Customer covers Id, so section 1 emits `ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [PK_Customer];` → Msg 3725 "The constraint 'PK_Customer' is being referenced by table 'Invoice', foreign key constraint 'FK_Invoice_Customer'. Cannot drop constraint." Widening a PK int to bigint is THE migration this pair of commits was written for, and it is unrunnable on any table that has children.

**Fix**

In the up-front pass, for every Different non-rebuild table compute `touched = ColumnsDroppedOrAltered(sideA, sideB)` (already computed a few lines below for indexes) and AddFkDrop for (i) every target-side FK of that table whose Columns intersect touched, and (ii) every FK held by ANY table whose ReferencedTable is this table and whose ReferencedColumns intersect touched. Then mirror forcedIndexRecreates with a forcedFkRecreates name set consumed by EmitFkAdds (`mustRestore`), otherwise EmitFkAdds skips the unchanged FK and the deploy silently ends without the constraint — the same trap the index path already had to solve. Test: the two scenarios above; neither is covered by ColumnDependencyOrderingTests, which only exercises index / PK / CHECK.

**Verifica adversariale**

Verified: the only FK-drop emitter is ScriptGenerator.cs:242 (grep 'DROP CONSTRAINT' across src/ returns exactly ScriptGenerator.cs:243, TableScriptEmitter.cs:237 and :460, the latter two skipping ForeignKey explicitly at :226 and via IsNamedNonFkConstraint at :505). Its three feeds are the ones quoted; none is keyed on ColumnsDroppedOrAltered, and ForeignKeyShapeEqual (:908) ignores column types, so an unchanged FK on a retyped column is never dropped. Child side: ALTER COLUMN on a column used in a FOREIGN KEY is disallowed by SQL Server → Msg 5074. Parent side: the new blocksColumnDdl branch (TableScriptEmitter.cs:231, new in 275660a) emits DROP CONSTRAINT [PK_…] which is refused while an FK references the key → Msg 3725. Two corrections. (1) The evidence's 'Parent side, now newly reachable because of 275660a' is wrong: before the diff the same widening emitted a bare ALTER COLUMN on a PK column and died with Msg 5074, so the migration was unrunnable before and after — pre-existing, as the flag already says. (2) There IS a narrow genuine regression the finding misses: SQL Server permits ALTER COLUMN that only lengthens a varchar/nvarchar/varbinary column used in a UNIQUE constraint, so that case succeeded pre-diff; now blocksColumnDdl drops the UNIQUE constraint and, if an FK references it, the deploy fails with Msg 3725 where it used to pass. Severity high stands (canonical int→bigint widening on any FK-constrained column aborts the whole deploy).

---

## [high] EO-3 · emission-ordering

**The DROP pass's "reverse-topological" order is vacuous: dependency edges are source-side only, and target-only objects have none**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:263 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

`foreach (ObjectIdentity id in createOrder.Reverse()) { ... if (pair.Status != DifferenceStatus.OnlyInB) continue; ...}`. createOrder comes from `new DependencyResolver().Order([...], dependencies)`, and every caller supplies SOURCE edges only: ScriptCommand.cs:85 `dependencies: srcResult.Value!.Dependencies`, MainWindowViewModel.cs:607/641 `AppState.SourceDependencies`. DependencyReader reads sys.sql_expression_dependencies of one database, so an object that exists only in the TARGET is in no edge at all: `if (!nodeSet.Contains(e.Dependent) || !nodeSet.Contains(e.Referenced)) { continue; }` plus the plain absence of rows. Every OnlyInB node therefore has in-degree 0 and lands in KindRank order — DependencyResolver.cs:19 `["Table"]=3, ["View"]=4, ["Function"]=5` — whose reverse drops Function BEFORE View and BEFORE Table. AppStateViewModel.cs:87 even retains TargetDependencies ("Not used by the forward script"), which is exactly the list this pass needs.

**Scenario**

Target has dbo.fnCalcolaImporto and dbo.Movimenti with computed column `Importo AS dbo.fnCalcolaImporto([Qta],[Prezzo])`; both were removed from the source. Both pairs are OnlyInB, no edges exist for either. createOrder = [Movimenti(3), fnCalcolaImporto(5)]; reversed = [fnCalcolaImporto, Movimenti] → `DROP FUNCTION [dbo].[fnCalcolaImporto];` runs first → Msg 3729 "Cannot DROP FUNCTION 'dbo.fnCalcolaImporto' because it is being referenced by object 'Movimenti'" → whole deploy rolls back. Identical failure for a WITH SCHEMABINDING view over a dropped function, and for a CHECK constraint that calls one.

**Fix**

Thread the target-side edge list into Generate (it is already collected and retained) and build the DROP order from `new DependencyResolver().Order(onlyInBIdentities, targetDependencies).Reverse()`, keeping the source-edge order for the CREATE pass. Also fix the fallback ranks used for drops: View(4) before Function(5) is wrong for CREATE too — the DeployScriptBuilderTests comment admits "a new view that selects from a new function emits first and the deploy dies on Msg 208" whenever edges are missing.

**Verifica adversariale**

Mechanism verified end to end. Every caller supplies source-side edges only (ScriptCommand.cs:85 srcResult.Dependencies; MainWindowViewModel.cs:608/641 AppState.SourceDependencies, with TargetDependencies parked at AppStateViewModel.cs:87 and documented as unused by the forward script). DependencyReader reads sys.sql_expression_dependencies of one database and emits only ModuleReference edges — no FK edges at all — so an OnlyInB object appears in no edge, hits inDegree 0 in DependencyResolver.Order, and lands in CompareNodes order (KindRank Table=3, View=4, Function=5). createOrder.Reverse() at ScriptGenerator.cs:263 therefore drops Function before View and before Table: a target-only scalar UDF used by a target-only computed column or a schema-bound view dies on Msg 3729 and rolls the deploy back. Correctly flagged pre-existing: git show 19131d8 of ScriptGenerator.cs has the identical `foreach (ObjectIdentity id in createOrder.Reverse())` DROP pass. Severity lowered to medium: the failing shape needs a hard schema-bound cross-kind dependency between two objects both removed in the same deploy, and it fails loudly with a full rollback rather than silently. Note the root cause is shared with EO-7, whose table↔table sub-case is the far likelier trigger of the same bug.

---

## [high] EO-4 · emission-ordering

**forcedIndexRecreates is keyed by index name alone, but index names are only unique per table — a collision forces a duplicate CREATE INDEX and suppresses a needed DROP**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:217 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`HashSet<string> forcedIndexRecreates = new(StringComparer.Ordinal); ... blockingIndexDrops.Add((sideB.Schema, sideB.Name, ix)); forcedIndexRecreates.Add(ix.Name);` — the drop list is table-qualified, the recreate set is not. It is then handed to EVERY Different table: line 323 `string indexDelta = EmitIndexDelta(tSrc, tTgt, forcedIndexRecreates);`, where line 846 `if (alreadyDropped is not null && alreadyDropped.Contains(t.Name)) { continue; }` skips that table's DROP and line 856-859 `bool mustRestore = alreadyDropped.Contains(s.Name); ... if (existsOnTarget && !shapeChanged && !mustRestore) { continue; }` forces its CREATE. sys.indexes uniqueness is (object_id, index_id/name), i.e. per table, so IX_Data on two tables is legal and common.

**Scenario**

dbo.Ordini (Different): CustomerId int→bigint, target index IX_Data covers it → dropped up front, forcedIndexRecreates={"IX_Data"}. dbo.Fatture (Different for an unrelated added column) also has an index named IX_Data, byte-identical on both sides. Its delta would normally emit nothing; mustRestore is now true, so the script emits `CREATE NONCLUSTERED INDEX [IX_Data] ON [dbo].[Fatture] ([DataDoc]);` for an index that still exists → Msg 1913 "An index or statistics with name 'IX_Data' already exists on table 'dbo.Fatture'" → NOEXEC ON → the entire deploy rolls back; the user cannot deploy at all. Mirror case, silent: if the source instead REMOVED dbo.Fatture.IX_Data, the DROP loop skips it as "already dropped", nothing recreates it, the script reports success, and the index the user asked to remove is still in production.

**Fix**

Key the set by (Schema, Table, IndexName) — `HashSet<(string,string,string)>` — and pass EmitIndexDelta a per-table projection (or pass src.Schema/src.Name into the lookup). One line at each of the three sites; the existing tests all use a single table so they cannot see the collision.

**Verifica adversariale**

Code matches the quote exactly. blockingIndexDrops stores (sideB.Schema, sideB.Name, ix) but forcedIndexRecreates stores the bare ix.Name (ScriptGenerator.cs:203-218, both new in 275660a), and the same set is handed to EmitIndexDelta for every Different table (:323). Inside EmitIndexDelta the name-only membership test both suppresses a needed DROP (:846) and forces an unwanted CREATE (:856-859). SQL Server index names are unique per object, not per database, so two tables can legally carry IX_Data — the collision produces either Msg 1913 on a CREATE for an index that still exists (whole deploy aborts under SET XACT_ABORT/NOEXEC) or, in the mirror case, a silently skipped DROP so the index the user removed in the source survives in production while the script reports success. 'regression-from-this-diff: true' is correct — forcedIndexRecreates does not exist at 19131d8. Severity lowered to medium: it needs one Different table with a retyped/dropped indexed column plus a second Different table carrying an index of the same name in the same deploy, which most naming conventions (IX_<Table>_<Col>) make unlikely; the loud variant blocks the deploy rather than corrupting data.

---

## [high] EO-6 · emission-ordering

**A deliberately disabled trigger comes back ENABLED whenever its body changed — the DISABLE fix was applied at one call site only**

- file: `src/DbDelta.Core/ScriptGen/TriggerScriptEmitter.cs`:52 · effort S · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

ScriptGenerator.cs:359 adds, for the rebuild path, "CREATE OR ALTER always yields an ENABLED trigger; a trigger that was deliberately disabled must not come back enabled" followed by an explicit DISABLE TRIGGER emission. TriggerScriptEmitter.EmitDifferent only emits DISABLE/ENABLE when `bodiesMatch && sideA.IsDisabled != sideB.IsDisabled`; every other path ends at `return EmitCreateOrAlter(sideA);` (line 52) and `EmitCreateOrAlter` (line 30) emits only ModuleHeader.ToCreateOrAlterScript(...) — it never looks at t.IsDisabled. Same for `DifferenceStatus.OnlyInA when pair.SideA is Trigger t => EmitCreateOrAlter(t)`.

**Scenario**

dbo.trg_Ordini_Audit is DISABLED in both databases (switched off during a bulk load) and its body was edited in the source. ComparisonEngine reports Different; EmitDifferent sees bodies that do not match, so it emits `CREATE OR ALTER TRIGGER dbo.trg_Ordini_Audit ...` with no trailing DISABLE. SQL Server brings the trigger back ENABLED: from the next INSERT on production it fires again, writing audit rows (or raising the errors it was disabled to avoid) that nobody asked for. The script reports success. Same for a source-only trigger that is disabled in the source: it is created enabled.

**Fix**

Move the guard into the shared function: in `EmitCreateOrAlter(Trigger t)` append `DISABLE TRIGGER [t.Schema].[t.Name] ON [t.ParentSchema].[t.ParentTable];` when `t.IsDisabled`, then delete the duplicated block at ScriptGenerator.cs:361-366 — one guard where all three callers route through instead of one per caller.

**Verifica adversariale**

TriggerScriptEmitter.cs is byte-identical at 19131d8 (git diff over that path is empty), and reads exactly as quoted: EmitDifferent emits DISABLE/ENABLE only on the `bodiesMatch && IsDisabled differs` branch (:48), every other route returns EmitCreateOrAlter (:54), which only calls ModuleHeader.ToCreateOrAlterScript and never looks at IsDisabled — same for the OnlyInA arm (:20). ALTER (hence CREATE OR ALTER on an existing trigger) re-enables a disabled trigger, which is precisely what the author asserts in the new comment at ScriptGenerator.cs:359 while fixing only the rebuild call site. So a trigger disabled on both sides whose body changed, and a source-only disabled trigger, both come back enabled. Correctly flagged pre-existing. Severity lowered to medium: ClassifyTrigger (ComparisonEngine.cs:624-636) does compare IsDisabled, so the divergence is visible on the very next compare and the following deploy emits the DISABLE — a two-run convergence and a detectable wrong state, not a permanent silent one, and the disabled+body-changed combination is uncommon.

---

## [high] ER-1 · empirical-revert

**ExecuteAsync_rejects_a_negative_command_timeout passes with the guard deleted — un-awaited ThrowAsync**

- file: `tests/DbDelta.Persistence.UnitTests/Sql/SqlExecutorTests.cs`:166 · effort S · regressione di questo diff: **True** · verdetto **UNVERIFIED**

**Evidenza**

        Func<Task> act = () => SqlExecutor.ExecuteAsync(
            "Server=localhost;Database=X;Connect Timeout=1",
            "SELECT 1",
            CancellationToken.None,
            useOwnTransaction: true,
            commandTimeoutSeconds: -1);

        act.Should().ThrowAsync<ArgumentOutOfRangeException>();

// guard under test, src/DbDelta.Persistence/Sql/SqlExecutor.cs:
        ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);

**Scenario**

EMPIRICALLY CONFIRMED. I deleted `ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);` from SqlExecutor.ExecuteAsync, rebuilt, and ran `dotnet test tests/DbDelta.Persistence.UnitTests --filter ExecuteAsync_rejects_a_negative_command_timeout` → "Superato! Non superati: 0. Superati: 1". `ThrowAsync` returns a `Task<ExceptionAssertions<T>>` that is discarded, so the assertion body never executes. Without the guard, `commandTimeoutSeconds: -1` reaches `new SqlCommand(...) { CommandTimeout = -1 }`, which throws `ArgumentException` ("Invalid CommandTimeout value -1") from deep inside the batch loop, is swallowed by the `catch (Exception ex)` in ExecuteAsync, and is reported to the operator as a *deployment failure with an error message* rather than a caller bug — i.e. the `apply --command-timeout -1` path silently degrades instead of failing fast. The test that is supposed to pin this cannot fail for any reason whatsoever.

**Fix**

`await act.Should().ThrowAsync<ArgumentOutOfRangeException>();` and make the test `async Task`. Grep the rest of the suite for the same shape — any bare `.Should().ThrowAsync<` / `.Should().NotThrowAsync<` without `await` is dead.

---

## [high] ER-2 · empirical-revert

**A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end passes with the entire up-front FK drop pass reverted**

- file: `tests/DbDelta.Core.UnitTests/ScriptGen/ForeignKeyDropOrderingTests.cs`:127 · effort S · regressione di questo diff: **True** · verdetto **UNVERIFIED**

**Evidenza**

        int dropFk = sql.IndexOf(
            "ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];",
            StringComparison.Ordinal);
        int addFk = sql.IndexOf(
            "ADD CONSTRAINT [FK_Invoice_Currency] FOREIGN KEY",
            StringComparison.Ordinal);
        dropFk.Should().BeGreaterThan(0);
        addFk.Should().BeGreaterThan(dropFk);

**Scenario**

EMPIRICALLY CONFIRMED. I reverted commit ec6eb83's ScriptGenerator changes by hand (removed the up-front `fkDrops` collection and its "Dropping foreign keys" batch, restored `inboundFkDrops` + its batch after the DROP pass, and restored the DROP half inside `EmitFkAdds`), rebuilt, and ran the full Core suite: only 2 of the 4 ForeignKeyDropOrderingTests failed — `Fk_from_a_different_table_is_dropped_before_the_table_it_references` and `Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table`. `A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end` PASSED. It passes because the pre-fix `EmitFkDelta` emitted the DROP and the ADD into the *same late batch*, DROP first, so `addFk > dropFk` was already true. The test name and its comment ("The drop and the add are now in different passes") claim it pins the new pass structure; the assertion pins nothing beyond "drop precedes add", which no reachable code has ever violated. A future refactor that moves FK drops back to the end of the script — the exact Msg 3726 regression this commit fixed — would leave this test green.

**Fix**

Assert the *position relative to another pass*, which is what "up front" means: e.g. add an object DROP or an ALTER COLUMN to the fixture and assert `dropFk < indexOf("ALTER TABLE ... ALTER COLUMN")`, or assert the drop lands in the batch labelled "Dropping foreign keys" (`sql.IndexOf("Dropping foreign keys") < dropFk < sql.IndexOf("Adding foreign keys")`). Otherwise rename it to `A_reshaped_fk_is_dropped_before_it_is_re_added` so the name stops overstating.

---

## [high] ER-3 · empirical-revert

**Commit 95549e3's headline fix (app call sites pass dependency edges) is completely untested — reverting it leaves 395 tests green**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs`:608 · effort M · regressione di questo diff: **True** · verdetto **UNVERIFIED**

**Evidenza**

// Both call sites, the actual subject of the commit:
        string script = DeployScriptBuilder.Build(
            selected,
            AppState.SourceConnectionString ?? string.Empty,
            AppState.TargetConnectionString ?? string.Empty,
            DateTime.UtcNow,
            AppState.SourceDependencies);

// The only new test, which supplies the edges itself and never touches the app:
// tests/.../DeployScriptBuilderTests.cs:184
        string withEdges = DeployScriptBuilder.Build(
            [viewPair, fnPair], "src", "tgt", DateTime.UtcNow, [edge]);

**Scenario**

EMPIRICALLY CONFIRMED. Commit 95549e3's message says "Build now takes the edges and forwards them, and both app call sites pass AppState.SourceDependencies" — the whole point being that only the GUI path was broken ("Every cross-kind dependency the Kahn resolver was built for (#24) was silently inert on the GUI path; only the CLI ... was ordered correctly"). I deleted the `AppState.SourceDependencies` argument from BOTH call sites (`SaveScriptAsync` line 608 and the execute path line 641), leaving them exactly as they were at 19131d8 (`dependencies` defaults to `null`, so `ScriptGenerator` falls back to `dependencies ??= []`). Result: DbDelta.Core.UnitTests 312/312 PASS, DbDelta.App.HeadlessTests 52/52 PASS, DbDelta.ScriptGen.GoldenTests 31/31 PASS. Nothing anywhere notices. The new test only proves `DeployScriptBuilder.Build` *honours* edges when handed them; it never proves the app hands them over. Concretely: a source with a new `dbo.vFattureIva` selecting from a new `dbo.fnIva` — the app's "Genera script" emits CREATE VIEW before CREATE FUNCTION and the deploy dies on Msg 208 — and the regression is reintroducible by deleting one argument with a fully green suite. This matters more than usual because the commit body names inverse-script generation as the next consumer of this ordering.

**Fix**

Add a headless test on MainWindowViewModel: seed `AppState` via a fake `ISchemaSource` pair whose `Dependencies` contain a View→Function `ModuleReference` edge, invoke the script-generation command with both rows selected, and assert the function precedes the view in the produced text. Alternatively make the omission impossible: drop the `= null` default on `DeployScriptBuilder.Build`'s `dependencies` parameter so every call site must state its intent explicitly.

---

## [high] EXEC-1 · executor-transactions

**RolledBack reports true when nothing was rolled back and earlier batches ARE committed**

- file: `D:\Develop\AI\_ClaudeCode\SQL Compare\src\DbDelta.Persistence\Sql\SqlExecutor.cs`:189 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

TryRollbackAsync, tx == null branch:

    try
    {
        if (cn.State != System.Data.ConnectionState.Open) { return false; }
        await using SqlCommand rollback = new(
            "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", cn)
        { CommandTimeout = RollbackTimeoutSeconds };
        await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }
    catch
    {
        return false;
    }

The return value is `true` whenever the COMMAND executes, not whenever a rollback happens. `IF @@TRANCOUNT > 0 ROLLBACK` with @@TRANCOUNT = 0 is a successful no-op, and the method reports it as a rollback. The documented meaning of the flag (SqlExecutor.cs:15-21) is the opposite: "True when a rollback was issued and acknowledged, so the target is known to be unchanged." ApplyCommand.cs:114 prints it verbatim as `rolledBack`.

**Scenario**

Three reachable paths, all with tx == null so this branch runs:

(a) The author's own new acceptance test proves it. tests/DbDelta.Cli.AcceptanceTests/ApplyCommandTests.cs, `No_transaction_opt_out_leaves_the_earlier_batches_applied`: two plain batches, `--no-transaction`, batch 1 `CREATE TABLE dbo.KeptBatch` auto-commits, batch 2 fails with Msg 1767 (FK to dbo.NoSuchTable). XACT_ABORT is not set, the connection stays open, @@TRANCOUNT is 0, the no-op rollback succeeds -> RolledBack = true. The test itself asserts `dbo.KeptBatch` still exists. So `dbdelta apply` prints `"success": false, "rolledBack": true, "transaction": "none"` for a target that is permanently half-migrated. The test asserts only the exit code and the table, never the JSON, so it locks the lie in.

(b) No flag needed: a script whose first batches run before its BEGIN TRANSACTION, e.g.
    CREATE TABLE dbo.A (Id int);
    GO
    BEGIN TRANSACTION
    CREATE TABLE dbo.Bad (X int REFERENCES dbo.Nope(Id));
    COMMIT TRANSACTION
    GO
ScriptManagesItsOwnTransaction returns true (correctly), so useOwnTransaction = false. dbo.A auto-commits, batch 2 fails, the executor's rollback undoes only the script's transaction, dbo.A survives, and the report says rolledBack: true.

(c) Any script with two or more BEGIN/COMMIT pairs (two DbDelta scripts concatenated, a step-wise hand migration): step 1 commits, step 2 fails, @@TRANCOUNT is 0 by then -> rolledBack: true with step 1 permanently applied.

Consequence: the operator reads "the target is known to be unchanged", performs no cleanup, and the next compare/deploy runs against a schema nobody believes was touched. This is precisely the distinction the flag was added to make, inverted in the dangerous direction.

**Fix**

Report the rollback only when one actually fired, and never claim it covers work outside the transaction. In the tx == null branch use ExecuteScalar so the server tells you: `IF @@TRANCOUNT > 0 BEGIN ROLLBACK TRANSACTION; SELECT 1 END ELSE SELECT 0` and return `(int)result == 1`. Additionally, `RolledBack` must be false whenever a client transaction was not owned AND `executed > 0` batches ran outside it (i.e. the no-transaction opt-out, and the script-managed case where the failure was not in batch 1) — in those modes the executor cannot know what was already committed, so "we do not know" is the only honest answer. Add an acceptance assertion on the JSON `rolledBack` field in both new tests; today no test anywhere reads the flag's value.

**Verifica adversariale**

Reproduced against the real code. SqlExecutor.cs:189-201 returns true whenever the probe command executes; `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;` with @@TRANCOUNT = 0 is a successful no-op, and nothing distinguishes it. The path is reachable exactly as described: ApplyCommand.cs:102 `useOwnTransaction = !selfManaged && !noTx`, so `--no-transaction` gives tx == null, and Msg 1767/1750 (sev 16) leaves the connection Open, so the probe runs and returns true. The new acceptance test at tests/DbDelta.Cli.AcceptanceTests/ApplyCommandTests.cs (No_transaction_opt_out_leaves_the_earlier_batches_applied) does assert dbo.KeptBatch survives and asserts nothing about the JSON, so the run prints rolledBack:true for a half-migrated target. Grep confirms RolledBack has exactly one reader (ApplyCommand.cs:114) and no test anywhere asserts it, so nothing else contradicts the flag. The root cause is sharper than the reviewer states, which strengthens the finding: the tx==null branch conflates two different worlds. For the primary path (a DbDelta script with its own BEGIN TRANSACTION envelope — DeploymentScriptWriter.WritePreamble) @@TRANCOUNT == 0 genuinely means XACT_ABORT already rolled everything back, so true is CORRECT there; for --no-transaction it means 'there was never a transaction, everything auto-committed', so true is a lie. One branch, two meanings, no discriminator. Introduced by this diff (the field is new). Severity corrected critical -> high: the tool destroys nothing here and does not report success — the same JSON object carries success:false, batchesExecuted:1 and transaction:"none"/"script", which an operator can use to detect the half-migration. It is a false guarantee on an already-failed run, not silent damage.

---

## [high] SL-2 · silent-loss

**forcedIndexRecreates keys indexes by bare name, but index names are per-table — an unrelated table's identical index is re-CREATEd (Msg 1913) and a genuinely removed one is never dropped**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:217 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

ScriptGenerator.cs:204 `HashSet<string> forcedIndexRecreates = new(StringComparer.Ordinal);` … :216-217 `blockingIndexDrops.Add((sideB.Schema, sideB.Name, ix)); forcedIndexRecreates.Add(ix.Name);` — note the drop list is (schema, table, index) but the restore set is the name alone. :323 `string indexDelta = EmitIndexDelta(tSrc, tTgt, forcedIndexRecreates);` passes the SAME global set for every Different table. Inside: :846 `if (alreadyDropped is not null && alreadyDropped.Contains(t.Name)) { continue; }` (suppresses the DROP) and :856-859 `bool mustRestore = alreadyDropped is not null && alreadyDropped.Contains(s.Name); … if (existsOnTarget && !shapeChanged && !mustRestore) { continue; }` (forces the CREATE).

**Scenario**

Two Different tables sharing a common index name — `IX_Codice`, `IX_CreatedAt`, `IX_TenantId` are ubiquitous. (a) `dbo.Orders` retypes `CustomerId int→bigint` and has `IX_Codice` on `CustomerId` → dropped up front, `forcedIndexRecreates = {"IX_Codice"}`. `dbo.Customers` is also Different (a column added) and has its own `IX_Codice` on `Codice`, byte-identical on both sides. `EmitIndexDelta(Customers…)` sets `mustRestore = true` and emits `CREATE NONCLUSTERED INDEX [IX_Codice] ON [dbo].[Customers] ([Codice] ASC);` — the index still exists there → Msg 1913 "An index or statistics with name 'IX_Codice' already exists on table 'dbo.Customers'", the batch fails and the whole deploy rolls back. (b) The mirror image: if `dbo.Customers.IX_Codice` was REMOVED in the source, line 846 skips its DROP because the name is in `alreadyDropped` — the index that belonged to `dbo.Orders`. Production keeps an index the source does not have, the tool reports success, and the next compare still shows `dbo.Customers` as Different.

**Fix**

Key the restore set the same way the drop list is keyed: `HashSet<(string Schema, string Table, string Index)>` (ordinal for the index name, case-insensitive for schema/table to match the rest of the engine), and have `EmitIndexDelta` receive the per-table subset — or take `(src.Schema, src.Name)` and test membership with the full triple. A test with two Different tables that share an index name would have caught both halves; `ColumnDependencyOrderingTests` only ever builds one table.

**Verifica adversariale**

Verified line by line. `forcedIndexRecreates` is `HashSet<string>` of bare index names (ScriptGenerator.cs:204), populated from every Different non-rebuild table (:213-218) while the drop list keeps the (schema, table, index) triple (:216). The same global set is then handed to `EmitIndexDelta` for EVERY Different table (:323), and inside it is consulted by name only: the DROP is suppressed at :846 and the CREATE is forced at :856-859. Index names in SQL Server are unique per table, not per database — and IndexReader.cs excludes PK/UQ-backing indexes (is_primary_key = 0, is_unique_constraint = 0), so what remains is exactly the user-named IX_* population where cross-table duplicates (IX_Codice, IX_CreatedAt, IX_TenantId) are routine. Scenario (a) emits `CREATE NONCLUSTERED INDEX [IX_Codice] ON [dbo].[Customers]` for an index that was never dropped there → Msg 1913 → whole deploy rolls back. Scenario (b) is the silent half: the DROP for a genuinely-removed index on the second table is skipped, so production keeps an index the source does not have while the tool reports success. Introduced by these commits — the `alreadyDropped` parameter did not exist before 275660a (confirmed against `git diff`, the old signature was `EmitIndexDelta(Table src, Table tgt)`). No test guards it: ColumnDependencyOrderingTests and the ScriptDom RichComparison fixture only ever have one Different non-rebuild table with indexes, so the collision is never exercised. High is correct — the silent half is a false-success deploy.

---

## [high] SL-3 · silent-loss

**Nothing drops the foreign keys that block the column DDL, so the flagship 'widen an int key to bigint' migration the fix advertises still fails — now on the newly-emitted PK drop (Msg 3725)**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:167 · effort L · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

The whole up-front FK pass has exactly three sources, and none of them is "this FK constrains a column about to be altered": ScriptGenerator.cs:141-155 drops only target-side FKs that vanished or whose shape changed, and :166-170 `bool pointsAtDropped = droppedTables.Contains(referenced); bool pointsAtRebuilt = rebuildTargets.Contains(referenced) && !rebuildTargets.Contains((holder.Schema, holder.Name)); if (pointsAtDropped || pointsAtRebuilt)`. Meanwhile TableScriptEmitter.cs:226 `if (oldC is ForeignKey) { continue; }` and :375-384 `DependsOnColumn` returns `_ => false` for ForeignKey, while :231-238 now emits a brand-new `ALTER TABLE … DROP CONSTRAINT [PK_x]` for an unchanged PK whose column is touched. The XML doc at ScriptGenerator.cs:16-21 nonetheless claims "EVERY foreign-key drop … both DROP TABLE and ALTER COLUMN are blocked by one", and :234-236 repeats "an ALTER COLUMN cannot run while an FK constrains the column".

**Scenario**

`dbo.Orders(Id int NOT NULL, CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (Id))` and `dbo.OrderLines(OrderId int NOT NULL, CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id))`. Source widens both `Orders.Id` and `OrderLines.OrderId` to `bigint` — the exact migration ColumnDependencyOrderingTests' summary calls "about as routine as a migration gets". Both tables are Different; `ForeignKeyShapeEqual` sees identical columns / referenced table / referenced columns / actions, so the FK is NOT dropped; `droppedTables` and `rebuildTargets` are both empty so the holder loop at :157 never even runs. The script emits `ALTER TABLE [dbo].[Orders] DROP CONSTRAINT [PK_Orders];` (new in 275660a, because `Id` is in `touchedColumns`) → Msg 3725 "The constraint 'PK_Orders' is being referenced by table 'OrderLines', foreign key constraint 'FK_OrderLines_Orders'." Remove the PK from the scenario and it fails one statement later instead: `ALTER TABLE [dbo].[OrderLines] ALTER COLUMN [OrderId] [bigint] NOT NULL;` → Msg 5074 "The object 'FK_OrderLines_Orders' is dependent on column 'OrderId'." Either way the transaction rolls back and the migration is impossible.

**Fix**

Extend the up-front pass with a fourth source: for every selected Different table compute `ColumnsDroppedOrAltered` once, then (i) drop any of its own FKs whose `Columns` intersect the touched set, and (ii) drop any FK — held by any table — whose `(ReferencedSchema, ReferencedTable, ReferencedColumns)` resolves to a PK/UNIQUE that `DependsOnColumn` will make `EmitAlter` drop. Both must be re-added in the existing FK ADD pass (source-side shape), which already runs after the CREATE/ALTER pass. Note that (ii) needs the FULL comparison to find Identical holders, i.e. it depends on SL-1 being fixed first.

**Verifica adversariale**

The three FK-drop sources are exactly as quoted and I found no fourth. ScriptGenerator.cs:141-155 drops only target-side FKs a Different table lost or whose `ForeignKeyShapeEqual` changed — and that predicate (:908-916) compares columns, referenced schema/table/columns, actions, IsDisabled, IsNotForReplication, never data types, so widening int→bigint on both sides leaves it "equal". Lines 157-176 fire only when `droppedTables.Count > 0 || rebuildTargets.Count > 0`, both empty in the scenario. TableScriptEmitter skips FKs entirely in section 1 (:226 `if (oldC is ForeignKey) { continue; }`) and `DependsOnColumn` returns `_ => false` for ForeignKey (:375-384). So nothing drops an FK that constrains a column being altered, while the new code at :231-238 does emit a fresh `DROP CONSTRAINT [PK_Orders]` because `Id` is in `touchedColumns` → Msg 3725 when a child table's FK references that PK; strip the PK and the child's own `ALTER COLUMN [OrderId] [bigint]` dies on Msg 5074 instead. The XML docs at :16-21 ("EVERY foreign-key drop") and :234-236 ("an ALTER COLUMN cannot run while an FK constrains the column") assert a guarantee the code does not deliver — the claim is unverified and false. No test covers FK + ALTER COLUMN (grepped all of tests/: only index/PK/CHECK cases in ColumnDependencyOrderingTests). Regression flag `false` is right — pre-diff the same migration died at the ALTER COLUMN on Msg 5074 — but this is the exact case the commit advertises, so it is a genuine incomplete-coverage hole. High stands: routine migration, deterministic failure.

---

## [high] SL-4 · silent-loss

**A DEFAULT-value-only change is misclassified as a column alteration, so changing DEFAULT ((0)) to ((1)) drops and recreates every index on the column and drops the PK**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`:357 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

TableScriptEmitter.cs:354-359 `foreach (Column newCol in newT.Columns) { if (!oldColsByName.TryGetValue(newCol.Name, out Column? oldCol)) continue; if (ColumnShapeEqual(oldCol, newCol)) continue; touched.Add(newCol.Name); }` and `ColumnShapeEqual` at :531 includes `&& BodyNormalizer.ExpressionsEqual(a.DefaultExpression, b.DefaultExpression)`. A live default populates BOTH representations: TableReader.cs:36 `dc.definition AS DefaultExpression` → :110 `defaultExpression: defaultExpr`, and ConstraintReader.cs:275 builds a separate `DefaultConstraint`. So a default change puts the column in `touchedColumns` even though section 3 only emits a no-op `ALTER COLUMN [c] <same type> <same nullability>;` (:273-278) that does not touch the default at all — the default is handled by the constraint drop/add in sections 1 and 5.

**Scenario**

`dbo.Config(ChiaveId int NOT NULL CONSTRAINT DF_Config_ChiaveId DEFAULT ((0)), CONSTRAINT PK_Config PRIMARY KEY CLUSTERED (ChiaveId))`, plus a 40M-row `dbo.Storico` with `IX_Storico_ChiaveId`. Source changes the default to `((1))` — a metadata-only change. `ColumnShapeEqual` returns false → `touchedColumns = {"ChiaveId"}` → `DependsOnColumn(PK_Config, …)` true → the script now emits `ALTER TABLE [dbo].[Config] DROP CONSTRAINT [PK_Config];` before a no-op ALTER COLUMN, and re-adds it after. If any table holds an FK to `Config(ChiaveId)` that is Msg 3725 and the deploy dies (see SL-3). Even with no FK, dropping and re-adding a CLUSTERED PK rebuilds the entire table, and every index covering the column is dropped and rebuilt by the `blockingIndexDrops` pass — inside a batch whose `CommandTimeout` is the 60 s default (SqlExecutor.cs:35; the GUI's `SqlExecutor.ExecuteAsync(…, useOwnTransaction: false)` call at MainWindowViewModel.cs:650-654 passes no timeout), so the deploy times out and rolls back. Before 275660a the same change produced only `DROP CONSTRAINT [DF_Config_ChiaveId]` + a no-op ALTER COLUMN + `ADD CONSTRAINT [DF_…]` and succeeded in milliseconds.

**Fix**

`ColumnsDroppedOrAltered` should test only what section 3 actually emits — data type, nullability, collation, computed expression, identity — not `DefaultExpression`. Split `ColumnShapeEqual` into `ColumnRequiresAlterColumn` (used for `touchedColumns` and for the section-3 guard) and the existing full equality (used by the comparison-side logic). Also worth suppressing the no-op ALTER COLUMN when only the default changed.

**Verifica adversariale**

The mechanism is exactly as described. `ColumnsDroppedOrAltered` (TableScriptEmitter.cs:354-359) adds any column for which `ColumnShapeEqual` is false, and `ColumnShapeEqual` includes `BodyNormalizer.ExpressionsEqual(a.DefaultExpression, b.DefaultExpression)` (:531). Both representations really are populated from a live DB — TableReader.cs joins sys.default_constraints into the column's DefaultExpression (ColumnsQuery, `dc.definition AS DefaultExpression`) and ConstraintReader.ReadDefaultsAsync separately materialises a `DefaultConstraint` — so a DEFAULT-only change puts the column into `touchedColumns` even though section 3 then emits nothing but a no-op `ALTER COLUMN [c] <same type> <same nullability>`. `DependsOnColumn` fires for PK/UQ (:377-378) and the ScriptGenerator blocking-index pass fires for every index covering the column (:213-218), so a metadata-only default flip now drops and re-adds the PK plus every index on that column. This is genuinely new: pre-diff section 1 only dropped constraints that had disappeared or changed shape (`if (!stillPresent || shapeChanged)`), and `blockingIndexDrops` did not exist. The irony is that the `DependsOnColumn` XML doc explicitly reasons that DEFAULTs must be excluded because ALTER COLUMN tolerates a bound default — yet a default change is what put the column in the set in the first place, which is the inconsistency. Consequences: hard Msg 3725 when the PK/UQ is FK-referenced (very common for a PK), and on a large table a clustered-PK drop+re-add inside a batch capped at the 60 s default (SqlExecutor.cs:36; MainWindowViewModel.cs:650-654 passes no timeout) turns a millisecond deploy into a rollback. Previously-succeeding deploys now fail, so high is justified; the 40M-row `dbo.Storico` in the reviewer's scenario is irrelevant (only the changed table's own indexes are touched), which does not affect the verdict.

---

## [high] TQ-02 · test-quality

**forcedIndexRecreates is keyed on bare index name, but index names are per-table — cross-table collision silently keeps a deleted index or breaks the deploy**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:204 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`HashSet<string> forcedIndexRecreates = new(StringComparer.Ordinal);` … `forcedIndexRecreates.Add(ix.Name);` (line 217), then one global set is handed to every table: `EmitIndexDelta(tSrc, tTgt, forcedIndexRecreates)` (line 323). Inside: `if (alreadyDropped is not null && alreadyDropped.Contains(t.Name)) { continue; }` (line 846) and `bool mustRestore = alreadyDropped is not null && alreadyDropped.Contains(s.Name);` (line 856). No schema/table component anywhere. Contrast the sibling collection, which does carry the table: `blockingIndexDrops.Add((sideB.Schema, sideB.Name, ix))`.

**Scenario**

Two Different tables. `dbo.Orders` retypes `TenantId` int→bigint and has `IX_TenantId` on it → dropped up front, name enters forcedIndexRecreates. `dbo.Invoices` also has an index named `IX_TenantId` (index names are unique per object_id, not per database — generic names like this are the norm) which the source has deleted. In Invoices' delta the DROP loop hits `alreadyDropped.Contains("IX_TenantId")` and `continue`s, so `DROP INDEX [IX_TenantId] ON [dbo].[Invoices]` is never emitted: the index the source removed survives in production while the tool reports success. Mirror case — Invoices' `IX_TenantId` is identical on both sides: `mustRestore` is true, so `CREATE NONCLUSTERED INDEX [IX_TenantId] ON [dbo].[Invoices]` is emitted over an existing index → Msg 1913 and the entire deploy rolls back.

**Fix**

Key the set by the same tuple blockingIndexDrops already uses: `HashSet<(string Schema, string Table, string Index)>`, and have EmitIndexDelta test `alreadyDropped.Contains((src.Schema, src.Name, t.Name))`. Add a two-table test where both tables carry an index of the same name and only one is blocking.

**Verifica adversariale**

Reproduced exactly. ScriptGenerator.cs:204 declares ONE `HashSet<string> forcedIndexRecreates` keyed on `ix.Name` (added at :217), and :323 hands that same set to every Different table's `EmitIndexDelta(tSrc, tTgt, forcedIndexRecreates)`. Inside, :846 `if (alreadyDropped.Contains(t.Name)) continue;` suppresses the DROP and :856 `mustRestore = alreadyDropped.Contains(s.Name)` forces the CREATE — neither carries schema or table. The sibling collection two lines up does carry it (`blockingIndexDrops.Add((sideB.Schema, sideB.Name, ix))`, :216), which makes the omission look like an oversight rather than a decision. Index names are per-object_id in SQL Server, so `IX_TenantId` on dbo.Orders and dbo.Invoices is legal and common. Both branches of the reviewer's scenario check out: the DROP-suppression variant silently keeps an index the source deleted while the tool reports success, and the mustRestore variant emits CREATE INDEX over an existing index (Msg 1913) and rolls the whole deploy back. Genuinely introduced here — `forcedIndexRecreates` does not exist in `git show 19131d8:.../ScriptGenerator.cs` (pre-diff EmitIndexDelta took no third argument). No test in the diff exercises two tables with a shared index name; ColumnDependencyOrderingTests is single-table throughout. high is right — the loud Msg 1913 variant is the likelier manifestation.

---

## [high] TQ-03 · test-quality

**The up-front FK-drop pass never collects an FK that constrains a column being retyped — the exact case its own doc comment claims to cover**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:16 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

Class doc: "Foreign-key DROP pass — EVERY foreign-key drop, before any object is touched … both DROP TABLE and ALTER COLUMN are blocked by one", and at the emission site (line 234) "an ALTER COLUMN cannot run while an FK constrains the column". But fkDrops is fed by exactly three sources — a Different table's lost/reshaped target FKs (line 147 `if (!stillThere || !ForeignKeyShapeEqual(t, s!))`), FKs pointing at `droppedTables`, and FKs pointing at `rebuildTargets` (line 167-169). An FK whose shape is unchanged and whose referenced table is neither dropped nor rebuilt is never collected. TableScriptEmitter.DependsOnColumn (line 375) hands the case off: `PrimaryKey pk => …, UniqueConstraint uq => …, CheckConstraint ck => …, _ => false` — FKs fall through to `false`, and EmitAlter's section 1 skips them anyway (`if (oldC is ForeignKey) { continue; }`, line 226). ColumnDependencyOrderingTests covers index, PK, UQ and CHECK — there is no FK case in the file.

**Scenario**

Routine widening: `dbo.Customer.Id` int→bigint (PK_Customer) and `dbo.Orders.CustomerId` int→bigint, with `FK_Orders_Customer` byte-identical on both sides. fkDrops is empty. Script emits `ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [PK_Customer];` → Msg 3725 ("the constraint is being referenced by … foreign key constraint 'FK_Orders_Customer'"), and had that survived, `ALTER TABLE [dbo].[Orders] ALTER COLUMN [CustomerId] [bigint] NOT NULL;` → Msg 5074/4922. Deploy dies. EmitFkAdds would also skip re-adding the FK (shape unchanged), so a bare drop is not enough.

**Fix**

In the blocking-dependency pass, also walk `sideB.Constraints.OfType<ForeignKey>()` and, when any of `fk.Columns` is in `touched` (or the FK's referenced table has a touched referenced column), AddFkDrop it AND add its name to a `forcedFkReadds` set that EmitFkAdds honours the way EmitIndexDelta honours `alreadyDropped`. Add the FK sibling test next to the PK one in ColumnDependencyOrderingTests.

**Verifica adversariale**

Verified against the real code. The class doc (ScriptGenerator.cs:16-21) and the emission-site comment (:236) both claim the up-front pass covers ALTER COLUMN, but fkDrops has exactly the three feeders the reviewer names: :141-155 (a Different table's lost/reshaped TARGET FKs, gated on `!stillThere || !ForeignKeyShapeEqual`), and :157-176 (FKs pointing at droppedTables or rebuildTargets). An unchanged FK on a table whose referenced table is neither dropped nor rebuilt is collected by none of them. Confirmed on the other side too: TableScriptEmitter.cs:226 `if (oldC is ForeignKey) { continue; }` skips FKs in EmitAlter section 1, and DependsOnColumn (:375-384) has no ForeignKey arm so FKs fall to `_ => false`. ColumnDependencyOrderingTests covers index / PK / UQ / CHECK and contains no FK case — I read the whole file. The scenario is a routine PK+FK widening and dies twice (Msg 3725 on `DROP CONSTRAINT [PK_Customer]`, then Msg 5074 on `ALTER COLUMN [CustomerId]`), and EmitFkAdds (:897-904) would skip the re-add because the shape is unchanged, so a bare drop would not be a complete fix either. Regression flag `false` is correct — pre-diff EmitFkDelta had the identical gate. high stands: loud failure on a common migration, no data loss.

---

## [high] TQ-07 · test-quality

**FK drops are deduped by bare constraint name on the false premise that constraint names are database-scoped**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:132 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`void AddFkDrop(string schema, string table, ForeignKey fk) { // Constraint names are database-scoped, so the name alone dedupes.  if (fkDropNames.Add(fk.Name)) { fkDrops.Add((schema, table, fk)); } }`. Constraint names live in sys.objects and are unique per SCHEMA, not per database — `sales.FK_Invoice_Currency` and `hr.FK_Invoice_Currency` can both exist. The same wrong premise is repeated in TableRebuildPkSwapTests' class doc and in EmitRebuild's comment, and `rebuildOrchestratedFkNames` (line 91) has the same shape.

**Scenario**

`sales.Invoice` and `hr.Invoice` each hold an FK named `FK_Invoice_Currency` referencing `dbo.Currency`, which the source has deleted. The first holder enumerated wins fkDropNames.Add; the second's drop is never emitted. The script runs `ALTER TABLE [sales].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];` then `DROP TABLE [dbo].[Currency];` → Msg 3726, deploy rolls back. No test uses two schemas.

**Fix**

Dedupe on `(schema, table, fk.Name)` in both fkDropNames and rebuildOrchestratedFkNames (the latter is also consulted by EmitFkAdds/EmitAdd, so pass the tuple through). Add a two-schema FK test.

**Verifica adversariale**

The premise in the comment is wrong and the dedupe key is too coarse — but the reviewer picked the weaker of the two failure modes. ScriptGenerator.cs:132-139 dedupes on `fk.Name` alone with the justification 'Constraint names are database-scoped'; constraint names live in sys.objects and are unique per SCHEMA, so sales.FK_Invoice_Currency and hr.FK_Invoice_Currency coexist legally. Their Msg 3726 scenario is NOT a regression: pre-diff the FK drops lived in EmitFkDelta at the END of the script, so `DROP TABLE [dbo].[Currency]` failed with Msg 3726 there too — no previously-working case broke. The genuinely new failure is silent: two Different tables in different schemas that both lost an identically-named FK now feed AddFkDrop twice, the second `fkDropNames.Add` returns false, only one `ALTER TABLE … DROP CONSTRAINT` is emitted, and nothing else re-emits it (EmitFkAdds at :889 only adds). The FK the source removed survives in production and the tool reports success. Raising severity to high on that basis. `rebuildOrchestratedFkNames` shares the shape but is pre-existing (present at 19131d8), so it is not part of this regression. Confirmed no test uses two schemas for FK dedupe — SchemaEmissionTests uses sales/hr only for CREATE/DROP SCHEMA.

---

## [medium] EO-5 · emission-ordering

**FK dedup and rebuild-skip sets key on constraint name alone; the comment claiming DB-scoped names is wrong (they are schema-scoped)**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:134 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`void AddFkDrop(...) { // Constraint names are database-scoped, so the name alone dedupes.\n if (fkDropNames.Add(fk.Name)) { fkDrops.Add((schema, table, fk)); } }` and `HashSet<string> rebuildOrchestratedFkNames = new(StringComparer.Ordinal)` used at lines 389, 408 and 899 to SKIP an FK add. Foreign keys are rows in sys.objects with the parent table's schema_id, so uniqueness is (schema_id, name): dbo.FK_Fattura_Valuta and archivio.FK_Fattura_Valuta coexist legally. TableScriptEmitter.cs:447 repeats the same wrong belief ("SQL Server constraint names are DB-scoped").

**Scenario**

Multi-schema DB with a mirrored table set (dbo.* live, archivio.* history), each carrying FK_Fattura_Valuta. Source removes both Valuta tables. droppedTables = {(dbo,Valuta),(archivio,Valuta)}; the holder scan adds `ALTER TABLE [dbo].[Fattura] DROP CONSTRAINT [FK_Fattura_Valuta];` then swallows the archivio one because fkDropNames already contains the name → `DROP TABLE [archivio].[Valuta];` → Msg 3726 → whole deploy rolls back. Silent variant on the add side: dbo.Fattura is a rebuild target so FK_Fattura_Valuta enters rebuildOrchestratedFkNames; archivio.Fattura is a plain Different table that gains its own new FK_Fattura_Valuta, and EmitFkAdds skips it (`skipNames.Contains(s.Name)`) while the inbound re-add pass only emits the dbo one — the script reports success and archivio.Fattura is left with no foreign key, so orphan rows become writable.

**Fix**

Make both sets `HashSet<(string Schema, string Table, string Name)>` (or at minimum (Schema, Name)) and thread the holder's schema/table through AddFkDrop, EmitFkAdds and the two rebuild branches. Fix the two comments while you are there — the belief also drives the whole EmitRebuild PK-swap dance.

**Verifica adversariale**

The factual core is right and the code matches. FK constraints are rows in sys.objects carrying the parent table's schema_id, so uniqueness is (schema, name): dbo.FK_Fattura_Valuta and archivio.FK_Fattura_Valuta coexist legally, and the comment at ScriptGenerator.cs:134 ('database-scoped') is simply wrong. AddFkDrop's `fkDropNames.Add(fk.Name)` therefore swallows the second holder's drop, and with two mirrored schemas both losing their Valuta table one DROP TABLE dies on Msg 3726 — a loud full rollback. AddFkDrop/fkDropNames are new in ec6eb83 (pre-diff the drops were per-pair inside EmitFkDelta, so no cross-schema dedupe existed), so 'regression: true' is correct for the drop side. Two corrections to the evidence: the silent add-side variant is described backwards — FK_Fattura_Valuta enters rebuildOrchestratedFkNames only when the REFERENCED table (Valuta) is the rebuild target and its holder is not, not when Fattura is the rebuild target; and rebuildOrchestratedFkNames with its name-only keying already exists at 19131d8, so that half is pre-existing, not introduced. Severity medium stands.

---

## [medium] EO-7 · emission-ordering

**Two tables that both need an identity rebuild and reference each other: the FK between them is never dropped and the rebuild order is alphabetical**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:168 · effort S · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

`bool pointsAtRebuilt = rebuildTargets.Contains(referenced) && !rebuildTargets.Contains((holder.Schema, holder.Name));` — when holder and referenced are BOTH rebuild targets the drop is skipped, on the assumption that the holder's own DROP TABLE removes it first. Nothing enforces that order: the rebuild is emitted from the CREATE pass in createOrder, and DependencyResolver.cs:62 discards exactly the edges that would order it (`if (e.Kind == EdgeKind.ForeignKey) { continue; }`), while DependencyReader never produces FK edges at all — so two Tables sort by `string.CompareOrdinal(a.ObjectName, b.ObjectName)`. The same blind spot makes ForeignKeyDropOrderingTests.A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk pass by luck: it asserts both DROP TABLEs exist but never their order, and "Invoice" > "Currency" happens to reverse correctly.

**Scenario**

dbo.Cliente and dbo.Fattura both flip IDENTITY on their Id (a legacy-schema cleanup), Fattura holds FK_Fattura_Cliente → Cliente. Both are rebuild targets, so the FK is dropped nowhere. createOrder = [Cliente, Fattura] (C < F), so Cliente's rebuild block runs first and `DROP TABLE [dbo].[Cliente];` fails with Msg 3726 while FK_Fattura_Cliente still exists. Rename the pair the other way round and it works — the correctness of the deploy depends on the alphabet. Identical exposure for two target-only tables: `dbo.Indirizzo → dbo.Cliente` both removed from source, reverse-alphabetical drop order emits `DROP TABLE [dbo].[Cliente];` first → Msg 3726, and case (b) deliberately skips the FK because its holder is also being dropped.

**Fix**

Drop the `!rebuildTargets.Contains(holder)` exclusion (an extra explicit DROP CONSTRAINT before a DROP TABLE is harmless), and drop the droppedTables self-skip too — or, better, stop relying on table order entirely: emit a DROP CONSTRAINT for every FK whose referenced table is in droppedTables ∪ rebuildTargets regardless of who holds it. Add a test with names that force the wrong alphabetical order (Cliente/Fattura), since every current test is accidentally ordered correctly.

**Verifica adversariale**

Verified: `pointsAtRebuilt` (ScriptGenerator.cs:168) skips the drop when holder and referenced are both rebuild targets, nothing else drops that FK (the rebuild block only drops non-FK named constraints, TableScriptEmitter.cs:455-461), and the rebuild blocks are emitted from the CREATE pass in createOrder, which for two Tables with no edges is schema-then-ordinal-name order — DependencyResolver discards ForeignKey edges (:62) and DependencyReader never produces any. Cliente before Fattura ⇒ DROP TABLE [dbo].[Cliente] with FK_Fattura_Cliente still live ⇒ Msg 3726; rename them and it works. Pre-existing is right: 19131d8 had the same `!rebuildTargets.Contains((tgtT.Schema, tgtT.Name))` guard and the same drop ordering. Two notes. The closing sub-example is backwards: with dbo.Indirizzo → dbo.Cliente, reverse order drops Indirizzo (the child) first and succeeds; the failing shape is the child sorting BEFORE the parent (e.g. Fattura → Valuta ⇒ DROP TABLE [dbo].[Valuta] first ⇒ Msg 3726). Corrected, that sub-case is a coin flip on every pair of FK-linked target-only tables and is materially likelier than the double-rebuild headline. The remark about A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk is fair as coverage criticism though loosely worded: the test asserts no order, so it passes either way — what is lucky is that Currency/Invoice happens to be the naming that would actually run. Severity medium kept for the finding as stated.

---

## [medium] EO-8 · emission-ordering

**CREATE SCHEMA / DROP SCHEMA are selection-gated, so a partial deploy into a new schema fails on its first statement**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:230 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`EmitSchemaCreates(writer, pairs)` / `EmitSchemaDrops(writer, pairs)` both filter `pairs`, which is `selection ?? result.Differences` — in the GUI it is literally the ticked rows (MainWindowViewModel.cs:673). Nothing promotes the schema of a selected object into the emission set, and Mapper.cs:16 projects a Schema pair with `ObjectName: d.Identity.ObjectName`, which Schema.cs:8 leaves as `string.Empty`, so the grid row for a schema shows an empty object name and a KindOrder of 99 (DifferenceRowViewModel has no "Schema" case).

**Scenario**

Source adds schema `vendite` plus table vendite.Ordine. The grid shows an OnlyInA row with an empty name (kind "Schemi") and the vendite.Ordine row. The user ticks only the table — the normal one-object deploy — and the script goes straight to `CREATE TABLE [vendite].[Ordine]` with no CREATE SCHEMA → Msg 2760, the exact failure the SchemaEmissionTests doc comment says this feature exists to prevent. Mirror case: ticking only the `legacy` schema row while leaving legacy.OldOrder unticked emits DROP SCHEMA on a non-empty schema → Msg 3729.

**Fix**

In Generate, after computing `pairs`, add back the OnlyInA Schema pair for every SchemaName referenced by a selected object (and conversely suppress a DROP SCHEMA whose schema still holds unselected target-only objects) — the full result is available for the lookup once EO-1 is fixed. Give the Schema row a real display name so a user can recognise it in the grid.

**Verifica adversariale**

Confirmed as a coverage gap. EmitSchemaCreates/EmitSchemaDrops filter `pairs`, which is `selection ?? result.Differences`, i.e. the ticked rows on the GUI path; nothing promotes the schema of a selected object into the emission set, and every SchemaEmissionTests case exercises the full-result (CLI) shape. Tick only vendite.Ordine and the script opens with CREATE TABLE [vendite].[Ordine] ⇒ Msg 2760. Mapper.cs:16 + Schema.cs:8 confirm the row renders with an empty object name and KindOrder 99, which makes the schema row easy to overlook (minor correction: DifferenceRowViewModel.KindDisplayName has no Schema case, so the grid shows 'Schema', not 'Schemi' — 'Schemi' only exists in KindCatalog for the HTML/JSON reports). The 'regression-from-this-diff: true' flag needs splitting: for the CREATE half it is false — before 3dd4d09 no CREATE SCHEMA was ever emitted, so a new schema failed with Msg 2760 on every deploy, partial or not, and the diff strictly improves the full-deploy case. Only the DROP half is a genuinely new way to produce a failing script (ticking a schema row without its contents ⇒ Msg 3729), and that requires a user action that is close to self-inflicted. Severity medium stands on the strength of the CREATE half.

---

## [medium] EO-9 · emission-ordering

**DROP USER is emitted in the prologue but DROP SCHEMA in the epilogue, so dropping a user that owns a dropped schema still fails**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:455 · effort S · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

Prologue: `EmitSchemaCreates(...); EmitUsers(writer, pairs); EmitRoles(writer, pairs);` where EmitUsers emits `_userEmitter.EmitDrop(u)` for OnlyInB users (line 706). Epilogue, 220 lines later: `EmitSchemaDrops(writer, pairs);` with the comment "after every object pass, so the objects a removed schema held are already gone". Users are not option-gated (only permissions are), so DROP USER always ships.

**Scenario**

Target has `CREATE SCHEMA [legacy] AUTHORIZATION [app_legacy]`; the source has neither the schema nor the user. The script emits `DROP USER [app_legacy];` in the prologue → Msg 15138 "The database principal owns a schema in the database, and cannot be dropped" → NOEXEC ON → the whole deploy (including every unrelated object change the operator queued) rolls back. The DROP SCHEMA that would have unblocked it sits 200 statements further down and never executes.

**Fix**

Split the user/role pass: CREATE/ALTER stays in the prologue (default-schema and permission targets need it), DROP moves after EmitSchemaDrops. Correct order is object drops → schema drops → principal drops. Worth doing now: the inverse-script generator will have to reverse this sequence, and a wrong forward order becomes a wrong rollback order.

**Verifica adversariale**

Ordering verified: EmitSchemaCreates/EmitUsers/EmitRoles at ScriptGenerator.cs:230-232, EmitSchemaDrops at :455, EmitUsers emitting _userEmitter.EmitDrop(u) for OnlyInB at :706, and users are indeed ungated — IgnoreUsersPermissionsAndRoleMemberships exists in ComparisonOptions but is absent from Default and is never read by EmitUsers. A target-only user owning a target-only schema therefore always hits Msg 15138 in the prologue and takes the whole deploy down. Correctly flagged pre-existing: DROP USER sat in the prologue at 19131d8 and DROP SCHEMA did not exist at all, so the scenario failed before too. Worth adding that the real root gap is broader and independent of the new schema pass — Schema is modelled as its name alone (no principal_id), so DbDelta cannot see ownership at all and DROP USER fails the same way when the owned schema exists on both sides, a case no reordering of EmitSchemaDrops would fix. Severity medium stands (loud, edge-case, full rollback).

---

## [medium] EXEC-2 · executor-transactions

**Transaction-mode heuristic false-positives on any BEGIN TRAN at line start — comments, module bodies, dynamic SQL — and then skips the transaction it was added to add**

- file: `D:\Develop\AI\_ClaudeCode\SQL Compare\src\DbDelta.Persistence\Sql\SqlExecutor.cs`:265 · effort M · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

[GeneratedRegex(
    @"^\s*BEGIN\s+TRAN(SACTION)?\b",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
private static partial Regex BeginTransactionPattern();

public static bool ScriptManagesItsOwnTransaction(string script) => BeginTransactionPattern().IsMatch(script);

ApplyCommand.cs:101-102:
    bool selfManaged = SqlExecutor.ScriptManagesItsOwnTransaction(script);
    bool useOwnTransaction = !selfManaged && !noTx;

`Multiline` makes `^` match after every newline and `\s*` swallows indentation, so the match is "the token BEGIN TRAN appears at the start of any line, however indented, whatever lexical context it is in".

**Scenario**

Every one of these is a false positive that turns the new protection OFF for exactly the population it was written for (hand-written scripts, other tools' output, a generated script whose envelope was edited out):

1. A module body — the single most common case. A migration script that deploys a procedure or trigger:
     CREATE PROCEDURE dbo.usp_Transfer AS
     BEGIN
         SET NOCOUNT ON;
         BEGIN TRANSACTION;   -- indented: ^\s* matches
         ...
         COMMIT TRANSACTION;
     END
   `^\s*BEGIN\s+TRAN` matches the indented line inside the CREATE PROCEDURE text. The script itself opens no transaction. selfManaged = true -> useOwnTransaction = false -> batch 1 commits, batch 3 fails, database half-migrated, and per EXEC-1 the report says `"transaction": "script", "rolledBack": true`. Half-migration silently reported as clean, with no `--no-transaction` flag on the command line to hint to the operator that protection was off.

2. A block comment:
     /*
     BEGIN TRANSACTION was removed from this script on purpose
     */
   matches. The unit test comment at SqlExecutorTests.cs:149-151 claims "A mention inside a comment or a name must not count as an opener", but the only comment case tested is a trailing `-- BEGIN TRANSACTION`, which is safe only because `--` is not whitespace. The sibling scenario reachable by the same path (block comment, or a `--` comment on its own line preceded by nothing... any line whose first non-space token is BEGIN) is not covered.

3. A multi-line string literal:
     EXEC sp_executesql N'
     BEGIN TRANSACTION
     ...'
   matches; no transaction is opened at the outer level.

The opposite direction is also wrong but much less dangerous, which is worth stating precisely: `BEGIN DISTRIBUTED TRANSACTION`, and `BEGIN TRANSACTION` not first on its line (`SET NOCOUNT ON; BEGIN TRANSACTION`, `IF @@TRANCOUNT = 0 BEGIN TRAN`) are NOT matched, so a genuinely self-managing script gets wrapped and reaches @@TRANCOUNT = 2. For a hand-written script with no failure gate that still ends correctly (the outer rollback undoes the nested work; the outer commit commits it). The @@TRANCOUNT = 2 catastrophe the XML doc describes needs a script that both self-gates AND has a non-line-start BEGIN — which DbDelta's own writer can never produce, since DeploymentScriptWriter.Batch() puts "BEGIN TRANSACTION" alone on a line. So the false-positive direction is the one that actually bites.

**Fix**

Text matching on raw T-SQL cannot decide this without a lexer, and the consequence of guessing wrong is a half-migration, so stop guessing. Two lazy options, either is sound: (a) key off provenance instead of syntax — DeployScriptBuilder/DeploymentScriptWriter already emit a recognisable header; emit a machine-readable marker line (`-- dbdelta:transaction=script`) and match that, treating everything else as "needs a client transaction"; or (b) replace the sniff with an explicit `--transaction script|client|none` option defaulting to `client`, and let the generated-script path pass `script`. If the regex is kept as a fallback, at minimum strip `/* */` and `--` comments and string literals first, and require the match to be in a batch that contains nothing but SET/BEGIN statements. Also add unit cases for the indented-in-procedure-body and block-comment inputs, which currently return the wrong answer.

**Verifica adversariale**

The regex behaviour is exactly as quoted (SqlExecutor.cs:265-268): RegexOptions.Multiline plus `\s*` means any line whose first non-space token is BEGIN TRAN/TRANSACTION matches — indented module bodies, block comments, multi-line string literals. `\s` also spans newlines, so it matches even more loosely than claimed. The test-coverage criticism is accurate: SqlExecutorTests.cs:149-151 claims 'a mention inside a comment or a name must not count' but the only comment case tested is a trailing `--` (safe purely because `-` is not whitespace); no block comment, no indented case, no module body. BUT the regression framing is wrong, and that matters for what has to be fixed before the undo work. At 19131d8 ApplyCommand passed `useOwnTransaction: false` unconditionally, so the entire false-positive population (hand-written scripts, other tools' output) got NO transaction before this diff and gets no transaction after it — identical behaviour, no new half-migration. What this diff genuinely adds on a false positive is a wrong label: JSON `transaction: "script"` for a script that manages nothing, compounding EXEC-1. Also note the DbDelta writer always puts BEGIN TRANSACTION alone on its own line (DeploymentScriptWriter.Batch), so the product's own scripts are always classified correctly — the sniff never mis-handles generated output. Severity high -> medium: an incomplete fix that fails to close the hole for a common sibling case, plus a misleading report, not a new failure.

---

## [medium] SCHEMA-01 · schema-kind-and-persistence

**DROP SCHEMA is emitted for target-only schemas whose contents the tool never drops (cdc, CLR types, XML schema collections)**

- file: `src/DbDelta.Providers.LiveDb/Readers/SchemaReader.cs`:14 · effort M · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

SchemaReader filter: `WHERE s.principal_id != 4 -- exclude sys / AND s.name NOT IN ('sys','INFORMATION_SCHEMA','guest') / AND s.name NOT LIKE 'db[_]%'`. ScriptGenerator.cs:453-455: `// Schema drops — after every object pass, so the objects a removed schema held are already gone.` + `EmitSchemaDrops(writer, pairs);`. TableReader.cs:20 filters only `WHERE t.is_ms_shipped = 0`; ModuleReader likewise (`v.is_ms_shipped = 0`, `p.is_ms_shipped = 0`); UserDefinedTypeReader.cs:24 filters `AND t.is_assembly_type = 0` so CLR types are read by nobody. SchemaScriptEmitter.EmitDrop: `return $"DROP SCHEMA [{schema.Name}];";` — no IF EXISTS, no emptiness check.

**Scenario**

Target production DB has Change Data Capture enabled (very common; source dev DB does not). `sys.schemas` on the target returns `cdc`, which passes every clause of the reader filter (name is not sys/INFORMATION_SCHEMA/guest, does not start with `db_`, principal_id is the `cdc` user's, not 4). CompareSchemas yields OnlyInB for `cdc`, so the epilogue emits `DROP SCHEMA [cdc];`. Its contents (cdc.change_tables, cdc.captured_columns, cdc.lsn_time_mapping, cdc.ddl_history, cdc.<instance>_CT) are `is_ms_shipped = 1`, so TableReader never returns them and the DROP pass emits nothing for them. The batch fails with Msg 3729 ("Cannot drop schema 'cdc' because it is being referenced by object ..."). With the default envelope the error gate trips, XACT_ABORT rolls the whole deploy back and NOTHING ships — the tool cannot deploy to any CDC-enabled target at all. With ComparisonOptions.NoTransactions or `dbdelta apply --no-transaction`, every batch before the schema drop has already committed and the run ends half-migrated. Same shape for a target-only schema holding a CLR UDT (excluded by `is_assembly_type = 0`), an XML SCHEMA COLLECTION, a CLR aggregate, or an assembly — none of which any reader models. The mirror case is worse: a target-only schema that is genuinely empty of unmodelled objects is DROPped silently and succeeds, with no IF EXISTS guard and no inverse-script coverage.

**Fix**

Do not emit DROP SCHEMA unconditionally. Two independent guards: (1) tighten the reader — exclude schemas whose objects are all `is_ms_shipped = 1` (or blacklist the feature schemas: cdc, plus any schema owned by a principal the tool also refuses to drop); (2) make the drop conditional in T-SQL so an unexpectedly non-empty schema is a no-op rather than a deploy abort: `IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID(N'x')) AND NOT EXISTS (SELECT 1 FROM sys.types WHERE schema_id = SCHEMA_ID(N'x')) AND NOT EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE schema_id = SCHEMA_ID(N'x')) DROP SCHEMA [x];` — plus a PRINT that says it was skipped.

**Verifica adversariale**

Code claims all verify. SchemaReader.cs:12-18 filters only on principal_id!=4, three literal names, and NOT LIKE 'db[_]%' — a CDC-enabled target's `cdc` schema (owned by the `cdc` user, principal_id>4) passes every clause, LiveDbSource.cs:33 feeds it into Database.Schemas, ComparisonEngine.CompareSchemas yields OnlyInB, and ScriptGenerator.cs:455 EmitSchemaDrops emits a bare `DROP SCHEMA [cdc];`. UserDefinedTypeReader.cs:24 does exclude is_assembly_type=1 and there is no reader for XML schema collections / assemblies / aggregates (ls of Readers/ confirms 13 readers, none of them), so an unmodelled-content schema is genuinely reachable.

Two corrections. (1) The 'destroys a production object while the tool reports success' framing is REFUTED: SQL Server refuses DROP SCHEMA on a schema that still holds anything (Msg 3729), so the damage ceiling is a loud, XACT_ABORT-rolled-back deploy, not silent loss. The mirror case the reviewer calls 'worse' — an empty target-only schema dropped silently — loses only an empty container, and is what a schema-compare tool is supposed to do. That caps this at medium, not high. (2) The 'no IF EXISTS' sub-claim is not a defect: bare DROPs are the codebase norm for non-module kinds (RoleScriptEmitter:35, SequenceScriptEmitter:58, SynonymScriptEmitter:20, TableScriptEmitter:183, UserScriptEmitter:60 all emit bare DROP); only the CREATE-OR-ALTER module kinds use IF EXISTS.

One addition that makes the finding much more reachable than the CDC story: the Avalonia app deploys only ticked rows (MainWindowViewModel.cs:673 `Rows.Where(r => r.IsSelected).Select(r => r.Pair)`), and rows default to unchecked. Tick the target-only `legacy` schema row and leave its tables unticked and you get `DROP SCHEMA [legacy];` with the tables still present → Msg 3729 → whole deploy rolls back. The generator's own comment at line 453 ('after every object pass, so the objects a removed schema held are already gone') is only true for a full selection, which the app never has by default. Introduced by 3dd4d09: true — no DROP SCHEMA existed before it.

---

## [medium] PERSIST-01 · schema-kind-and-persistence

**The forWrite rethrow reaches four async-void / fire-and-forget call sites; the commit's "surfaces through LastError" claim is false**

- file: `src/DbDelta.App.Avalonia/Views/ConnectionManagerDialog.axaml.cs`:86 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

JsonConnectionStore.ReadDocumentAsync: `catch (Exception ex) when (!forWrite && ex is IOException or UnauthorizedAccessException)` — with forWrite:true the filter is false and the exception propagates out of UpsertAsync/DeleteAsync/TouchUsageAsync. Call sites: ConnectionManagerDialog.axaml.cs:15 `private async void OnNewClick(...)` → `await cs.UpsertExplicitAsync(...)`; :43 `private async void OnEditClick(...)` → UpsertExplicitAsync / `await cs.DeleteAsync(entry.Id, ...)`; :79 `private async void OnDeleteClick(...)` → `await cs.DeleteAsync(id, ...)` — no try/catch in any of the three, and ConnectionStoreViewModel.cs:119/144 (`await store.UpsertAsync(...)`, `await store.DeleteAsync(...)`) do not guard either. MainWindowViewModel.cs:228 (SaveProjectAsync) and :308 (LoadProjectFromPathAsync) `await _recentProjects.AddOrTouchAsync(path, CancellationToken.None)` unguarded inside `[RelayCommand]` methods; CommunityToolkit.Mvvm 8.4.2 (Directory.Packages.props:24) routes AsyncRelayCommand.Execute through the internal `static async void AwaitAndThrowIfFailed(Task)` unless FlowExceptionsToTaskScheduler is set, which none of these commands set. `grep -rn "UnhandledException|UnobservedTaskException" src` → no matches: there is no global handler, exactly as the commit message states. Only AppStateViewModel.CompareAsync (line 268 `catch (Exception ex) { LastError = ex.Message; }`) actually implements the claimed LastError channel, and it covers only the AutosaveAsync path.

**Scenario**

connections.json is momentarily held open by OneDrive/Dropbox/AV (the very trigger 7fb6a8d names) or the roaming profile is read-only. User opens the Connection Manager and clicks "Elimina" → OnDeleteClick (async void) → ConnectionStoreViewModel.DeleteAsync → JsonConnectionStore.DeleteAsync → ReadDocumentAsync(forWrite: true) → IOException rethrown → escapes the async void handler onto the Avalonia dispatcher with no global handler → the process dies with no dialog and no log. Identical for "Nuova connessione"/"Modifica", and for Salva progetto / Carica progetto / MRU-open via AsyncRelayCommand's async-void rethrow (there the project XML has already been written, so the crash happens after a successful save). The one variant that does NOT crash is worse in a different way: ProjectSetupDialog.axaml.cs:145 `private void OnSaveClick(...) => _ = SaveAsAsync();` discards the Task, so the same exception is swallowed as an unobserved task exception — the user sees no error and the MRU silently never updates. Net effect: 7fb6a8d moved the crash from startup to the first write, and the commit message asserts the opposite.

**Fix**

Either (a) wrap each mutating call site so the failure lands in AppState.LastError / StatusText (the new error banner from b7bbdcb is exactly the right sink), or (b) better and smaller: have the mutating store methods return a bool/Result instead of throwing, so no call site can forget. Also register App.OnFrameworkInitializationCompleted-level handlers (Dispatcher unhandled + TaskScheduler.UnobservedTaskException) so nothing else in the app can die this way, and convert the async void handlers to `=> _ = HandleAsync()` with an internal try/catch.

**Verifica adversariale**

Mechanism fully verified. JsonConnectionStore.ReadDocumentAsync's new filter is `catch (Exception ex) when (!forWrite && ex is IOException or UnauthorizedAccessException)`, so with forWrite:true it does not catch. ConnectionManagerDialog.axaml.cs really has three `private async void` handlers (OnNewClick:15, OnEditClick:43, OnDeleteClick:79) with no try/catch, and ConnectionStoreViewModel.cs:119/144 don't guard either. MainWindowViewModel.cs:228 (SaveProjectAsync) and :308 (LoadProjectFromPathAsync) await AddOrTouchAsync unguarded inside [RelayCommand] methods; Directory.Packages.props:24 is CommunityToolkit.Mvvm 8.4.2 and none of these declare FlowExceptionsToTaskScheduler, so AsyncRelayCommand.Execute routes through the internal async-void AwaitAndThrowIfFailed. grep over src for UnhandledException|UnobservedTaskException|FlowExceptionsToTaskScheduler returns zero hits — there is no global handler. AppStateViewModel.cs:268 `catch (Exception ex) { LastError = ex.Message; }` is indeed the only LastError channel and covers only the AutosaveAsync path (:262-265, inside the try).

The reviewer's regression=false is right and worth restating precisely: before 7fb6a8d ReadDocumentAsync caught only JsonException, so IOException/UnauthorizedAccessException escaped Upsert/Delete/TouchUsage/AddOrTouch exactly as they do now. `forWrite: true` reproduces the prior behaviour verbatim; the commit changed only the load path. So nothing here is new except the commit message's unsupported 'That throw surfaces through the existing LastError channel' sentence. Severity corrected to medium: a pre-existing robustness gap that needs a locked or read-only store file to fire, not a crash on an ordinary path.

---

## [medium] SL-5 · silent-loss

**The rebuilt-table trigger restore only handles Identical triggers; a state-only Different trigger emits a bare ENABLE/DISABLE against a trigger the rebuild just destroyed**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:351 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

ScriptGenerator.cs:350-351 filters `x.Identity.Kind == "Trigger" && x.Status == DifferenceStatus.Identical` — Different triggers are excluded on the stated grounds that "Triggers that DIFFER are already re-emitted by the CREATE pass above" (:340-342). But TriggerScriptEmitter.EmitDifferent does not always re-emit the body: `if (bodiesMatch && sideA.IsDisabled != sideB.IsDisabled) { string verb = sideA.IsDisabled ? "DISABLE" : "ENABLE"; return $"{verb} TRIGGER [{sideA.Schema}].[{sideA.Name}] ON [{sideA.ParentSchema}].[{sideA.ParentTable}];"; }` — a state-only diff yields no CREATE at all.

**Scenario**

`dbo.Invoice` gets an IDENTITY change (→ rebuild) and carries `trg_Invoice_Audit` whose body is unchanged but which was disabled in prod during a data fix and is enabled in source. The pair is Different (state only), so it is selectable and the user ticks it along with the table. Topo order puts Table (rank 3) before Trigger (rank 7), so the script runs `DROP TABLE [dbo].[Invoice]` / `sp_rename` — destroying the trigger — and then the only trigger statement in the whole script is `ENABLE TRIGGER [dbo].[trg_Invoice_Audit] ON [dbo].[Invoice];`, which fails because the object no longer exists, rolling back the deploy. Under the CLI's `--no-transaction`, or if the state-only diff is the sole trigger difference and the statement is tolerated, the trigger is simply gone with a success verdict.

**Fix**

Widen the restore filter to "every Trigger pair whose parent is a rebuild target and whose emitted DDL is not a full CREATE" — simplest is: for rebuild targets, always emit `EmitCreateOrAlter(sideA)` plus the `DISABLE` fix-up from the source side, and have the CREATE pass skip Trigger pairs whose parent is a rebuild target so the trigger is emitted exactly once, after `sp_rename`.

**Verifica adversariale**

The code reads as quoted: the restore loop filters `Status == DifferenceStatus.Identical` (ScriptGenerator.cs:350-351) on the stated grounds that Different triggers are re-emitted by the CREATE pass, but `TriggerScriptEmitter.EmitDifferent` (TriggerScriptEmitter.cs:48-52) returns a bare `ENABLE`/`DISABLE TRIGGER` and no CREATE when the bodies match and only IsDisabled flipped. DependencyResolver's KindRank puts Table at 3 and Trigger at 7, so the rebuild's DROP TABLE/sp_rename runs first and the ENABLE then targets an object the rebuild destroyed. So the defect is real and it is a true sibling of the case the fix covers. But the regression flag is WRONG and should be false: the CREATE-pass topo order and `EmitDifferent`'s state-only branch both predate this diff (they appear only as context lines in `git diff 19131d8..HEAD`), so the failing ENABLE-after-rebuild script was emitted before these commits too — the new code merely failed to extend its restore to that case. Severity medium is right: inside the transaction it is a loud failure and rollback; the silent-loss variant needs `--no-transaction`.

---

## [medium] SL-6 · silent-loss

**The FK-drop dedupe keys on the bare constraint name and the comment asserts DB-scoped names — a cross-schema collision silently swallows a required drop**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:134 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

ScriptGenerator.cs:130-139 `HashSet<string> fkDropNames = new(StringComparer.Ordinal); void AddFkDrop(string schema, string table, ForeignKey fk) { // Constraint names are database-scoped, so the name alone dedupes.  if (fkDropNames.Add(fk.Name)) { fkDrops.Add((schema, table, fk)); } }`. The same false premise is written into TableScriptEmitter.cs:447-449 ("SQL Server constraint names are DB-scoped"). Constraints live in sys.objects and are unique per (schema_id, name), not per database.

**Scenario**

A multi-tenant schema layout where `dbo` and `sales` mirror each other: `dbo.Righe` has `FK_Righe_Testa → dbo.Testa` and `sales.Righe` has `FK_Righe_Testa → sales.Testa` (legal — different schemas). The source removes both `dbo.Testa` and `sales.Testa`. `droppedTables` contains both; the holder loop calls `AddFkDrop("dbo","Righe",…)` first, which claims the name, so the second call for `sales.Righe` is silently discarded. The script emits only `ALTER TABLE [dbo].[Righe] DROP CONSTRAINT [FK_Righe_Testa];`, then `DROP TABLE [sales].[Testa];` → Msg 3726, deploy rolls back. The same swallow can hit the two drops in a mixed dropped/rebuilt pair, where the surviving FK also blocks `DROP TABLE` inside the rebuild block.

**Fix**

Dedupe on `(schema, table, fk.Name)` — case-insensitively on schema/table to match the rest of the engine. `rebuildOrchestratedFkNames` (used as `skipNames` in `EmitFkAdds`, and as the filter at :389 and :408) has the same name-only weakness and should be keyed the same way.

**Verifica adversariale**

The code and the false premise are both exactly as quoted (ScriptGenerator.cs:130-139, comment at :134; the same claim is repeated at TableScriptEmitter.cs:447-449). The SQL Server fact is on the reviewer's side: constraints are sys.objects rows carrying their parent table's schema_id, so a name must be unique within the SCHEMA, not the database — `s1.a CONSTRAINT pk_x PRIMARY KEY` and `s2.b CONSTRAINT pk_x PRIMARY KEY` coexist legally. (The belief almost certainly comes from SQL Server's own misleading in-schema error text, "There is already an object named 'x' in the database".) With two same-named FKs in different schemas, `fkDropNames.Add(fk.Name)` swallows the second, so either the referenced DROP TABLE dies on Msg 3726 or — for the (a) source, where a Different table merely lost the FK — the stale FK silently survives in production with a success verdict, which is the worse half. Introduced by these commits for the (a) path: pre-diff each table's `EmitFkDelta` emitted its own DROP with no cross-table dedupe, so both fired. (The pre-existing `rebuildOrchestratedFkNames` was already name-keyed, so the rebuild path had a narrower version of the same flaw.) Medium is right — it needs mirrored schemas with duplicated constraint names, which is a real multi-tenant pattern but not the common case.

---

## [medium] SL-7 · silent-loss

**The rebuild's _tmp table now carries no DEFAULT at all, so a source-only NOT NULL column with a default makes the row-copy INSERT fail**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`:624 · effort S · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

TableScriptEmitter.cs:624-632 `if (namedDefault is not null && inlineNamedDefault) { … } else if (namedDefault is null && !string.IsNullOrEmpty(c.DefaultExpression)) { sb.Append(" DEFAULT ").Append(c.DefaultExpression); }` — with `inlineNamedDefault: false` (the rebuild's `EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false)` at :465-467) a column that has a named default gets neither branch, so no default is emitted. Meanwhile :437-443 `commonInsertable` = `newT.Columns.Where(c => oldColNames.Contains(c.Name) && c.ComputedExpression is null)` — a column absent from the old table is never in the INSERT column list. Every live default is a named `DefaultConstraint` (ConstraintReader.cs:275), so this is the normal case, not an edge one.

**Scenario**

`dbo.Fatture` (2M rows) gets an IDENTITY change on `Id` → rebuild, and the source simultaneously adds `Stato tinyint NOT NULL CONSTRAINT DF_Fatture_Stato DEFAULT ((0))`. `_tmp` is created as `[Stato] [tinyint] NOT NULL` with no default; `INSERT INTO [dbo].[Fatture_tmp] (Id, …) SELECT Id, … FROM [dbo].[Fatture];` supplies no value for `Stato` → Msg 515 "Cannot insert the value NULL into column 'Stato'". The rebuild is impossible for any schema change that both flips identity and adds a NOT NULL defaulted column. (Not a regression in outcome — before this change the same case died later with Msg 1781 on the named re-add — but the fix closed one half and left the other open.)

**Fix**

Emit the inline named default on `_tmp` only for columns that are absent from `oldT` (they need it for the INSERT to be legal), and record those names so the post-`sp_rename` loop at :494-501 skips their standalone re-add — the same `inlinedNamedDefaults` pattern `EmitAlter` already uses at :207 / :306.

**Verifica adversariale**

Traced the whole rebuild. `EmitRebuild` calls `EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false)` (TableScriptEmitter.cs:465-467); `namedDefaults` is now computed unconditionally (:43) and passed with `inlineNamedDefault: false`, so in `FormatColumn` the first branch fails on the flag and the second fails on `namedDefault is null` (:624-632) — no default is emitted on _tmp. `commonInsertable` (:437-443) only includes columns present in the OLD table, so a source-only column is never in the INSERT list, and the column lands NULL in a NOT NULL slot → Msg 515. Every live default is a named DefaultConstraint (ConstraintReader.ReadDefaultsAsync), so the suppression is the normal case. The suppression itself is deliberate and documented (:36-42 and the test `Rebuild_tmp_table_carries_no_inline_default_for_a_named_default`), but that documented reasoning only considers columns the re-add will cover after sp_rename; it does not consider a column that the INSERT needs a value for BEFORE the rename, which is the hole. The reviewer's own "not a regression in outcome" is correct and I verified why: pre-diff `colsWithNamedDefault` was `includeNamedConstraints ? [...] : []`, so the rebuild path emitted the default inline-unnamed, the INSERT succeeded, and the run then died at the named re-add on Msg 1781 — so the scenario was already impossible, just failing later. Medium is right: loud, transactional, and needs the identity-flip + new-NOT-NULL-defaulted-column combination.

---

## [medium] TQ-04 · test-quality

**ExecuteAsync_rejects_a_negative_command_timeout asserts nothing — the Task from ThrowAsync is discarded**

- file: `tests/DbDelta.Persistence.UnitTests/Sql/SqlExecutorTests.cs`:166 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`[Fact] public void ExecuteAsync_rejects_a_negative_command_timeout() { Func<Task> act = () => SqlExecutor.ExecuteAsync(…, commandTimeoutSeconds: -1); act.Should().ThrowAsync<ArgumentOutOfRangeException>(); }` — FluentAssertions 7.0.0 (`Directory.Packages.props:38`) returns `Task<ExceptionAssertions<T>>` from `AsyncFunctionAssertions.ThrowAsync`. The test method is `void`, not `async Task`, so no CS4014 fires and the returned Task is dropped on the floor; any assertion failure surfaces only as an unobserved task exception, which xUnit does not fail the test on.

**Scenario**

Delete `ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);` from SqlExecutor.cs:80 and this test still passes green. The only test of the new `--command-timeout` validation is inert, so a future refactor that drops the guard ships silently: `--command-timeout -1` then reaches `new SqlCommand(batch, cn) { CommandTimeout = -1 }` and throws ArgumentException from inside the try, which is caught and reported as a generic deploy failure.

**Fix**

`public async Task ExecuteAsync_rejects_a_negative_command_timeout()` and `await act.Should().ThrowAsync<ArgumentOutOfRangeException>();`. Then grep the rest of the suite for other un-awaited `ThrowAsync`/`NotThrowAsync` calls.

**Verifica adversariale**

The test is inert as described. SqlExecutorTests.cs:155-167: the method is `void`, `act.Should().ThrowAsync<ArgumentOutOfRangeException>()` returns `Task<ExceptionAssertions<T>>` (FluentAssertions 7.0.0, Directory.Packages.props:38) and the Task is discarded. CS4014 only fires inside `async` methods, so TreatWarningsAsErrors (Directory.Build.props) does not catch it, and there is no FluentAssertions.Analyzers package. I traced the failure path: with the guard at SqlExecutor.cs:80 deleted, ExecuteAsync reaches `cn.OpenAsync` and awaits, so the subject task is incomplete when ThrowAsync awaits it, the assertion runs on a continuation, and the resulting failure lands in the dropped Task as an unobserved exception — xunit.v3 does not fail on those. Test goes green with the guard removed. Correcting severity down to medium: the guard itself is present and correct today, so nothing is broken in the product — this is a test-suite gap, not 'wrong result or crash on a realistic path'. Also worth noting the mirror risk if the guard ever goes: `commandTimeoutSeconds: -1` reaches `new SqlCommand(...) { CommandTimeout = -1 }` inside the try at :117-119 and is swallowed into a generic deploy failure.

---

## [medium] TQ-06 · test-quality

**rolledBack:true is reported after --no-transaction left earlier batches committed**

- file: `src/DbDelta.Persistence/Sql/SqlExecutor.cs`:189 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

TryRollbackAsync's no-transaction branch: `if (cn.State != Open) { return false; } … new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", cn) … await rollback.ExecuteNonQueryAsync(CancellationToken.None); return true;`. With `useOwnTransaction: false` and a plain script, @@TRANCOUNT is 0, the statement is a no-op, and the method still returns true. SqlBatchResult's own doc says RolledBack means "a rollback was issued and acknowledged, so the target is known to be unchanged". ApplyCommand.cs:114 publishes it: `rolledBack = result.RolledBack, transaction = selfManaged ? "script" : useOwnTransaction ? "client" : "none"`.

**Scenario**

`dbdelta apply --script s.sql --no-transaction` where batch 1 creates dbo.KeptBatch and batch 2 fails. Output JSON: `{"success":false, "rolledBack":true, "transaction":"none"}` while dbo.KeptBatch exists in the target. An operator (or a CI wrapper) reading rolledBack:true concludes nothing was applied and skips cleanup. The new acceptance test No_transaction_opt_out_leaves_the_earlier_batches_applied proves KeptBatch survives but never inspects stdout, so the contradiction ships untested.

**Fix**

Only claim a rollback when something was open: `SELECT @@TRANCOUNT` first (or `IF @@TRANCOUNT > 0 BEGIN ROLLBACK; SELECT 1 END ELSE SELECT 0` via ExecuteScalar) and return that. Assert the JSON `rolledBack` value in both new acceptance tests.

**Verifica adversariale**

Reproduced. SqlExecutor.cs:189-201: with `tx is null` the method only checks `cn.State != Open`, runs `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;` and returns true unconditionally on success. With `--no-transaction` on a plain script @@TRANCOUNT is 0, so the statement is a no-op and the method still reports true. That contradicts the record's own contract at SqlExecutor.cs:15-21 ('a rollback was issued and acknowledged, so the target is known to be unchanged'), and ApplyCommand.cs:114-115 publishes it verbatim alongside `transaction:"none"`. Introduced here — pre-diff SqlBatchResult had no RolledBack member at all. Test claim verified: I read the new `No_transaction_opt_out_leaves_the_earlier_batches_applied` acceptance test; it asserts the exit code and `ObjectExistsAsync(dbo.KeptBatch) == true` and never inspects stdout, so the contradictory JSON ships unpinned. medium is right: misleading operator/CI signal, not damage in itself. Note the app path is unaffected in practice — MainWindowViewModel.cs:654 passes `useOwnTransaction: false` for a script that really does own its transaction, where true is roughly accurate.

---

## [medium] TQ-08 · test-quality

**Rebuild _tmp table now created with NOT NULL and no default — a new NOT NULL column with a named default fails Msg 515 on the row copy**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`:624 · effort M · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

FormatColumn: `if (namedDefault is not null && inlineNamedDefault) { … CONSTRAINT [name] DEFAULT … } else if (namedDefault is null && !string.IsNullOrEmpty(c.DefaultExpression)) { … DEFAULT … }` — for the rebuild's `EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false)` (line 465) both arms are false, so a column with a named DEFAULT gets no default at all on `_tmp`. Meanwhile `commonInsertable` excludes columns absent from the old table: `newT.Columns.Where(c => oldColNames.Contains(c.Name) && c.ComputedExpression is null)` (line 439). The old code passed `hasNamedDefault: false` for this path, so `_tmp` did get an inline (auto-named) default. The new test Rebuild_tmp_table_carries_no_inline_default_for_a_named_default uses `CreatedAt`, present on BOTH sides, so it never reaches the new-column sub-case.

**Scenario**

One change set flips `Invoice.Id` to IDENTITY (forcing a rebuild) and adds `Invoice.Status int NOT NULL CONSTRAINT DF_Invoice_Status DEFAULT ((0))`. Script: `CREATE TABLE [dbo].[Invoice_tmp] ([Id] [int] IDENTITY(1,1) NOT NULL, …, [Status] [int] NOT NULL)` then `INSERT INTO [dbo].[Invoice_tmp] ([Id], [Amount]) SELECT [Id], [Amount] FROM [dbo].[Invoice];` → Msg 515, "Cannot insert the value NULL into column 'Status'". Deploy rolls back on any populated table.

**Fix**

EmitRebuild already drops the old table's named non-FK constraints before creating `_tmp` (line 457), so the names are free: emit the named DEFAULT inline on `_tmp` and skip DefaultConstraints from the post-rename re-add loop (line 494) for columns that carried it inline. Add a rebuild test that also introduces a new NOT NULL column with a named default.

**Verifica adversariale**

The defect is real in the current code but the regression flag is WRONG — `regression-from-this-diff` should be false. Current behaviour verified: EmitRebuild calls `EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false)` (TableScriptEmitter.cs:465), FormatColumn's two default arms (:624, :629) are both false for a column carrying a named DefaultConstraint, `commonInsertable` excludes columns absent from oldT (:439), and the re-add loop (:494) only fires after sp_rename — so a new NOT NULL column with a named DEFAULT gets `[Status] [int] NOT NULL` on _tmp and the row copy hits Msg 515 on a populated table. But I checked the pre-diff code: `git show 19131d8:.../TableScriptEmitter.cs` set `colsWithNamedDefault = []` when `includeNamedConstraints` was false, so FormatColumn's `if (!hasNamedDefault && !IsNullOrEmpty(c.DefaultExpression))` DID emit an inline auto-named default on _tmp — and the post-rename `ADD CONSTRAINT [DF_Invoice_Status] DEFAULT … FOR [Status]` then collided with Msg 1781. Both sysdefault paths populate Column.DefaultExpression and a DefaultConstraint (TableReader.cs:36, ConstraintReader DefaultsQuery), so both versions produce a failing script for this scenario; the fix traded Msg 1781 for Msg 515 and is strictly better on an empty table. So this is incomplete coverage, not a broken working case. Test claim verified: `Rebuild_tmp_table_carries_no_inline_default_for_a_named_default` uses CreatedAt present on BOTH sides, and the sibling non-rebuild case IS covered (`EmitAlter_adds_not_null_column_with_its_default_inline`), which makes the rebuild gap the obvious one to close. medium stands.

---

## [medium] TQ-09 · test-quality

**Rebuild trigger rescue covers only Identical triggers; a state-only Different trigger on a rebuilt table emits DISABLE TRIGGER against an object the rebuild destroyed**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:351 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

The new pass filters hard: `result.Differences.Where(x => x.Identity.Kind == "Trigger" && x.Status == DifferenceStatus.Identical)`. The comment justifies it with "Triggers that DIFFER are already re-emitted by the CREATE pass above" — but TriggerScriptEmitter.EmitDifferent (line 48) does NOT re-emit a body when only the state changed: `if (bodiesMatch && sideA.IsDisabled != sideB.IsDisabled) { string verb = sideA.IsDisabled ? "DISABLE" : "ENABLE"; return $"{verb} TRIGGER [{sideA.Schema}].[{sideA.Name}] ON [{sideA.ParentSchema}].[{sideA.ParentTable}];"; }`. The new test Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled only covers the Identical arm.

**Scenario**

`dbo.Invoice` gets an identity flip (rebuild) and in the same change set `trg_Invoice_Audit` is re-enabled on the source (identical body, IsDisabled false vs true). Trigger pair status is Different, so the Identical-only pass skips it and the CREATE pass emits just `ENABLE TRIGGER [dbo].[trg_Invoice_Audit] ON [dbo].[Invoice];` — after `DROP TABLE [dbo].[Invoice]` took the trigger with it. Msg 4916, deploy rolls back.

**Fix**

Widen the pass to every trigger whose `(ParentSchema, ParentTable)` is a rebuild target and whose status is Identical OR body-equal-but-state-flipped, forcing `EmitCreateOrAlter` + the DISABLE follow-up; or have the CREATE pass consult rebuildTargets and never take the state-only shortcut for them.

**Verifica adversariale**

Mechanism verified; regression flag should be false, not true. The new pass filters `x.Status == DifferenceStatus.Identical` (ScriptGenerator.cs:351) and its comment justifies that with 'Triggers that DIFFER are already re-emitted by the CREATE pass' — TriggerScriptEmitter.cs:48-52 falsifies it: when bodies match and only IsDisabled flipped, EmitDifferent returns a bare `ENABLE`/`DISABLE TRIGGER` and no body. Topo order puts the Table rebuild (DROP TABLE, TableScriptEmitter.cs:488) before the Trigger pair, so the ENABLE lands on an object the rebuild destroyed. Pre-existing though: `git show 19131d8` has no trigger rescue pass at all, so the state-only case broke identically before this diff — the commit narrowed the hole rather than opening it. Test claim verified: `Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled` covers only the Identical arm. Worth adding: the same pass has a second sibling gap in the other direction — a body-Different trigger on a rebuilt table that is disabled on the source is re-emitted via CREATE OR ALTER (which yields an ENABLED trigger) and no DISABLE is appended, because the DISABLE at :361-366 only runs inside the Identical-only loop. medium stands: loud failure, no silent loss.

---

## [medium] TQ-10 · test-quality

**PermissionScriptEmitter fallback now widens an unresolvable object permission into a database-scoped grant instead of failing loudly**

- file: `src/DbDelta.Core/ScriptGen/PermissionScriptEmitter.cs`:84 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`private static string? FormatTarget(Permission p) => p.ClassDesc switch { "DATABASE" => null, "SCHEMA" => …, _ when ObjectSchema && ObjectName => …, _ when ObjectName => …, _ => null };` plus `AppendOnTarget`: `if (target is null) { return; }` — the ON clause is simply omitted. The `_ => null` arm was `_ => "DATABASE"` before. PermissionReader.cs:39 admits `class_desc IN ('DATABASE','SCHEMA','OBJECT_OR_COLUMN')` and resolves the name through LEFT JOINs (lines 27-33), so an OBJECT_OR_COLUMN row with an unresolved `obj.name` reaches the fallback. No test covers the fallback arm — the two new tests both use ClassDesc "DATABASE".

**Scenario**

A target-only OBJECT_OR_COLUMN permission whose securable the LEFT JOIN cannot resolve (permission left behind on a dropped object, or an object outside the reader's visibility) yields ObjectSchema=null, ObjectName=null. With a Permission row selected (which now clears IgnorePermissions, per the DeployScriptBuilder change) the emitter produces `GRANT CONTROL TO [app_user];` — a valid DATABASE-scoped CONTROL grant, i.e. db_owner-equivalent. Before this diff it produced `GRANT CONTROL ON DATABASE TO [app_user];`, a syntax error that aborted the deploy.

**Fix**

Distinguish "database-scoped, no ON clause" from "unknown securable": return a sentinel for the latter and emit `-- WARNING: permission on an unresolvable securable was skipped` (or throw) instead of an unqualified GRANT. Add a test for ClassDesc="OBJECT_OR_COLUMN" with null names.

**Verifica adversariale**

Code change confirmed verbatim: `git show 19131d8:.../PermissionScriptEmitter.cs` had `"DATABASE" => "DATABASE"` and `_ => "DATABASE"`; the current file (PermissionScriptEmitter.cs:79, :84) returns null for both, and AppendOnTarget (:70-75) then omits the ON clause entirely. So an unresolvable object permission that used to emit invalid T-SQL now emits a valid database-scoped grant. The negative-control test in the new EmittedSqlParsesTests (`GRANT CONNECT ON DATABASE TO [app];`, line 105) is itself the proof that the pre-diff form aborted the deploy — the change swapped a hard stop for a silent privilege widening. Reachability is better than 'speculative': PermissionReader.cs admits `class_desc IN ('DATABASE','SCHEMA','OBJECT_OR_COLUMN')` and LEFT JOINs `sys.objects`, which excludes system objects (they live in sys.system_objects), so an OBJECT_OR_COLUMN grant on a system view or proc resolves obj.name and sch.name to NULL and lands on the `_ => null` arm — emitting e.g. `GRANT SELECT TO [monitor];` database-wide. Reaching it needs a Permission row selected, which the same diff made possible (DeployScriptBuilder.OptionsFor clears IgnorePermissions, :92-94). Both new permission tests use ClassDesc DATABASE and the ScriptDom fixture uses a fully-resolved OBJECT_OR_COLUMN, so the fallback arm is untested. medium stands.

---

## [medium] TQ-12 · test-quality

**ScriptManagesItsOwnTransaction's "comment immunity" is asserted but not implemented — a block-commented envelope disables the new atomicity**

- file: `tests/DbDelta.Persistence.UnitTests/Sql/SqlExecutorTests.cs`:149 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

InlineData comment: "A mention inside a comment or a name must not count as an opener: the pattern anchors to the start of a line" — followed by exactly one case, `"SELECT 1; -- BEGIN TRANSACTION would go here"`. The pattern is `@"^\s*BEGIN\s+TRAN(SACTION)?\b"` with RegexOptions.Multiline (SqlExecutor.cs:265), which has no comment awareness at all.

**Scenario**

A DbDelta script whose envelope was commented out with a block comment — `/*\nBEGIN TRANSACTION\nGO\n*/` — matches the pattern, so ApplyCommand computes `selfManaged = true`, `useOwnTransaction = false`, and runs the script with no transaction at all. Batch 3 of 12 fails and the database is left half-migrated: precisely the hole commit ed051a3 claims to close, reachable through the very edit case its own XML doc names ("a generated one whose envelope was edited out").

**Fix**

Either strip `/* … */` and `--` comments before matching, or accept the ceiling explicitly and drop the false claim from the test comment. The cheap correct version: since `apply` already splits on GO, run the pattern per batch after stripping line comments.

**Verifica adversariale**

The root cause is real — `^\s*BEGIN\s+TRAN(SACTION)?\b` with RegexOptions.Multiline (SqlExecutor.cs:265-268) has zero comment or string-literal awareness, and the InlineData comment at SqlExecutorTests.cs:149-151 claims immunity while proving only the same-line `--` case. Two corrections. First, their literal scenario is broken: with `GO` inside the block comment, SplitOnGo (:211-243) splits on it and batch 1 ends as an unterminated `/*`, so the script dies on a parse error rather than half-migrating; drop the GO (`/*\nBEGIN TRANSACTION\n*/`) and the scenario works as described. Second, and why I am raising severity: a far more realistic instance of the same defect exists. Any hand-written or third-party script containing `CREATE PROCEDURE … BEGIN TRANSACTION` at line start — indented counts, `^\s*` allows it — is classified self-managed, so ApplyCommand.cs:101-102 computes `selfManaged = true, useOwnTransaction = false` and the script runs with no transaction at all. That is exactly the population the ApplyCommand XML doc (:13-24) says must get a client transaction, and exactly the half-migration hole ed051a3 claims to close. Generated scripts are unaffected (DeploymentScriptWriter.cs:24 emits BEGIN TRANSACTION at line start, so the detection is correct for them).

---

## [low] ER-4 · empirical-revert

**Third assertion of Rebuild_recreates_every_index_including_the_identical_ones is a no-op, and its comment states the opposite of what the code does**

- file: `tests/DbDelta.Core.UnitTests/ScriptGen/TableRebuildPkSwapTests.cs`:288 · effort S · regressione di questo diff: **True** · verdetto **UNVERIFIED**

**Evidenza**

        // The index is identical on both sides, so the delta path would have
        // emitted a DROP for it — the rebuild already destroyed it.
        sql.Should().NotContain("DROP INDEX [IX_Invoice_Amount]");

// ScriptGenerator.EmitIndexDelta, the "delta path" the comment describes:
        foreach (TableIndex t in tgt.Indexes)
        {
            if (alreadyDropped is not null && alreadyDropped.Contains(t.Name)) { continue; }
            bool stillThere = srcByName.TryGetValue(t.Name, out TableIndex? s);
            bool shapeChanged = stillThere && !IndexShapeEqual(t, s!);
            if (!stillThere || shapeChanged)   // <- both false for an identical index

**Scenario**

The test's fixture uses the SAME `TableIndex ix` instance on both sides, so in the delta path `stillThere == true` and `shapeChanged == false`, and no DROP is ever emitted. I confirmed the rest of the test is real — reverting the rebuild index re-emission made assertion 2 fail (`Expected createIxIdx to be greater than 741 ..., but found -1`) — but assertion 3 would pass identically with the fix reverted, and the comment justifying it ("the delta path would have emitted a DROP for it") is factually wrong about DbDelta's own code. A reader trusting that comment will believe the delta path drops identical indexes, which is the opposite of the truth and is precisely why the re-emission pass was needed in the first place.

**Fix**

Either delete the assertion and its comment, or make it mean something: give the two sides *differently shaped* same-named indexes so the delta path really would emit a DROP, then assert the rebuild path took over instead. Fix the comment to say "the delta path emits nothing at all for an identical index, which is why the full source-side set has to be re-created here".

---

## [low] EXEC-3 · executor-transactions

**The unit test for the negative-timeout guard never awaits its assertion — it passes with the guard deleted**

- file: `D:\Develop\AI\_ClaudeCode\SQL Compare\tests\DbDelta.Persistence.UnitTests\Sql\SqlExecutorTests.cs`:166 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

[Fact]
public void ExecuteAsync_rejects_a_negative_command_timeout()
{
    // 0 means unlimited; negative is a caller bug, not "very short".
    Func<Task> act = () => SqlExecutor.ExecuteAsync(... commandTimeoutSeconds: -1);

    act.Should().ThrowAsync<ArgumentOutOfRangeException>();
}

The test method is `void`, and `ThrowAsync` returns a `Task<ExceptionAssertions<T>>` that is discarded. FluentAssertions performs the check inside that task, so nothing is ever evaluated on the test's thread. Because the method is not `async`, the compiler emits no CS4014 either, so the TreatWarningsAsErrors gate does not catch it.

**Scenario**

Delete `ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);` from SqlExecutor.cs:80 and this test still passes green. It is the only coverage of the new parameter's validation, so the guard is effectively untested — and the guard matters, because it is the only thing standing between `--command-timeout -1` and `SqlCommand.CommandTimeout = -1` (which throws from SqlClient deeper in, at a point where the connection is already open).

**Fix**

`public async Task` + `await act.Should().ThrowAsync<ArgumentOutOfRangeException>();`. Then also verify it fails without the guard.

**Verifica adversariale**

Verified. SqlExecutorTests.cs:155-167 is `void`, and FluentAssertions 7.0.0's `Func<Task>.Should().ThrowAsync<T>()` is an async method returning Task<ExceptionAssertions<T>> that performs the check after awaiting the subject — the returned task is discarded. With the guard present the whole thing happens to complete synchronously (ArgumentOutOfRangeException.ThrowIfNegative throws before ExecuteAsync's first await, so the returned Task is already faulted and the await inside InvokeWithInterceptionAsync completes inline) — which is why it passes today. Delete SqlExecutor.cs:80 and ExecuteAsync suspends at `cn.OpenAsync`, ThrowAsync returns an incomplete task, the test method returns, and the eventual assertion failure lands on an unobserved task that neither xUnit v3 nor the runtime surfaces. No CS4014 because the method is not async, and I checked tests/DbDelta.Persistence.UnitTests/DbDelta.Persistence.UnitTests.csproj plus Directory.Build.props: no FluentAssertions.Analyzers, so no analyzer catches it either. Severity medium -> low: the guard itself is present and correct, so the impact is zero coverage on one argument check, with no user-visible consequence today.

---

## [low] EXEC-4 · executor-transactions

**--command-timeout accepts a negative value; the resulting exception escapes the action and produces an exit code outside DbDelta's contract**

- file: `D:\Develop\AI\_ClaudeCode\SQL Compare\src\DbDelta.Cli\Commands\ApplyCommand.cs`:43 · effort S · regressione di questo diff: **True** · verdetto **PLAUSIBLE**

**Evidenza**

Option<int> commandTimeout = new("--command-timeout") { ... DefaultValueFactory = _ => SqlExecutor.CommandTimeoutSeconds };
...
int timeout = parseResult.GetValue(commandTimeout);
...
SqlBatchResult result = await SqlExecutor.ExecuteAsync(tgtConn, script, ct, useOwnTransaction, timeout);

No validator on the option. SqlExecutor.ExecuteAsync line 80 is `ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);` inside an `async` method, so the exception surfaces as a faulted Task at the await and propagates out of the SetAction delegate. Program.cs is `return await root.Parse(args).InvokeAsync();` with no try/catch, and System.CommandLine 2.0.0-beta5's own XML doc (CommandLineConfiguration.EnableDefaultExceptionHandler) says the default handler is "Enabled by default".

**Scenario**

`dbdelta apply --target ... --script x.sql --command-timeout -1` prints a raw unhandled-exception dump on stderr, emits no JSON result object at all, and returns System.CommandLine's default-handler exit code (1) rather than any code in DbDelta.Cli/ExitCodes.cs. In DbDelta's own published scheme 1 is `SuccessDifferencesFound`, so a pipeline whose gate is `exit -le 1 = ok` treats a typo'd flag on an apply as a successful deployment and moves on. Same shape for any other unexpected throw on this path.

**Fix**

Add a validator to the option (`commandTimeout.Validators.Add(r => { if (r.GetValue(commandTimeout) < 0) r.AddError("--command-timeout must be >= 0 (0 = unlimited)"); })`) so a bad value is a parse error, and wrap the action body in a try/catch returning `ExitCodes.InternalError` (99) so nothing on the apply path can ever exit in the 0/1 success range.

**Verifica adversariale**

The facts check out: no validator on the option (ApplyCommand.cs:43-50), the guard is inside an async method so it surfaces as a faulted Task at the await and escapes SetAction, Program.cs is a bare `return await root.Parse(args).InvokeAsync();`, and I confirmed the quoted XML doc in the beta5 package (EnableDefaultExceptionHandler 'Enabled by default'). What is missing is the novelty. The 'exit code outside the contract' shape is pre-existing and CLI-wide: System.CommandLine's default handlers already return 1 for every parse error on every DbDelta verb (`--command-timeout abc`, an unknown option, a missing required option), and an unexpected throw already escapes this same action on a pre-existing path (File.ReadAllTextAsync on an unreadable-but-existing --script file). So a pipeline gating on `exit -le 1` was already broken at 19131d8; this diff only adds one more easy trigger. Severity medium -> low, fix is a one-line option validator.

---

## [low] EXEC-6 · executor-transactions

**The deliberately uncancellable 15 s rollback is killed at 2 s by System.CommandLine's process-termination timeout, and the result JSON is never printed**

- file: `D:\Develop\AI\_ClaudeCode\SQL Compare\src\DbDelta.Persistence\Sql\SqlExecutor.cs`:45 · effort S · regressione di questo diff: **True** · verdetto **PLAUSIBLE**

**Evidenza**

SqlExecutor.cs:40-45:
/// Short, fixed timeout for the best-effort rollback. ...
private const int RollbackTimeoutSeconds = 15;
...and the rollback runs with CancellationToken.None "because a rollback must never be cancellable".

Program.cs: `return await root.Parse(args).InvokeAsync();` — default configuration. System.CommandLine 2.0.0-beta5 XML doc, CommandLineConfiguration.ProcessTerminationTimeout: "Enables signaling and handling of process termination (Ctrl+C, SIGINT, SIGTERM) via a CancellationToken that can be passed to a CommandLineAction during invocation. If not provided, a default timeout of 2 seconds is enforced."

**Scenario**

The workload the whole `--command-timeout 0` change exists for is a long batch (the commit names an INSERT…SELECT over 30M rows during an identity rebuild). Operator runs `dbdelta apply --command-timeout 0`, changes their mind after ten minutes, presses Ctrl+C. The token cancels, ExecuteNonQueryAsync aborts, TryRollbackAsync starts with a 15 s budget and CancellationToken.None — but rolling back a 30M-row insert takes minutes, and System.CommandLine kills the process 2 s after the signal. Result: the JSON block is never written, so the operator gets no `success`, no `error`, and above all no `rolledBack`; the exit code is the termination handler's, not `DeploymentCancelled` (41). The database itself does end up consistent (socket drop -> server-side rollback), but it stays locked under SERIALIZABLE for the duration with nothing on screen explaining why. Cancellation is the exact case the hardened rollback was written for, and it is the one case where the report cannot be delivered.

**Fix**

Either raise the budget — `root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromMinutes(N) })` (or null to opt out of forced termination) — or, cheaper, do not depend on outliving the signal: print the JSON result before attempting the rollback is not possible, so at minimum write a one-line "cancellazione in corso, rollback lato server in corso" to stderr as soon as cancellation is observed, so a hard kill is not silent. Note in the XML doc that RollbackTimeoutSeconds is only reachable on the timeout path under the CLI.

**Verifica adversariale**

The mechanism is real enough to keep on the list but the finding misdescribes the code and overstates the impact. Verified in the beta5 package: the ProcessTerminationTimeout doc says exactly what is quoted, and System.CommandLine.Invocation.ProcessTerminationHandler carries SIGINT_EXIT_CODE = 130 / SIGTERM_EXIT_CODE = 143 (reflected out of the assembly), so a Ctrl+C that outlives the window does return an exit code outside DbDelta/ExitCodes.cs and loses the JSON. I could not verify from the shipped IL that the handler hard-returns after the timeout rather than continuing to wait, so this stays PLAUSIBLE. Two corrections: the '15 s budget' is wrong — RollbackTimeoutSeconds applies only to the @@TRANCOUNT probe in the tx == null branch; the client-transaction branch (SqlExecutor.cs:180) calls tx.RollbackAsync with no CommandTimeout at all and can block indefinitely, which makes the hang worse than described, not better. And the pre-diff comparison is weaker than claimed: cancelling a long INSERT…SELECT already blocks inside ExecuteNonQueryAsync until the server acknowledges the attention signal, so exceeding 2 s on Ctrl+C did not need this diff. Net effect of the change is a larger post-cancel rollback (batches 1..n-1 now included) and therefore a higher chance of losing the report. Severity medium -> low: the database ends consistent, only the report is lost.

---

## [low] SCHEMA-02 · schema-kind-and-persistence

**Schema drops are emitted before the permission REVOKEs that reference those schemas**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`:455 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

ScriptGenerator.Generate, lines 453-463: `EmitSchemaDrops(writer, pairs);` immediately followed by `if (!options.HasFlag(ComparisonOptions.IgnorePermissions)) { EmitPermissions(writer, pairs); }`. PermissionScriptEmitter.FormatTarget: `"SCHEMA" => $"SCHEMA::[{p.ObjectSchema ?? p.ObjectName}]"`. PermissionReader reads class_desc 'SCHEMA' rows. DeployScriptBuilder.OptionsFor clears IgnorePermissions whenever the user ticked any Permission row.

**Scenario**

Target has schema `legacy` (absent from source) plus `GRANT SELECT ON SCHEMA::[legacy] TO [app]` (also absent from source). In the Avalonia app the user ticks the `legacy` schema row, its tables, and any Permission row — which clears IgnorePermissions for the whole script. Emission order becomes: DROP TABLE [legacy].* ... DROP SCHEMA [legacy]; ... REVOKE SELECT ON SCHEMA::[legacy] FROM [app];. The REVOKE runs against a schema that no longer exists → Msg 15151 ("Cannot find the schema 'legacy', because it does not exist") → error gate → NOEXEC ON → ROLLBACK of the entire deploy. The schema drop pass is new, so this ordering hazard is introduced here; permissions were already the last pass.

**Fix**

Move EmitSchemaDrops after EmitPermissions (a schema's own permissions disappear with the schema, so revoking first is harmless and revoking last is fatal), or skip REVOKE emission for any Permission whose ObjectSchema matches a schema being dropped in this script.

**Verifica adversariale**

Ordering and target formatting both verified against the real code. ScriptGenerator.cs:453-463: EmitSchemaDrops(writer, pairs) then `if (!options.HasFlag(IgnorePermissions)) EmitPermissions(...)`. PermissionReader.cs:29-31 joins sys.schemas on `p.class_desc='SCHEMA' AND sch.schema_id = p.major_id`, so a SCHEMA-class row carries the schema name in ObjectSchema with ObjectName NULL; PermissionScriptEmitter.FormatTarget then yields `SCHEMA::[legacy]` and EmitRevoke emits `REVOKE ... ON SCHEMA::[legacy] FROM [app];` after the schema is gone → Msg 15151 → NOEXEC ON → full rollback. Reachable from both entry points: DeployScriptBuilder.OptionsFor clears IgnorePermissions when any Permission row is selected, and ScriptCommand.cs:76 clears it too.

Severity corrected down: the trigger conjunction is narrow (target-only schema AND a target-only SCHEMA-class permission on it AND the user opting into permissions), the failure is loud and transactional with no damage, and the REVOKE is redundant anyway — DROP SCHEMA already removes the permission row, so the fix is to suppress REVOKEs whose target schema is being dropped rather than to reorder. Introduced by this diff: true (permissions were already last; the schema-drop pass is what moved in front of them).

---

## [low] SCHEMA-03 · schema-kind-and-persistence

**The new Schema kind is not registered in the Avalonia grid's own kind tables: English label, sorts last, name renders as "sales."**

- file: `src/DbDelta.App.Avalonia/ViewModels/DifferenceRowViewModel.cs`:88 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

KindDisplayName (line 88) switch has Table/View/Procedure/Function/Trigger/Sequence/Synonym/UserDefinedType/User/Role/Permission and `_ => Kind`; no "Schema" arm. KindOrder (line 111) has the same set mapped 0..10 and `_ => 99`; no "Schema" arm. QualifiedName (line 60): `string.IsNullOrEmpty(Dto.SchemaName) ? Dto.ObjectName : $"{Dto.SchemaName}.{Dto.ObjectName}"`. Schema.Identity = `new(SchemaName: Name, ObjectName: string.Empty, Kind: "Schema")`. MainWindowViewModel.BuildRowsView sorts by StatusOrder, then KindOrder, then QualifiedName. ResultsGridView.axaml:152 binds "Tipo entità" to KindDisplayName; :177 and :213 bind both name columns to QualifiedName. KindCatalog now says `["Schema"] = "Schemi"` and SortOrder("Schema") == 0. grep KindDisplayName/KindOrder over tests/ returns nothing — no test guards either table.

**Scenario**

Source has schema `sales` containing table `sales.Order`; target has neither. The grid shows two rows in the "Solo provenienza" group. The schema row renders "Tipo entità" = `Schema` (raw English; KindCatalog says "Schemi") and "Nome (orig)" = `sales.` (trailing dot, no object name), and because KindOrder falls through to 99 it sorts BELOW `Permessi` at the very bottom of the group — while KindCatalog declares Schema the FIRST kind. Grouping by "Tipo di oggetto" creates a group header literally titled "Schema" next to "Tabelle"/"Viste". Rows default to unchecked ("the user opts in explicitly"), so the one row the user must find and tick to avoid the Msg 2760 failure 3dd4d09 exists to prevent is the hardest row in the grid to notice or identify. Also a straight violation of the project's DRY rule: two independent kind→label/order tables now disagree.

**Fix**

Delete DifferenceRowViewModel.KindDisplayName and KindOrder and delegate to KindCatalog.DisplayLabel / KindCatalog.SortOrder (which already carry Schema and TableType, the other kind the VM tables are missing). Make QualifiedName return SchemaName alone when ObjectName is empty.

**Verifica adversariale**

Every quoted line checks out. DifferenceRowViewModel.cs:88-101 KindDisplayName and :111-125 KindOrder both stop at 'Permission' with `_ => Kind` / `_ => 99`; QualifiedName (:60) is `IsNullOrEmpty(SchemaName) ? ObjectName : $"{SchemaName}.{ObjectName}"`; Mapper.cs projects Identity.SchemaName/ObjectName verbatim, and Schema.Identity leaves ObjectName empty — so the row really does render 'Schema' / 'sales.' and sort at 99, below Permessi, while KindCatalog.SortOrder("Schema")==0 and DisplayLabel=="Schemi". BuildRowsView (MainWindowViewModel.cs:387-408) sorts StatusOrder→KindOrder→QualifiedName and ApplyGrouping groups on KindDisplayName. grep over tests/ for KindDisplayName|KindOrder returns nothing.

Two corrections. The 'two independent kind→label/order tables now disagree / straight DRY violation' part is PRE-EXISTING, not introduced: 'TableType' has been in KindCatalog.KnownKinds and ItalianLabels all along, ComparisonEngine.cs:27 CompareTableTypeUdts really does produce TableType rows, and DifferenceRowViewModel has no TableType arm in either switch either. Schema is the second instance, not the first. And the functional argument ('the one row the user must find and tick') overstates the label's role: the app performs no dependency closure on selection at all (MainWindowViewModel.cs:673 takes only IsSelected rows), so Msg 2760 is equally reachable with a perfectly-labelled, top-sorted Schema row. That makes this a cosmetic/UX defect on top of a separate design gap — low, not medium.

---

## [low] SCHEMA-04 · schema-kind-and-persistence

**SchemaReader's NOT LIKE 'db[_]%' over-excludes user schemas, re-opening the exact Msg 2760 hole the commit claims to close**

- file: `src/DbDelta.Providers.LiveDb/Readers/SchemaReader.cs`:16 · effort S · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

SchemaReader: `AND s.name NOT LIKE 'db[_]%'`. TableReader.cs TablesQuery: `FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0` — no schema-name exclusion. ModuleReader ViewQuery/ProcQuery: same, only `is_ms_shipped = 0`. ComparisonEngine.cs:58-60 asserts "The reader already filters out the system schemas ... so nothing here can emit a DROP SCHEMA [db_datareader]".

**Scenario**

Source has a user-created schema `db_stage` (or `db_reports`, `db_etl` — a naming convention people do use) holding table `db_stage.Load`. `db_stage` is filtered out of Database.Schemas on BOTH sides by the LIKE, so CompareSchemas produces no row and no CREATE SCHEMA. TableReader has no such filter, so `db_stage.Load` IS read and compared, and the generator emits `CREATE TABLE [db_stage].[Load]` in the topo pass. Against a target lacking the schema that fails with Msg 2760 ("The specified schema name 'db_stage' either does not exist or you do not have permission to use it") — precisely the failure the commit was written to eliminate, still reachable. Mirror case: a target-only `db_stage` gets its tables dropped and the now-empty schema left behind forever, since it can never appear as OnlyInB.

**Fix**

Replace the name heuristic with the catalog's own answer: exclude by schema_id / principal_id instead — `WHERE s.schema_id NOT BETWEEN 16384 AND 16393 AND s.name NOT IN ('sys','INFORMATION_SCHEMA','guest')` (16384..16393 are exactly the ten fixed db_ role schemas), or join sys.database_principals and exclude `is_fixed_role = 1` owners.

**Verifica adversariale**

Verified. SchemaReader.cs:16 `AND s.name NOT LIKE 'db[_]%'` — the `[_]` escape makes it a literal underscore, so a user schema named db_stage is excluded on both sides and can never produce a Schema pair. TableReader.cs TablesQuery/ColumnsQuery and ModuleReader filter only is_ms_shipped=0 with no schema-name exclusion, so db_stage.Load is read and compared and CREATE TABLE [db_stage].[Load] is emitted with no CREATE SCHEMA — the exact Msg 2760 the commit set out to eliminate. The ComparisonEngine.cs:58-60 remark ('The reader already filters out the system schemas ... so nothing here can emit a DROP SCHEMA [db_datareader]') is therefore load-bearing on a name heuristic where an exact check exists (`sys.database_principals.is_fixed_role = 1` on s.principal_id — the fixed db_* schemas are owned by their same-named fixed role, which is precisely what the heuristic is approximating). regression-from-this-diff: false is correct — SchemaReader is untouched by the diff (last touched in 6048d64 / d6dabd6), and before 3dd4d09 no CREATE SCHEMA was emitted for any schema.

Severity down to low: `db_*` as a user-schema naming convention is genuinely rare, and both outcomes are benign — a loud transactional failure, or an orphaned empty schema left on the target.

---

## [low] PERSIST-02 · schema-kind-and-persistence

**Degrade-on-load makes the next write duplicate every autosaved connection instead of touching it**

- file: `src/DbDelta.App.Avalonia/ViewModels/ConnectionStoreViewModel.cs`:55 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

JsonConnectionStore.LoadAsync → ReadDocumentAsync(forWrite: false) → on IOException returns `new Document(CurrentSchemaVersion, [])`. ConnectionStoreViewModel.LoadAsync then does `Entries.Clear();` and adds nothing. AutosaveAsync decides create-vs-touch purely from that in-memory list: `if (Entries.FirstOrDefault(e => string.Equals(e.ServerName, server, ...) && string.Equals(e.DatabaseName, database, ...)) is { } existing) { ...touch... }` else `var id = Guid.NewGuid(); ... await store.UpsertAsync(entry, ct);`. UpsertAsync re-reads the real file (`doc.Entries.Where(e => e.Id != entry.Id)` then appends), so the new Guid is APPENDED next to the entry that was already there.

**Scenario**

connections.json is locked for the few hundred ms of app startup (sync client, AV scan). LoadAsync degrades to empty — that is the intended fix, and no crash occurs. The lock then releases. Every subsequent successful Compare calls AutosaveAsync twice (source + target); Entries is empty so both take the create branch, mint fresh Guids, and UpsertAsync appends them to the file that still holds the originals. After N compares in that session the user's Connection Manager lists 2N duplicate "<server>.<db> (auto)" entries, each with its own DPAPI secret written under dbdelta:connection:{new-guid}, and the originals' LastUsedUtc never advances so the MRU ordering is wrong too. Not loss, but the degrade path silently corrupts the store it was added to protect, and it is newly reachable (before 7fb6a8d the locked file crashed at startup instead).

**Fix**

Have LoadAsync signal degradation (return a Result, or set a `LoadFailed` flag on the store/VM) and make AutosaveAsync a no-op — or force a re-read via a forWrite read — while that flag is set. Cheapest correct version: match on (ServerName, DatabaseName) inside JsonConnectionStore.UpsertAsync against the freshly-read document rather than in the VM, so the dedupe decision always sees the real file.

**Verifica adversariale**

The mechanism is real: LoadAsync degrades to an empty Document, ConnectionStoreViewModel.LoadAsync then clears Entries and adds nothing, AutosaveAsync decides create-vs-touch purely from that in-memory list, and UpsertAsync re-reads the on-disk file (`doc.Entries.Where(e => e.Id != entry.Id)` then append) so the fresh Guid is appended alongside the surviving original, plus a new DPAPI secret under dbdelta:connection:{new-guid}.

But the arithmetic is REFUTED. AutosaveAsync's create branch ends with `Entries.Add(entry)` (ConnectionStoreViewModel.cs, just after the store.UpsertAsync call), so the second and every later compare in that session hits the touch branch. The damage is at most two duplicate '(auto)' entries per degraded session — one for source, one for target — not 2N after N compares, and the MRU ordering is only wrong for the stale originals, which the fresh timestamps outrank anyway. A cosmetic store-hygiene defect newly reachable because startup no longer dies: low, not medium.

---

## [low] PERSIST-03 · schema-kind-and-persistence

**JsonRecentProjectsStore's new future-schema gate destroys the future file, unlike its sibling which moves it aside**

- file: `src/DbDelta.Persistence/Json/JsonRecentProjectsStore.cs`:109 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

JsonRecentProjectsStore.ReadDocumentAsync: `return doc is null || doc.SchemaVersion > CurrentSchemaVersion ? new Document(CurrentSchemaVersion, []) : doc;` — no MoveAside, and the branch is not gated on forWrite. JsonConnectionStore for the same condition: `if (doc.SchemaVersion > CurrentSchemaVersion) { MoveAside("future-schema"); return new Document(CurrentSchemaVersion, []); }`. Commit message: "brings the MRU store up to its sibling's hardening ... and a future-schema-version gate on read."

**Scenario**

User runs a newer DbDelta that writes recent-projects.json with schemaVersion 2, then rolls back to this build (rc→rc rollback is a documented flow). AddOrTouchAsync → ReadDocumentAsync(forWrite: true) → sees SchemaVersion 2 → returns EMPTY (the forWrite rethrow does not apply to this branch) → WriteAtomicAsync overwrites the v2 file with a v1 document containing exactly one entry. The whole MRU is gone with no .broken-* copy to recover from. JsonConnectionStore hits the same forWrite-blind branch but at least renames the old file aside first, so its data survives — the two stores are still not equivalent, and on this specific point the sibling parity claim is false. Same hole applies to the MRU store's `catch (JsonException) { return empty; }`, which likewise neither moves aside nor honours forWrite.

**Fix**

Fold future-schema and invalid-json into the same forWrite policy as the IO failures: on forWrite, rethrow (or return a sentinel that makes AddOrTouchAsync bail) rather than returning empty. If keeping the degrade, at minimum MoveAside first, as JsonConnectionStore does.

**Verifica adversariale**

Verified line for line. JsonRecentProjectsStore.cs:109 `return doc is null || doc.SchemaVersion > CurrentSchemaVersion ? new Document(CurrentSchemaVersion, []) : doc;` — no MoveAside, and the branch precedes both catch clauses so forWrite is irrelevant to it. JsonConnectionStore.cs (same read method) does `if (doc.SchemaVersion > CurrentSchemaVersion) { MoveAside("future-schema"); return new Document(...); }`. So on a v1→v2→v1 rollback AddOrTouchAsync reads empty, WriteAtomicAsync overwrites the v2 file with a one-entry v1 document, and there is no .broken-* copy. The sibling-parity claim in the commit message is genuinely incomplete on this point. The reviewer's aside is also right that the pre-existing `catch (JsonException) { return empty; }` in the MRU store has the same shape — though that half is not introduced by this diff; only the future-schema branch is. Severity low is correct: the loss is a 10-entry path list, no schema or credential data.

---

## [low] TEST-01 · schema-kind-and-persistence

**Schemas_present_on_both_sides_produce_no_row passes with CompareSchemas removed entirely**

- file: `tests/DbDelta.Core.UnitTests/ScriptGen/SchemaEmissionTests.cs`:52 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

Test body: `Database a = Db([new Schema("dbo"), new Schema("sales")]); Database b = Db([new Schema("dbo"), new Schema("sales")]); ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default); r.Differences.Should().NotContain(p => p.Identity.Kind == "Schema");` (line 62).

**Scenario**

Delete `pairs.AddRange(CompareSchemas(a.Schemas, b.Schemas));` from ComparisonEngine.Compare — i.e. revert the entire feature — and this test still passes green, because with no schema comparison at all there is also no Schema row. The test asserts the absence of something, so it cannot distinguish "deliberately suppressed because the pair matches" (what its name and comment claim it proves) from "schemas are never compared" (the bug the commit fixes). Any future refactor that accidentally drops CompareSchemas keeps this test green; only Source_only_schema_is_reported_as_a_difference catches it, and only for the OnlyInA direction.

**Fix**

Make the assertion positive-and-negative in one arrange: give the source a third schema present on only one side, then assert `r.Differences.Where(p => p.Identity.Kind == "Schema").Select(p => p.Identity.SchemaName).Should().Equal("extra")` — the presence half proves the comparer ran, the exclusion half proves dbo/sales were suppressed.

**Verifica adversariale**

The mechanical claim holds: SchemaEmissionTests.cs:62 asserts `r.Differences.Should().NotContain(p => p.Identity.Kind == "Schema")`, which is trivially satisfied if `pairs.AddRange(CompareSchemas(a.Schemas, b.Schemas))` is deleted from ComparisonEngine.Compare. Verified against the real file.

The framing is partly REFUTED, though. The test name and its assertion match exactly — 'Schemas present on both sides produce no row' is precisely what it checks, so it does not overstate. And it is not vacuous: ComparisonResult.Differences carries Identical pairs (only ScriptGenerator.Generate filters them at line 71), so if CompareSchemas were changed to emit Identical pairs for a matching dbo/sales the assertion would fail. It pins the deliberate suppression documented at ComparisonEngine.cs:44-52, which is what it exists for. The 'CompareSchemas accidentally deleted' regression is covered by the two positive tests in the same file (Source_only... and Target_only..., both `ContainSingle`), and the two ordering tests guard their IndexOf results with `BeGreaterThan(0)` before comparing, so no -1 satisfies anything. A missing-mutation-coverage note, not a broken test: low.

---

## [low] SCHEMA-06 · schema-kind-and-persistence

**Schema identifiers are interpolated into DDL without doubling ']'**

- file: `src/DbDelta.Core/ScriptGen/SchemaScriptEmitter.cs`:47 · effort M · regressione di questo diff: **False** · verdetto **CONFIRMED**

**Evidenza**

`public static string EmitCreate(Schema schema) { ...; return $"CREATE SCHEMA [{schema.Name}];"; }` and `EmitDrop` → `$"DROP SCHEMA [{schema.Name}];"`. `grep -rn "QuoteName|EscapeIdentifier|Replace(\"]\"" src` returns nothing — the codebase has no identifier-quoting helper at all.

**Scenario**

A schema legally named with an embedded bracket, e.g. created as `CREATE SCHEMA [a]]b]` (name = `a]b`), round-trips out of sys.schemas as `a]b` and is emitted as `CREATE SCHEMA [a]b];` — a syntax error at best, and the same pattern is a statement-injection vector for any catalog value an attacker controls (requirement: "no injection through catalog values"). Systemic across every emitter, not specific to schemas, but this is a brand-new emitter that repeats the pattern.

**Fix**

Add one shared `static string Q(string id) => "[" + id.Replace("]", "]]", StringComparison.Ordinal) + "]";` in ScriptGen and route every `[{...}]` interpolation through it — one helper, mechanical call-site change, and it closes the class of defect rather than this instance.

**Verifica adversariale**

Verified: SchemaScriptEmitter.cs:47/57 interpolate `schema.Name` into `[...]` with no doubling of ']', and `grep -rn 'QuoteName|EscapeIdentifier|Replace("]"' src` returns nothing — the codebase has no identifier-quoting helper, and every emitter (TableScriptEmitter:183, SequenceScriptEmitter:58, UserScriptEmitter:60, IndexScriptEmitter:47, …) uses the same raw interpolation. A schema created as `CREATE SCHEMA [a]]b]` round-trips out of sys.schemas as `a]b` and re-emits as `CREATE SCHEMA [a]b];`, and a crafted name such as `x]; DROP TABLE dbo.T; --` closes the bracket early and injects a statement into a script a privileged user then runs on the target — a real breach of the 'no injection through catalog values' requirement.

Severity low and regression=false are both right, with a caveat the reviewer should have drawn: because the pattern is systemic, fixing it in SchemaScriptEmitter alone buys nothing. It needs one shared `Q(string)` helper applied across all emitters, which is also the cheapest fix. Exploitation requires CREATE SCHEMA rights on the source database, and the common accidental case is a parse error rather than working injection.

---

## [low] TQ-05 · test-quality

**A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end passes verbatim with the reordering reverted**

- file: `tests/DbDelta.Core.UnitTests/ScriptGen/ForeignKeyDropOrderingTests.cs`:107 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

Body asserts only `dropFk.Should().BeGreaterThan(0); addFk.Should().BeGreaterThan(dropFk);`. The removed code (`EmitFkDelta`, now `EmitFkAdds`) emitted the DROP loop and then the ADD loop into the SAME batch: `foreach (ForeignKey t in tgtFks.Values) { … sb.AppendLine($"ALTER TABLE … DROP CONSTRAINT [{t.Name}];"); }` followed by the add loop. So drop-before-add held before this diff too; the test cannot distinguish "up front" from "at the end", which is the entire claim in its name and in the file's class doc.

**Scenario**

Revert the whole up-front fkDrops pass and restore EmitFkDelta: this test stays green while `Fk_from_a_different_table_is_dropped_before_the_table_it_references` is the only test that actually fails. A partial revert that keeps the DROP inside EmitFkAdds for the reshaped-FK case (a plausible merge accident, since the two halves now live in different methods) would be caught by nothing.

**Fix**

Anchor the drop against a later pass rather than against its own add, e.g. `sql.IndexOf("ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT") .Should().BeLessThan(sql.IndexOf("PRINT N'Altering Table [dbo].[Invoice]'"))`, or assert the drop appears inside the `PRINT N'Dropping foreign keys'` batch.

**Verifica adversariale**

The headline claim is true. I diffed the removed code: `git show 19131d8:.../ScriptGenerator.cs` EmitFkDelta (line 647) appends the DROP loop and then the ADD loop into ONE StringBuilder returned as a single batch, so drop-index < add-index held before the reordering. ForeignKeyDropOrderingTests.cs:126-127 asserts only `dropFk > 0` and `addFk > dropFk`, which cannot distinguish 'up front' from 'in the same batch at the end' — so the test name and the class doc overstate it. Correcting severity down to low: the assertion is NOT vacuous (if the DROP vanished from the split-out path, `dropFk` would be -1 and `BeGreaterThan(0)` would fail — no accidental -1 satisfaction here), and the positional property IS covered for the case that matters by `Fk_from_a_different_table_is_dropped_before_the_table_it_references` two tests above, which anchors the DROP against `DROP TABLE [dbo].[Currency];`. What is genuinely uncovered is the reshaped-FK ordering against an ALTER COLUMN, which no test in the file pins.

---

## [low] TQ-11 · test-quality

**DescribeEndpoint can print a fragment of a password as the server name, contradicting its own allow-list guarantee**

- file: `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs`:117 · effort S · regressione di questo diff: **True** · verdetto **CONFIRMED**

**Evidenza**

`foreach (string token in endpointSummary.Split(';', …)) { int eq = token.IndexOf('='); … if (server is null && IsServerKey(key)) { server = value; } … }` under a doc that claims "an unrecognised token (a keyword alias we never enumerated, a fragment of a value containing `;`) is dropped instead of leaked". A fragment is dropped only if its key is NOT in the allow-list; a fragment that happens to look like `Server=…` is printed. Build_header_never_leaks_a_password_from_a_connection_string uses `Password=Dev!Secret` and `PWD=Pr0d!Secret` — neither contains a semicolon.

**Scenario**

Connection string `Server=DEV01;Database=App;Password="a;Server=hunter2"`. Split yields `Password="a`, `Server=hunter2"`, so `server` is set from the first Server token (DEV01) — but reorder the keys (`Password="a;Server=hunter2";Database=App;Server=DEV01`, legal ordering) and the header reads `-- Source    : hunter2 / App`, i.e. a slice of the password written into a .sql file that gets mailed and committed.

**Fix**

Parse with `SqlConnectionStringBuilder` and read `DataSource` / `InitialCatalog`, falling back to the current split only when the builder throws — the two callers already construct one to validate. Add a test with a semicolon-bearing quoted password.

**Verifica adversariale**

The doc claim at DeployScriptBuilder.cs:101-109 ('a fragment of a value containing `;` is dropped instead of leaked') is falsified by the loop at :117-126: a fragment is dropped only if its key misses the allow-list, and `Server=hunter2"` matches IsServerKey. Reachability checked properly, and it is real but narrow. The setup dialog is immune: ProjectSetupViewModel.BuildConnectionString (:309-319) always emits `Server=` first and `Password=` last, so the real server wins the `server is null` race. The exposed path is the raw connection-string TextBox — ConnectionPickerView.axaml:35 and :88 bind `Text` TwoWay to ConnectionPickerSlot.ConnectionString, which assigns straight into AppState.Source/TargetConnectionString (ConnectionPickerSlot.cs:19-20) — plus ConnectionStoreViewModel.MaterialiseAsync (:90-103), which does a raw `{PASSWORD}` string replace into a user-authored template whose key order is arbitrary. Paste `Password="a;Server=hunter2";Database=App;Server=DEV01` there and the header reads `-- Source    : hunter2 / App`. Also confirmed the existing test cannot catch it: `Build_header_never_leaks_a_password_from_a_connection_string` uses `Dev!Secret` / `Pr0d!Secret`, neither containing a semicolon. low is right.

---

# Affermazioni dei commit verificate

- **FALSE** (emission-ordering) — ec6eb83 "fix(core): drop every foreign key before dropping or altering anything", restated in the class doc as "Foreign-key DROP pass — EVERY foreign-key drop, before any object is touched ... both DROP TABLE and ALTER COLUMN are blocked by one"
  - Three FK classes are never dropped. (1) FKs constraining or referencing a column the ALTER retypes/drops — no branch keyed on ColumnsDroppedOrAltered exists, and EmitAlter skips ForeignKey outright (EO-2). (2) FKs held by a table that is itself being dropped — skipped by design at line 163, while the DROP TABLE order among tables is FK-blind (EO-7). (3) FKs between two rebuild targets — excluded by the `!rebuildTargets.Contains(holder)` guard at line 169 (EO-7). "Before any object is touched" is also loose: the batch is emitted after CREATE SCHEMA, CREATE/DROP USER and ROLE.

- **FALSE** (emission-ordering) — 95313c4 "fix(core): identity rebuild no longer silently destroys indexes, triggers and FKs"
  - True only when the caller passes a full ComparisonResult (the CLI does: ScriptCommand.cs:80). The GUI passes DeployScriptBuilder's synthetic result built from selectedPairs, and Identical rows are unselectable by construction (DifferenceRowViewModel.IsSelectable => !IsIdentical), so the `result.Differences.Where(... Identical)` trigger-restore pass can never fire on the path that actually executes against the target. The indexes and outbound FKs of the rebuilt table itself ARE restored (they read pair.SideA of a selected pair), so the claim is two-thirds true and wrong on the one part whose failure is silent.

- **FALSE** (emission-ordering) — Class doc: "DROP pass — removed objects (OnlyInB) in reverse-topological order (dependent-first) so a referenced object is never dropped before its dependents"
  - The edge list is always the SOURCE database's (ScriptCommand.cs:85 srcResult.Value!.Dependencies; MainWindowViewModel.cs:607/641 AppState.SourceDependencies). An object that exists only in the target appears in no edge, so every OnlyInB node has in-degree 0 and the order is the reversed KindRank — which drops Function(5) before View(4) and before Table(3). The pass is therefore never actually dependency-ordered for the only status it handles (EO-3).

- **TRUE** (emission-ordering) — 275660a "fix(core): clear index / key / CHECK dependencies before touching a column"
  - Literally accurate and correctly ordered: ColumnsDroppedOrAltered feeds both EmitAlter section 1 (PK/UQ/CHECK dropped and restored via droppedForColumnDependency) and the up-front blockingIndexDrops pass, with the index CREATE forced afterwards through forcedIndexRecreates. Verified against ColumnDependencyOrderingTests, whose assertions are real (each IndexOf is guarded with BeGreaterThan(0) before being compared). The commit does not claim FK coverage — that is the gap in EO-2, not a false claim.

- **FALSE** (emission-ordering) — In-code comment: "Constraint names are database-scoped, so the name alone dedupes" (ScriptGenerator.cs:134), repeated at TableScriptEmitter.cs:447
  - Foreign keys, PKs, UQs, CHECKs and named DEFAULTs are rows in sys.objects carrying the parent table's schema_id, so uniqueness is per schema, not per database. dbo.FK_X and archivio.FK_X coexist, which breaks fkDropNames dedup and the rebuildOrchestratedFkNames skip set (EO-5).

- **OVERSTATED** (emission-ordering) — Class doc order list (9 items) describes the emission order the code produces
  - The code has TEN passes: the early index-drop batch ("Dropping indexes on columns being altered", line 250) sits between the FK-drop pass and the DROP pass and is absent from the doc list entirely. Everything else in the list matches the code in sequence, including the new epilogue position of the schema drops. Worth fixing before the inverse-script work reads this doc as the spec.

- **TRUE** (emission-ordering) — 95549e3 "the app's deploy script is ordered by dependency edges again"
  - AppStateViewModel now stores srcRes.Value!.Dependencies and both MainWindowViewModel call sites (lines 604-608, 637-641) pass AppState.SourceDependencies into DeployScriptBuilder.Build, which forwards them to Generate. DeployScriptBuilderTests.Build_orders_by_dependency_edges_when_they_are_supplied genuinely discriminates: without the edge, KindRank puts View(4) before Function(5) and the viewPos > fnPos assertion fails.

- **TRUE** (emission-ordering) — 3dd4d09 CompareSchemas remark: "The reader already filters out the system schemas (sys, INFORMATION_SCHEMA, guest, db_*), so nothing here can emit a DROP SCHEMA [db_datareader]"
  - SchemaReader.cs filters `principal_id != 4`, `name NOT IN ('sys','INFORMATION_SCHEMA','guest')` and `name NOT LIKE 'db[_]%'`. LiveDb is the only provider that builds a Database, so no code path can feed unfiltered schemas in. dbo survives the filter but is present on both sides and so is never emitted.

- **OVERSTATED** (silent-loss) — ec6eb83 — "drop every foreign key before dropping or altering anything"; and the XML doc at ScriptGenerator.cs:16-21 / :234-236: "EVERY foreign-key drop … both DROP TABLE and ALTER COLUMN are blocked by one" / "an ALTER COLUMN cannot run while an FK constrains the column".
  - The pass has exactly three sources (:141-155 vanished/reshaped target-side FKs; :167 pointsAtDropped; :168-169 pointsAtRebuilt). None of them is "this FK constrains a column being retyped" or "this FK references a PK that EmitAlter is about to drop". `DependsOnColumn` (TableScriptEmitter.cs:375-384) returns false for ForeignKey and section 1 `continue`s on FKs. So the ALTER-COLUMN half of the stated rationale is unimplemented — see SL-3. The DROP-TABLE half is implemented, but only when the generator is handed the full comparison, which the app never does (SL-1).

- **OVERSTATED** (silent-loss) — 275660a — "clear index / key / CHECK dependencies before touching a column".
  - Keys and CHECKs are cleared and restored correctly within one table's EmitAlter (drop at :231-238, restore via `droppedForColumnDependency` at :310-311), and indexes are cleared. But the index RESTORE is keyed by bare index name across all tables, which both duplicate-creates and drop-suppresses indexes on unrelated tables (SL-2); the newly-emitted PK drop has no inbound-FK clearing so it fails with Msg 3725 (SL-3); and the trigger for the whole mechanism fires on DEFAULT-value-only changes, turning a metadata edit into a clustered-PK and index rebuild (SL-4).

- **FALSE** (silent-loss) — 95313c4 — "identity rebuild no longer silently destroys indexes, triggers and FKs".
  - Indexes and the rebuilt table's own outbound FKs are genuinely fixed: those branches (:306-319, :404-419) read the table pair itself, which is always present. Triggers are NOT: the restore reads `result.Differences` for `Identical` Trigger pairs (:350-351), and the app's `DeployScriptBuilder` hands the generator `new ComparisonResult(selectedPairs)` while `DifferenceRowViewModel.IsSelectable => !IsIdentical` makes an Identical pair impossible to select. The trigger restore therefore cannot fire from the application under any user action (SL-1). A state-only Different trigger is not covered either (SL-5).

- **OVERSTATED** (silent-loss) — ForeignKeyDropOrderingTests.Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table and TableRebuildPkSwapTests.Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled prove the Identical-object orchestration works.
  - Both tests call `ScriptGenerator.Generate` directly with a hand-built `ComparisonResult` that contains the Identical pair. That input shape is produced by the CLI `script` command only. The GUI — both Deploy (save .sql) and Esegui — routes through `DeployScriptBuilder.Build`, which constructs a result from the ticked rows alone and can never contain an Identical pair. The tests pass while the shipping path still hits Msg 3726 / still loses the trigger. No test in the diff exercises `DeployScriptBuilder` with any Identical-object orchestration.

- **FALSE** (silent-loss) — ScriptGenerator.cs:134 — "Constraint names are database-scoped, so the name alone dedupes"; TableScriptEmitter.cs:447-449 — "SQL Server constraint names are DB-scoped".
  - Constraints are entries in sys.objects and are unique per (schema_id, name), i.e. schema-scoped. `dbo.FK_Righe_Testa` and `sales.FK_Righe_Testa` legally coexist, and the name-only dedupe in `AddFkDrop` then discards the second, required drop (SL-6). In EmitRebuild the false premise is harmless (dropping the old table's own constraints before creating `_tmp` in the same schema is correct regardless), but the dedupe is not.

- **TRUE** (silent-loss) — 3dd4d09 / ScriptGenerator.cs:38-39, :453-455 — "Schema DROP pass — after every object pass, so a removed schema is already empty".
  - `EmitSchemaDrops` is called at :455, after the table DROP pass, the CREATE/ALTER pass, the index pass, the trigger pass and both FK passes, and before permissions. For a whole-database compare every object in a target-only schema is necessarily OnlyInB and is dropped earlier, so the schema is empty by then. (It can still fail on a partial selection where the user ticks the schema row but not the objects inside it, or when the schema holds an object kind DbDelta does not read — both fail loudly with Msg 3729, not silently.)

- **TRUE** (silent-loss) — b7bbdcb / DeployScriptBuilder.cs:27-32 — the app now passes dependency edges, so the topological sort no longer degenerates to kind-then-alphabetical.
  - `AppStateViewModel` captures `srcRes.Value!.Dependencies` into `SourceDependencies` and both `MainWindowViewModel` call sites (:608, :641) pass it as the new fifth argument, which flows to `Generate(…, dependencies:)`. `DependencyResolver.KindRank` confirms the fallback order the doc describes (View=4 before Function=5), so the stated failure mode was real and the edge list does fix it.

- **OVERSTATED** (test-quality) — ec6eb83 / ScriptGenerator class doc: "Foreign-key DROP pass — EVERY foreign-key drop, before any object is touched … both DROP TABLE and ALTER COLUMN are blocked by one"
  - Ordering is right and the DROP TABLE half is genuinely fixed (verified against ForeignKeyDropOrderingTests and the fkDrops collection). But "EVERY" is false: an FK with unchanged shape whose referenced table is neither dropped nor rebuilt is never collected, so the ALTER COLUMN half the doc explicitly cites is not covered (TQ-03). DependsOnColumn also excludes ForeignKey with a comment implying it is handled elsewhere; it is not handled anywhere.

- **OVERSTATED** (test-quality) — 95313c4: "identity rebuild no longer silently destroys indexes, triggers and FKs"
  - True when ScriptGenerator.Generate is handed a full ComparisonResult (CLI `script` does: ScriptCommand.cs:81-85). The indexes and outbound-FK halves read pair.SideA and work everywhere. The trigger half reads `result.Differences … Status == Identical`, and the app's DeployScriptBuilder passes `new ComparisonResult(selectedPairs)` — which never contains Identical pairs — so in the Avalonia app a rebuilt table's unchanged triggers are still silently destroyed (TQ-01).

- **OVERSTATED** (test-quality) — ed051a3 / ApplyCommand doc: "without one a failure at batch 3 of 5 left the database half-migrated, which was the only genuine half-migration hole in the product"
  - The hole is real and the fix works for a plain script (A_failing_batch_rolls_back_the_earlier_batches_of_a_plain_script genuinely fails on the old `useOwnTransaction: false`). "Only" is wrong: the same hole reopens whenever ScriptManagesItsOwnTransaction false-positives on a block-commented BEGIN TRANSACTION (TQ-12), and a DbDelta script generated with ComparisonOptions.NoTransactions now gets a client transaction that the script's own `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION` verdict (DeploymentScriptWriter.cs:56) will roll back behind the client's back, making tx.CommitAsync throw and the result indeterminate.

- **FALSE** (test-quality) — ed051a3 / SqlBatchResult doc: RolledBack is "True when a rollback was issued and acknowledged, so the target is known to be unchanged … false when the failure left the outcome indeterminate"
  - TryRollbackAsync's no-transaction branch returns true after `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;` succeeds as a no-op, i.e. whenever the connection is merely open. With --no-transaction the earlier batches are committed and the target is demonstrably changed, yet rolledBack:true is printed (TQ-06). The new acceptance test in the same commit creates exactly this state and does not check the field.

- **TRUE** (test-quality) — ApplyCommandTests rename comment: the old test name "Applies_script_inside_a_transaction_and_..." "claimed" atomicity it never checked
  - Confirmed against the test body: one batch, no induced failure, only `ObjectExistsAsync(tgtDb, "dbo.AppliedByCli")`. The rename is honest and the replacement test (three batches, batch 2 fails on a missing FK target, asserts FirstBatch and ThirdBatch are absent) does fail on `useOwnTransaction: false`.

- **TRUE** (test-quality) — 622e453 / TableAlterDeltaTests: a table-level "CONSTRAINT [x] DEFAULT (e) FOR [c]" inside CREATE TABLE is Msg 102, so the golden had to be re-baselined
  - DEFAULT is absent from the T-SQL table_constraint grammar — it appears only in column_definition and in ALTER TABLE ADD CONSTRAINT. The old .verified.txt was invalid T-SQL. The new baseline (`[CreatedAt] [datetime2] (7) NOT NULL CONSTRAINT [DF_Person_CreatedAt] DEFAULT (sysutcdatetime()),` with the CHECK still table-level, PK still a trailing ALTER) is valid: column_definition allows [COLLATE] [NULL|NOT NULL] [CONSTRAINT name] DEFAULT expr in that order, and nothing the old file expressed was lost — same constraint name, same expression.

- **TRUE** (test-quality) — 1a96d9f / M6KindsTests: "this test used to assert exactly that broken form — contradicting its own name" (GRANT … ON DATABASE)
  - The diff shows the old expectation was `"GRANT CONNECT ON DATABASE TO [app];"` under the name Permission_database_level_grant_omits_target_object. `ON DATABASE` is not valid T-SQL (only `ON DATABASE::[name]`), and the new expectation `GRANT CONNECT TO [app];` is correct for a database-scoped grant executing in the target's context. The added EmitRevoke test closes the sibling path. The regression risk is confined to the `_ => null` fallback arm (TQ-10).

- **TRUE** (test-quality) — 95549e3 / DeployScriptBuilderTests: "Without edges the topological sort degenerates to kind-then-alphabetical order: View(rank 4) before Function(rank 5)"
  - DependencyResolver.KindRank confirms View=4, Function=5, and Build_orders_by_dependency_edges_when_they_are_supplied does discriminate: with no edges the view emits first, and the first occurrence of "fnIva" is then inside the view body (after "vFattureIva"), so `viewPos > fnPos` fails. The test proves the parameter is threaded through Build. It does NOT prove the app populates or passes AppState.SourceDependencies — that part of the fix has no test at all.

- **TRUE** (test-quality) — b7bbdcb / AppStateViewModel: the old sanitiser's regex "stops at the first `;`, so a quoted password containing one … left its tail in plain sight"
  - `(?i)(password|pwd)\s*=\s*[^;]+` with `[^;]+` does stop at the first semicolon, so `Password="ab;cd"` leaves `cd"` echoed, and no other secret-bearing keyword was covered. The replacement DescribeParseFailure emits only key names, a length and a control-character position — no values. It is untested, and the analogous allow-list in DeployScriptBuilder.DescribeEndpoint has the residual leak in TQ-11.

- **TRUE** (executor-transactions) — "`dbdelta apply` had no transaction at all. It passed useOwnTransaction:false unconditionally... The XML doc claimed the transaction existed all along."
  - 19131d8:src/DbDelta.Cli/Commands/ApplyCommand.cs line: `SqlExecutor.ExecuteAsync(tgtConn, script, ct, useOwnTransaction: false)`, and the old class doc said "execute it against the target server inside a single GO-split transaction". Both exactly as described.

- **OVERSTATED** (executor-transactions) — "The mode is now chosen per script via SqlExecutor.ScriptManagesItsOwnTransaction, with --no-transaction to opt out."
  - The plumbing is there, but the chooser is `^\s*BEGIN\s+TRAN(SACTION)?\b` with Multiline over raw script text. It matches an indented BEGIN TRANSACTION inside a CREATE PROCEDURE body, inside a /* */ block, and inside a multi-line string literal — all false positives that disable the very transaction this commit added. It also misses BEGIN DISTRIBUTED TRANSACTION and any BEGIN TRANSACTION not first on its line. See EXEC-2.

- **FALSE** (executor-transactions) — "Rollback ... reports through a new SqlBatchResult.RolledBack. That flag is the point: an operator has to be able to tell 'nothing was applied' from 'we do not know'."
  - The flag cannot make that distinction and gets it wrong in both directions. `IF @@TRANCOUNT > 0 ROLLBACK` executing as a no-op returns true, so a half-migrated target (the author's own `No_transaction_opt_out_leaves_the_earlier_batches_applied` test) is reported as "known to be unchanged" (EXEC-1). Conversely a client transaction already rolled back by XACT_ABORT makes SqlTransaction.Rollback throw, so a provably clean target is reported as indeterminate (EXEC-7). And the flag reaches exactly one reader — ApplyCommand's JSON; the desktop dialog, the primary UI, never shows it (EXEC-5).

- **OVERSTATED** (executor-transactions) — "Rollback now runs with CancellationToken.None (a rollback must not be cancellable) ... on its own short timeout."
  - True inside the executor. But the CLI's only cancellation path is Ctrl+C via System.CommandLine, whose default ProcessTerminationTimeout is 2 seconds (confirmed in the beta5 package XML doc), after which the process is terminated. The 15 s RollbackTimeoutSeconds budget is unreachable on the cancellation path, and the result JSON is never printed. See EXEC-6.

- **TRUE** (executor-transactions) — "The 60 s per-batch timeout ... Now a parameter, 0 = unlimited."
  - `CommandTimeout = commandTimeoutSeconds` on both branches; SqlClient treats 0 as no limit. Nothing else in the path re-imposes a command timeout: ConnectTimeout=10 is login only, and an explicit CommandTimeout assignment overrides a `Command Timeout` connection-string keyword. The only competing deadline is the 2 s process-termination timeout, which affects the rollback, not the batch.

- **TRUE** (executor-transactions) — "Deliberately NOT done here: the desktop app keeps the 60 s default. It passes CancellationToken.None and has no Cancel button."
  - MainWindowViewModel.cs:650-654 calls ExecuteAsync with CancellationToken.None, no timeout argument, useOwnTransaction: false. ConfirmExecuteViewModel exposes only ExecuteCommand plus IsRunning/IsDone; there is no cancel path.

- **TRUE** (executor-transactions) — "the acceptance test asserting atomicity (3 batches, the middle one fails, batch 1 must be gone) fails on the old behaviour."
  - `A_failing_batch_rolls_back_the_earlier_batches_of_a_plain_script`: plain script, no BEGIN TRAN, so old code (useOwnTransaction: false unconditionally) would auto-commit dbo.FirstBatch and the `ObjectExistsAsync(...).Should().BeFalse()` assertion would fail. New code takes the client-transaction branch. The assertion is a real behavioural discriminator.

- **OVERSTATED** (executor-transactions) — "Plus unit coverage for the transaction-mode detection, including 'BEGIN' opening a block and a mention inside a comment."
  - The block-opening `BEGIN` case is genuinely covered. The comment case tests only `SELECT 1; -- BEGIN TRANSACTION would go here`, which passes for an incidental reason (`--` is not whitespace, so `^\s*BEGIN` cannot reach it). The comment above it generalises to "a mention inside a comment or a name must not count as an opener", which is false: a `/* ... BEGIN TRANSACTION ... */` block, or an indented BEGIN TRANSACTION inside a procedure body, both match. The sibling scenario reachable by the same path is untested and broken.

- **TRUE** (executor-transactions) — "The existing 'Applies_script_inside_a_transaction_and_target_picks_up_the_change' was renamed to what it actually checks — one batch, no failure, no atomicity whatsoever."
  - Honest self-assessment; the renamed test writes one CREATE TABLE and asserts it exists. Good catch by the author, and the replacement name matches the body.

- **FALSE** (executor-transactions) — The new unit test proves ExecuteAsync rejects a negative command timeout.
  - `act.Should().ThrowAsync<ArgumentOutOfRangeException>();` in a non-async void test method. The returned Task is discarded, the assertion never runs on the test thread, and no CS4014 is emitted because the method is not async. The test passes with `ArgumentOutOfRangeException.ThrowIfNegative` deleted. See EXEC-3.

- **OVERSTATED** (schema-kind-and-persistence) — 3dd4d09: "The reader already excludes the system schemas (sys, INFORMATION_SCHEMA, guest, db_*), so no DROP SCHEMA [db_datareader] is reachable."
  - The narrow statement is true — the ten fixed db_ role schemas plus sys/INFORMATION_SCHEMA/guest are all filtered, so `DROP SCHEMA [db_datareader]` specifically cannot be emitted. But it is used as the safety argument for DROP SCHEMA in general and does not hold there. The filter is a name blacklist of the 13 built-ins; it does not exclude feature-provisioned schemas whose contents are is_ms_shipped=1 (canonically `cdc`), and TableReader/ModuleReader filter only on is_ms_shipped=0, so those contents are never dropped while their container is (SCHEMA-01). The same clause also over-excludes any user schema literally named db_* while the object readers still return its objects, re-opening the Msg 2760 hole the commit exists to close (SCHEMA-04). dbo is deliberately NOT excluded and is only safe because it exists in both databases by construction — a one-sided Schemas list would emit DROP SCHEMA [dbo]; that is currently unreachable only because LiveDbSource is the sole Database factory and loads all-or-nothing.

- **TRUE** (schema-kind-and-persistence) — 3dd4d09: "Each CREATE gets its own batch because CREATE SCHEMA must be the first statement in a batch."
  - Verified against DeploymentScriptWriter.WriteBatch (lines 29-40): it writes `PRINT N'label';` then `GO`, then the body, then `GO`, then the error gate, then `GO`. The body is therefore alone in its own batch, and EmitSchemaCreates calls WriteBatch once per schema, so each CREATE SCHEMA is genuinely the first and only statement of its batch. The PRINT label lives in the preceding batch, not the CREATE's.

- **OVERSTATED** (schema-kind-and-persistence) — 3dd4d09: "Creates go in the prologue before any object can reference one; drops go in the epilogue after the objects a removed schema held are already gone."
  - The create half is correct: EmitSchemaCreates is the first WriteBatch after WritePreamble, ahead of EmitUsers, the FK-drop pass, and every topo pass. The drop half is only true for the nine kinds in topoKinds AND only when those object pairs are actually in `pairs`. Objects no reader models (CLR types/assemblies, XML schema collections, anything is_ms_shipped=1) are never dropped, and in the Avalonia path `pairs` is the user's tick-list, so ticking only the schema row emits a bare DROP SCHEMA against a populated schema. Two further contents-ordering problems survive: schema drops precede the permission REVOKEs that name those schemas (SCHEMA-02), and DROP USER for a user that owns a target-only schema is emitted in the PROLOGUE, before the epilogue drop that would make it legal.

- **TRUE** (schema-kind-and-persistence) — 3dd4d09: "Schemas present on both sides are deliberately not emitted ... a pair that matches by identity is equal by construction."
  - Correct given Schema(string Name) as the whole model — there is no second field that could differ, so a matched pair carries no information and DifferenceStatus.Different is genuinely unreachable, as SchemaScriptEmitter's Different/Identical arms acknowledge. Worth recording that "matches by identity" is ordinal/case-SENSITIVE (ObjectIdentity is a record struct, ax.ToDictionary(s => s.Identity) uses default equality), so schema `Sales` in source vs `sales` in target yields OnlyInA + OnlyInB for the same object: CREATE SCHEMA [Sales] fails on a case-insensitive target and the run rolls back. That is loud rather than silent, and matches how every other kind already pairs, so I did not raise it separately.

- **OVERSTATED** (schema-kind-and-persistence) — 3dd4d09: "6 tests, covering both orderings that made this a deploy failure."
  - Six tests exist and the two ordering tests are sound — Create_schema_is_emitted_before_the_objects_that_live_in_it and Drop_schema_is_emitted_after_the_objects_it_held both assert `IndexOf(...) > 0` on the anchor before comparing positions, so a missing statement (-1) fails rather than accidentally satisfying the comparison. Create_schema_is_alone_in_its_batch is also sound. But Schemas_present_on_both_sides_produce_no_row is vacuous: it passes unchanged with CompareSchemas deleted from the engine (TEST-01). And nothing in the six covers a non-empty target-only schema whose contents the tool does not model — the case that turns DROP SCHEMA into a deploy blocker.

- **TRUE** (schema-kind-and-persistence) — 3dd4d09: KindCatalog gains "Schema" at the FRONT, shifting every other SortOrder by one.
  - Verified harmless in Core. `grep KindCatalog\.|KnownKinds` over the whole repo shows the only production consumers are HtmlReportGenerator.cs:141 (OrderBy over kind groups), :146 (DisplayLabel) and :165 (StatusOrder). SortOrder is a relative ordering only, never persisted, never serialized, and not used by JsonFormatter, TextFormatter, Mapper, XmlProjectStore, or DependencyResolver (which has its own KindRank). KindCatalogTests was updated to the new indices. The real fallout is elsewhere: the Avalonia VM keeps a second, now-divergent copy of the same table (SCHEMA-03).

- **FALSE** (schema-kind-and-persistence) — 7fb6a8d: "That throw surfaces through the existing LastError channel."
  - Only one of the five mutating call sites is covered. AppStateViewModel.CompareAsync's broad `catch (Exception ex) { LastError = ex.Message; }` catches the AutosaveAsync→UpsertAsync path. The other four are unguarded: ConnectionManagerDialog.OnNewClick/OnEditClick/OnDeleteClick are `async void` handlers calling UpsertExplicitAsync/DeleteAsync with no try/catch and no guard inside ConnectionStoreViewModel; MainWindowViewModel.SaveProjectAsync:228 and LoadProjectFromPathAsync:308 call AddOrTouchAsync unguarded inside [RelayCommand] methods, and CommunityToolkit.Mvvm 8.4.2 rethrows an AsyncRelayCommand's exception through its internal `static async void AwaitAndThrowIfFailed` because FlowExceptionsToTaskScheduler is not set. There is no global handler (grep for UnhandledException/UnobservedTaskException over src returns nothing), which the commit message itself asserts. ProjectSetupDialog.OnSaveClick uses `_ = SaveAsAsync()`, so its copy is silently swallowed instead. See PERSIST-01.

- **OVERSTATED** (schema-kind-and-persistence) — 7fb6a8d: "a read-modify-write that silently treated an unreadable file as empty would overwrite every saved connection with the single entry being upserted" — i.e. forWrite:true guarantees a write never clobbers entries it merely failed to read.
  - The invariant holds only for IOException/UnauthorizedAccessException, the two types named in the new filter. The two older degrade branches are still forWrite-blind and still return an empty document on the write path: `catch (JsonException)` (JsonConnectionStore:116, JsonRecentProjectsStore:113) and the future-schema gate (JsonConnectionStore:109, JsonRecentProjectsStore:109). JsonConnectionStore at least calls MoveAside first, so the old content survives as a .broken-* file; JsonRecentProjectsStore does neither, so a v2 MRU file is overwritten with a one-entry v1 document and is unrecoverable (PERSIST-03).

- **OVERSTATED** (schema-kind-and-persistence) — 7fb6a8d: "Also brings the MRU store up to its sibling's hardening, which it had diverged from: 0600 UnixCreateMode on non-Windows ... and a future-schema-version gate on read."
  - The UnixCreateMode half is exactly right and now byte-identical to JsonConnectionStore.WriteAtomicAsync. The future-schema half is not parity: the sibling moves the file aside before returning empty, the MRU store just returns empty, so the two stores behave differently on the same input and only one preserves the data. The MRU store also still lacks the sibling's MoveAside on invalid JSON.

- **TRUE** (schema-kind-and-persistence) — 7fb6a8d: "the load path degrades to an empty list so startup survives" (JsonConnectionStore.LoadAsync).
  - Verified end to end. App.axaml.cs registers `desktop.MainWindow.Opened += async (_, _) => { await connections.LoadAsync(CancellationToken.None)... }` — an async void lambda with no try/catch and no global handler, so before the fix an IOException from File.ReadAllBytesAsync did kill the process after the window painted and before ProjectSetupDialog appeared, exactly as described. With forWrite:false that path now returns an empty Document and startup continues. The fix is real; its side effect is that the empty list then drives ConnectionStoreViewModel.AutosaveAsync's create-vs-touch decision and duplicates entries (PERSIST-02).

- **TRUE** (empirical-revert) — Target 1 — `Rebuild_recreates_every_index_including_the_identical_ones` fails without the rebuild index re-emission in ScriptGenerator.
  - Reverted: deleted the `case DifferenceStatus.Different when pair.SideA is Table tReb && rebuildTargets.Contains((tReb.Schema, tReb.Name))` arm from the index pass (ScriptGenerator.cs:301-319) so a rebuilt table falls through to `EmitIndexDelta`. Build clean. Result: FAILED — "Expected createIxIdx to be greater than 741 because the index must be re-created after the table is back, but found -1 (difference of -742)." Full Core suite: 311 pass / 1 fail, no unexpected coupling. Real regression test (assertion 3 is dead weight, see ER-4).

- **TRUE** (empirical-revert) — Target 2 — `Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled` fails without the "Re-creating triggers on rebuilt tables" pass.
  - Reverted: deleted the whole `if (rebuildTargets.Count > 0) { ... writer.WriteBatch("Re-creating triggers on rebuilt tables", ...) }` block (ScriptGenerator.cs:347-372). Build clean. Result: FAILED — "Expected trgIdx to be greater than 741, but found -1 (difference of -742)." Full Core suite: 311 pass / 1 fail. Real regression test. Minor weakness: it never asserts `renameIdx > 0`, so if sp_rename vanished the ordering assertion could be satisfied by a trigger at offset 0; the `Contain("DISABLE TRIGGER ...")` assertion covers for it.

- **TRUE** (empirical-revert) — Target 3 — `Index_on_an_altered_column_is_dropped_before_the_alter_and_recreated_after` fails without the blocking-index drop pass.
  - Reverted: deleted the `blockingIndexDrops` / `forcedIndexRecreates` computation (ScriptGenerator.cs:196-219), deleted the "Dropping indexes on columns being altered" batch, and changed the call back to `EmitIndexDelta(tSrc, tTgt)`. Build clean. Result: FAILED — "Expected dropIx to be greater than 0 because Msg 5074 blocks the ALTER while the index covers the column, but found -1 (difference of -1)." Two SIBLING tests failed with it, both intended coverage of the same commit: `Index_that_merely_includes_the_altered_column_is_also_dropped`, and `Index_on_a_dropped_column_is_dropped_first` ("Expected dropCol to be greater than 481, but found 368" — the DROP INDEX was still emitted by the plain delta, but AFTER the DROP COLUMN, which is the real Msg 5074 ordering bug). 309 pass / 3 fail. Note: `Primary_key_on_an_altered_column_is_dropped_and_restored` and `Check_constraint_referencing_an_altered_column_is_dropped_and_restored` kept passing because they call `TableScriptEmitter.Emit` directly and are covered by the *other* half of commit 275660a, which I did not revert here.

- **TRUE** (empirical-revert) — Target 4 — `Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table` fails with the FK drops restored to the late FK pass.
  - Reverted commit ec6eb83's source change by hand: removed the up-front `fkDrops` collection + `AddFkDrop` + the "Dropping foreign keys" batch, restored `inboundFkDrops` collection and its "Dropping inbound foreign keys for rebuilt tables" batch after the DROP pass, and restored the target-side DROP loop inside `EmitFkAdds`. Build clean. Result: FAILED — "Expected dropFk to be greater than 0 because an FK on an Identical table still blocks the DROP, so it must be found, but found -1." Sibling `Fk_from_a_different_table_is_dropped_before_the_table_it_references` also failed correctly ("Expected dropTable to be greater than 467 ..., but found 343"). 310 pass / 2 fail. BUT: the other two tests in the file passed with the fix fully reverted — see finding ER-2 for `A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end`.

- **TRUE** (empirical-revert) — Target 5 — `A_failing_batch_rolls_back_the_earlier_batches_of_a_plain_script` fails with `useOwnTransaction: false` unconditional in ApplyCommand.
  - Docker WAS available (Docker Desktop 4.79.0), so this ran for real against the Testcontainers mssql 2022 fixture. Baseline: 5/5 ApplyCommandTests pass. Reverted: `bool useOwnTransaction = false && !selfManaged && !noTx;` in ApplyCommand.cs:102, reproducing the pre-fix unconditional `useOwnTransaction: false`. Build clean. Result: FAILED — "Expected (ObjectExistsAsync(tgtDb, \"dbo.FirstBatch\", ct)) to be False because batch 1 must be rolled back when batch 2 fails, but found True." 4 pass / 1 fail; the opt-out test `No_transaction_opt_out_leaves_the_earlier_batches_applied` correctly kept passing (it asserts the reverted behaviour by design). Real regression test, and the exit code it pins (40, DeploymentFailure) is specific enough not to be satisfied by an accidental non-zero.

- **TRUE** (empirical-revert) — Target 6a — `EmitAlter_adds_not_null_column_with_its_default_inline` fails without the inline named-DEFAULT change.
  - Reverted: changed section 4 of `TableScriptEmitter.EmitAlter` to `inlineNamedDefault: false` and removed the `inlinedNamedDefaults.Add(newCol.Name)` bookkeeping, reproducing the pre-fix `FormatColumn(newCol, colsWithNamedDefault.Contains(newCol.Name))` semantics (default suppressed on the ADD, re-added standalone in section 5). Build clean. Result: FAILED — the emitted line was `ALTER TABLE [dbo].[X] ADD [Status] [int] NOT NULL;`, i.e. exactly the Msg 4901 shape the commit describes. 311 pass / 1 fail.

- **TRUE** (empirical-revert) — Target 6b — `Create_schema_is_emitted_before_the_objects_that_live_in_it` fails when CREATE SCHEMA is not in the prologue.
  - Reverted the ORDERING specifically rather than the whole feature (the stronger test of the assertion): removed `EmitSchemaCreates(writer, pairs)` from the prologue and moved it next to `EmitSchemaDrops` at the end of Generate. Build clean. Result: FAILED — "Expected createTable to be greater than 482 because the schema must exist before its objects, but found 342 (difference of -140)." 311 pass / 1 fail. The assertion genuinely pins position, not just presence.

- **TRUE** (empirical-revert) — Commit 622e453 — the rebuild temp table no longer carries an inline auto-named default that collides with the named re-add after sp_rename.
  - Bonus target. Reverted the pre-fix suppression semantics inside `FormatColumn` (`else if (namedDefault is null && !string.IsNullOrEmpty(c.DefaultExpression))` → `else if (!string.IsNullOrEmpty(c.DefaultExpression))`, which is what `hasNamedDefault == false` produced when `includeNamedConstraints` was false). Build clean. Result: `Rebuild_tmp_table_carries_no_inline_default_for_a_named_default` FAILED (311 pass / 1 fail); the 31 golden tests stayed green, so the goldens do NOT cover the rebuild temp-table shape — this unit test is the only thing pinning it.

- **TRUE** (empirical-revert) — Commit f4b8100 — triggers whose parent is a view are now read (INSTEAD OF triggers).
  - Bonus target, Docker-backed LiveDb integration. Reverted `INNER JOIN sys.objects AS po ON po.object_id = tr.parent_id AND po.type IN ('U','V')` back to `INNER JOIN sys.tables AS po ON po.object_id = tr.parent_id` in ModuleReader.cs. Build clean. Result: `LiveDbSource_loads_INSTEAD_OF_trigger_on_a_view` FAILED — "Expected Trigger trg = result.Value!.Triggers to contain a single item matching (t.Name == \"trgVCustomerInsert\"), but the collection is empty." 2 pass / 1 fail. Real regression test for a genuine false-negative class.

- **TRUE** (empirical-revert) — Commit 95549e3 / b7bbdcb / DeployScriptBuilder — the header no longer leaks passwords, and a selected Permission row is actually deployed.
  - Bonus targets, reverted together (distinguishable by test name). Made `DescribeEndpoint` a pass-through and `OptionsFor` always return `Default | DoNotOutputCommentHeader`. Build clean. Result: `Build_header_never_leaks_a_password_from_a_connection_string` FAILED and `Build_emits_permission_ddl_when_a_permission_row_is_selected` FAILED. Also reverted the rebuild outbound-FK re-add in the same run: `Rebuild_readds_its_own_outbound_fk_when_identical_on_both_sides` FAILED ("Expected fkIdx to be greater than 741 because DROP TABLE took the outbound FK with it, so it must be re-added after the rename, but found -1"). 309 pass / 3 fail. All three are real regression tests.

- **OVERSTATED** (empirical-revert) — Commit ec6eb83 — "4 tests, including the Identical-holder case and a check that a table being dropped gets no pointless DROP CONSTRAINT."
  - 4 tests exist, but only 2 of them can fail on the pre-fix behaviour. `A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end` passes with the fix fully reverted (finding ER-2). `A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk` also passes reverted — that one is defensible as a forward-looking guard on the new code's `droppedTables.Contains(holder)` skip (it would fail if that skip were deleted, which I did not test), but it contributes nothing to the claim that the commit's tests fail on the previous behaviour. Effective regression coverage of this commit is 2 tests, not 4.

---

# Esito del revert empirico

```json
{
  "lens": "empirical-revert",
  "findings": [
    {
      "id": "ER-1",
      "title": "ExecuteAsync_rejects_a_negative_command_timeout passes with the guard deleted — un-awaited ThrowAsync",
      "severity": "high",
      "file": "tests/DbDelta.Persistence.UnitTests/Sql/SqlExecutorTests.cs",
      "line": 166,
      "evidence": "        Func<Task> act = () => SqlExecutor.ExecuteAsync(\n            \"Server=localhost;Database=X;Connect Timeout=1\",\n            \"SELECT 1\",\n            CancellationToken.None,\n            useOwnTransaction: true,\n            commandTimeoutSeconds: -1);\n\n        act.Should().ThrowAsync<ArgumentOutOfRangeException>();\n\n// guard under test, src/DbDelta.Persistence/Sql/SqlExecutor.cs:\n        ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);",
      "failure_scenario": "EMPIRICALLY CONFIRMED. I deleted `ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);` from SqlExecutor.ExecuteAsync, rebuilt, and ran `dotnet test tests/DbDelta.Persistence.UnitTests --filter ExecuteAsync_rejects_a_negative_command_timeout` → \"Superato! Non superati: 0. Superati: 1\". `ThrowAsync` returns a `Task<ExceptionAssertions<T>>` that is discarded, so the assertion body never executes. Without the guard, `commandTimeoutSeconds: -1` reaches `new SqlCommand(...) { CommandTimeout = -1 }`, which throws `ArgumentException` (\"Invalid CommandTimeout value -1\") from deep inside the batch loop, is swallowed by the `catch (Exception ex)` in ExecuteAsync, and is reported to the operator as a *deployment failure with an error message* rather than a caller bug — i.e. the `apply --command-timeout -1` path silently degrades instead of failing fast. The test that is supposed to pin this cannot fail for any reason whatsoever.",
      "fix_sketch": "`await act.Should().ThrowAsync<ArgumentOutOfRangeException>();` and make the test `async Task`. Grep the rest of the suite for the same shape — any bare `.Should().ThrowAsync<` / `.Should().NotThrowAsync<` without `await` is dead.",
      "effort": "S",
      "introduced_by_these_commits": true
    },
    {
      "id": "ER-2",
      "title": "A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end passes with the entire up-front FK drop pass reverted",
      "severity": "high",
      "file": "tests/DbDelta.Core.UnitTests/ScriptGen/ForeignKeyDropOrderingTests.cs",
      "line": 127,
      "evidence": "        int dropFk = sql.IndexOf(\n            \"ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];\",\n            StringComparison.Ordinal);\n        int addFk = sql.IndexOf(\n            \"ADD CONSTRAINT [FK_Invoice_Currency] FOREIGN KEY\",\n            StringComparison.Ordinal);\n        dropFk.Should().BeGreaterThan(0);\n        addFk.Should().BeGreaterThan(dropFk);",
      "failure_scenario": "EMPIRICALLY CONFIRMED. I reverted commit ec6eb83's ScriptGenerator changes by hand (removed the up-front `fkDrops` collection and its \"Dropping foreign keys\" batch, restored `inboundFkDrops` + its batch after the DROP pass, and restored the DROP half inside `EmitFkAdds`), rebuilt, and ran the full Core suite: only 2 of the 4 ForeignKeyDropOrderingTests failed — `Fk_from_a_different_table_is_dropped_before_the_table_it_references` and `Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table`. `A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end` PASSED. It passes because the pre-fix `EmitFkDelta` emitted the DROP and the ADD into the *same late batch*, DROP first, so `addFk > dropFk` was already true. The test name and its comment (\"The drop and the add are now in different passes\") claim it pins the new pass structure; the assertion pins nothing beyond \"drop precedes add\", which no reachable code has ever violated. A future refactor that moves FK drops back to the end of the script — the exact Msg 3726 regression this commit fixed — would leave this test green.",
      "fix_sketch": "Assert the *position relative to another pass*, which is what \"up front\" means: e.g. add an object DROP or an ALTER COLUMN to the fixture and assert `dropFk < indexOf(\"ALTER TABLE ... ALTER COLUMN\")`, or assert the drop lands in the batch labelled \"Dropping foreign keys\" (`sql.IndexOf(\"Dropping foreign keys\") < dropFk < sql.IndexOf(\"Adding foreign keys\")`). Otherwise rename it to `A_reshaped_fk_is_dropped_before_it_is_re_added` so the name stops overstating.",
      "effort": "S",
      "introduced_by_these_commits": true
    },
    {
      "id": "ER-3",
      "title": "Commit 95549e3's headline fix (app call sites pass dependency edges) is completely untested — reverting it leaves 395 tests green",
      "severity": "high",
      "file": "src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs",
      "line": 608,
      "evidence": "// Both call sites, the actual subject of the commit:\n        string script = DeployScriptBuilder.Build(\n            selected,\n            AppState.SourceConnectionString ?? string.Empty,\n            AppState.TargetConnectionString ?? string.Empty,\n            DateTime.UtcNow,\n            AppState.SourceDependencies);\n\n// The only new test, which supplies the edges itself and never touches the app:\n// tests/.../DeployScriptBuilderTests.cs:184\n        string withEdges = DeployScriptBuilder.Build(\n            [viewPair, fnPair], \"src\", \"tgt\", DateTime.UtcNow, [edge]);",
      "failure_scenario": "EMPIRICALLY CONFIRMED. Commit 95549e3's message says \"Build now takes the edges and forwards them, and both app call sites pass AppState.SourceDependencies\" — the whole point being that only the GUI path was broken (\"Every cross-kind dependency the Kahn resolver was built for (#24) was silently inert on the GUI path; only the CLI ... was ordered correctly\"). I deleted the `AppState.SourceDependencies` argument from BOTH call sites (`SaveScriptAsync` line 608 and the execute path line 641), leaving them exactly as they were at 19131d8 (`dependencies` defaults to `null`, so `ScriptGenerator` falls back to `dependencies ??= []`). Result: DbDelta.Core.UnitTests 312/312 PASS, DbDelta.App.HeadlessTests 52/52 PASS, DbDelta.ScriptGen.GoldenTests 31/31 PASS. Nothing anywhere notices. The new test only proves `DeployScriptBuilder.Build` *honours* edges when handed them; it never proves the app hands them over. Concretely: a source with a new `dbo.vFattureIva` selecting from a new `dbo.fnIva` — the app's \"Genera script\" emits CREATE VIEW before CREATE FUNCTION and the deploy dies on Msg 208 — and the regression is reintroducible by deleting one argument with a fully green suite. This matters more than usual because the commit body names inverse-script generation as the next consumer of this ordering.",
      "fix_sketch": "Add a headless test on MainWindowViewModel: seed `AppState` via a fake `ISchemaSource` pair whose `Dependencies` contain a View→Function `ModuleReference` edge, invoke the script-generation command with both rows selected, and assert the function precedes the view in the produced text. Alternatively make the omission impossible: drop the `= null` default on `DeployScriptBuilder.Build`'s `dependencies` parameter so every call site must state its intent explicitly.",
      "effort": "M",
      "introduced_by_these_commits": true
    },
    {
      "id": "ER-4",
      "title": "Third assertion of Rebuild_recreates_every_index_including_the_identical_ones is a no-op, and its comment states the opposite of what the code does",
      "severity": "low",
      "file": "tests/DbDelta.Core.UnitTests/ScriptGen/TableRebuildPkSwapTests.cs",
      "line": 288,
      "evidence": "        // The index is identical on both sides, so the delta path would have\n        // emitted a DROP for it — the rebuild already destroyed it.\n        sql.Should().NotContain(\"DROP INDEX [IX_Invoice_Amount]\");\n\n// ScriptGenerator.EmitIndexDelta, the \"delta path\" the comment describes:\n        foreach (TableIndex t in tgt.Indexes)\n        {\n            if (alreadyDropped is not null && alreadyDropped.Contains(t.Name)) { continue; }\n            bool stillThere = srcByName.TryGetValue(t.Name, out TableIndex? s);\n            bool shapeChanged = stillThere && !IndexShapeEqual(t, s!);\n            if (!stillThere || shapeChanged)   // <- both false for an identical index",
      "failure_scenario": "The test's fixture uses the SAME `TableIndex ix` instance on both sides, so in the delta path `stillThere == true` and `shapeChanged == false`, and no DROP is ever emitted. I confirmed the rest of the test is real — reverting the rebuild index re-emission made assertion 2 fail (`Expected createIxIdx to be greater than 741 ..., but found -1`) — but assertion 3 would pass identically with the fix reverted, and the comment justifying it (\"the delta path would have emitted a DROP for it\") is factually wrong about DbDelta's own code. A reader trusting that comment will believe the delta path drops identical indexes, which is the opposite of the truth and is precisely why the re-emission pass was needed in the first place.",
      "fix_sketch": "Either delete the assertion and its comment, or make it mean something: give the two sides *differently shaped* same-named indexes so the delta path really would emit a DROP, then assert the rebuild path took over instead. Fix the comment to say \"the delta path emits nothing at all for an identical index, which is why the full source-side set has to be re-created here\".",
      "effort": "S",
      "introduced_by_these_commits": true
    }
  ],
  "verified_claims": [
    {
      "claim": "Target 1 — `Rebuild_recreates_every_index_including_the_identical_ones` fails without the rebuild index re-emission in ScriptGenerator.",
      "verdict": "TRUE",
      "reasoning": "Reverted: deleted the `case DifferenceStatus.Different when pair.SideA is Table tReb && rebuildTargets.Contains((tReb.Schema, tReb.Name))` arm from the index pass (ScriptGenerator.cs:301-319) so a rebuilt table falls through to `EmitIndexDelta`. Build clean. Result: FAILED — \"Expected createIxIdx to be greater than 741 because the index must be re-created after the table is back, but found -1 (difference of -742).\" Full Core suite: 311 pass / 1 fail, no unexpected coupling. Real regression test (assertion 3 is dead weight, see ER-4)."
    },
    {
      "claim": "Target 2 — `Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled` fails without the \"Re-creating triggers on rebuilt tables\" pass.",
      "verdict": "TRUE",
      "reasoning": "Reverted: deleted the whole `if (rebuildTargets.Count > 0) { ... writer.WriteBatch(\"Re-creating triggers on rebuilt tables\", ...) }` block (ScriptGenerator.cs:347-372). Build clean. Result: FAILED — \"Expected trgIdx to be greater than 741, but found -1 (difference of -742).\" Full Core suite: 311 pass / 1 fail. Real regression test. Minor weakness: it never asserts `renameIdx > 0`, so if sp_rename vanished the ordering assertion could be satisfied by a trigger at offset 0; the `Contain(\"DISABLE TRIGGER ...\")` assertion covers for it."
    },
    {
      "claim": "Target 3 — `Index_on_an_altered_column_is_dropped_before_the_alter_and_recreated_after` fails without the blocking-index drop pass.",
      "verdict": "TRUE",
      "reasoning": "Reverted: deleted the `blockingIndexDrops` / `forcedIndexRecreates` computation (ScriptGenerator.cs:196-219), deleted the \"Dropping indexes on columns being altered\" batch, and changed the call back to `EmitIndexDelta(tSrc, tTgt)`. Build clean. Result: FAILED — \"Expected dropIx to be greater than 0 because Msg 5074 blocks the ALTER while the index covers the column, but found -1 (difference of -1).\" Two SIBLING tests failed with it, both intended coverage of the same commit: `Index_that_merely_includes_the_altered_column_is_also_dropped`, and `Index_on_a_dropped_column_is_dropped_first` (\"Expected dropCol to be greater than 481, but found 368\" — the DROP INDEX was still emitted by the plain delta, but AFTER the DROP COLUMN, which is the real Msg 5074 ordering bug). 309 pass / 3 fail. Note: `Primary_key_on_an_altered_column_is_dropped_and_restored` and `Check_constraint_referencing_an_altered_column_is_dropped_and_restored` kept passing because they call `TableScriptEmitter.Emit` directly and are covered by the *other* half of commit 275660a, which I did not revert here."
    },
    {
      "claim": "Target 4 — `Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table` fails with the FK drops restored to the late FK pass.",
      "verdict": "TRUE",
      "reasoning": "Reverted commit ec6eb83's source change by hand: removed the up-front `fkDrops` collection + `AddFkDrop` + the \"Dropping foreign keys\" batch, restored `inboundFkDrops` collection and its \"Dropping inbound foreign keys for rebuilt tables\" batch after the DROP pass, and restored the target-side DROP loop inside `EmitFkAdds`. Build clean. Result: FAILED — \"Expected dropFk to be greater than 0 because an FK on an Identical table still blocks the DROP, so it must be found, but found -1.\" Sibling `Fk_from_a_different_table_is_dropped_before_the_table_it_references` also failed correctly (\"Expected dropTable to be greater than 467 ..., but found 343\"). 310 pass / 2 fail. BUT: the other two tests in the file passed with the fix fully reverted — see finding ER-2 for `A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end`."
    },
    {
      "claim": "Target 5 — `A_failing_batch_rolls_back_the_earlier_batches_of_a_plain_script` fails with `useOwnTransaction: false` unconditional in ApplyCommand.",
      "verdict": "TRUE",
      "reasoning": "Docker WAS available (Docker Desktop 4.79.0), so this ran for real against the Testcontainers mssql 2022 fixture. Baseline: 5/5 ApplyCommandTests pass. Reverted: `bool useOwnTransaction = false && !selfManaged && !noTx;` in ApplyCommand.cs:102, reproducing the pre-fix unconditional `useOwnTransaction: false`. Build clean. Result: FAILED — \"Expected (ObjectExistsAsync(tgtDb, \\\"dbo.FirstBatch\\\", ct)) to be False because batch 1 must be rolled back when batch 2 fails, but found True.\" 4 pass / 1 fail; the opt-out test `No_transaction_opt_out_leaves_the_earlier_batches_applied` correctly kept passing (it asserts the reverted behaviour by design). Real regression test, and the exit code it pins (40, DeploymentFailure) is specific enough not to be satisfied by an accidental non-zero."
    },
    {
      "claim": "Target 6a — `EmitAlter_adds_not_null_column_with_its_default_inline` fails without the inline named-DEFAULT change.",
      "verdict": "TRUE",
      "reasoning": "Reverted: changed section 4 of `TableScriptEmitter.EmitAlter` to `inlineNamedDefault: false` and removed the `inlinedNamedDefaults.Add(newCol.Name)` bookkeeping, reproducing the pre-fix `FormatColumn(newCol, colsWithNamedDefault.Contains(newCol.Name))` semantics (default suppressed on the ADD, re-added standalone in section 5). Build clean. Result: FAILED — the emitted line was `ALTER TABLE [dbo].[X] ADD [Status] [int] NOT NULL;`, i.e. exactly the Msg 4901 shape the commit describes. 311 pass / 1 fail."
    },
    {
      "claim": "Target 6b — `Create_schema_is_emitted_before_the_objects_that_live_in_it` fails when CREATE SCHEMA is not in the prologue.",
      "verdict": "TRUE",
      "reasoning": "Reverted the ORDERING specifically rather than the whole feature (the stronger test of the assertion): removed `EmitSchemaCreates(writer, pairs)` from the prologue and moved it next to `EmitSchemaDrops` at the end of Generate. Build clean. Result: FAILED — \"Expected createTable to be greater than 482 because the schema must exist before its objects, but found 342 (difference of -140).\" 311 pass / 1 fail. The assertion genuinely pins position, not just presence."
    },
    {
      "claim": "Commit 622e453 — the rebuild temp table no longer carries an inline auto-named default that collides with the named re-add after sp_rename.",
      "verdict": "TRUE",
      "reasoning": "Bonus target. Reverted the pre-fix suppression semantics inside `FormatColumn` (`else if (namedDefault is null && !string.IsNullOrEmpty(c.DefaultExpression))` → `else if (!string.IsNullOrEmpty(c.DefaultExpression))`, which is what `hasNamedDefault == false` produced when `includeNamedConstraints` was false). Build clean. Result: `Rebuild_tmp_table_carries_no_inline_default_for_a_named_default` FAILED (311 pass / 1 fail); the 31 golden tests stayed green, so the goldens do NOT cover the rebuild temp-table shape — this unit test is the only thing pinning it."
    },
    {
      "claim": "Commit f4b8100 — triggers whose parent is a view are now read (INSTEAD OF triggers).",
      "verdict": "TRUE",
      "reasoning": "Bonus target, Docker-backed LiveDb integration. Reverted `INNER JOIN sys.objects AS po ON po.object_id = tr.parent_id AND po.type IN ('U','V')` back to `INNER JOIN sys.tables AS po ON po.object_id = tr.parent_id` in ModuleReader.cs. Build clean. Result: `LiveDbSource_loads_INSTEAD_OF_trigger_on_a_view` FAILED — \"Expected Trigger trg = result.Value!.Triggers to contain a single item matching (t.Name == \\\"trgVCustomerInsert\\\"), but the collection is empty.\" 2 pass / 1 fail. Real regression test for a genuine false-negative class."
    },
    {
      "claim": "Commit 95549e3 / b7bbdcb / DeployScriptBuilder — the header no longer leaks passwords, and a selected Permission row is actually deployed.",
      "verdict": "TRUE",
      "reasoning": "Bonus targets, reverted together (distinguishable by test name). Made `DescribeEndpoint` a pass-through and `OptionsFor` always return `Default | DoNotOutputCommentHeader`. Build clean. Result: `Build_header_never_leaks_a_password_from_a_connection_string` FAILED and `Build_emits_permission_ddl_when_a_permission_row_is_selected` FAILED. Also reverted the rebuild outbound-FK re-add in the same run: `Rebuild_readds_its_own_outbound_fk_when_identical_on_both_sides` FAILED (\"Expected fkIdx to be greater than 741 because DROP TABLE took the outbound FK with it, so it must be re-added after the rename, but found -1\"). 309 pass / 3 fail. All three are real regression tests."
    },
    {
      "claim": "Commit ec6eb83 — \"4 tests, including the Identical-holder case and a check that a table being dropped gets no pointless DROP CONSTRAINT.\"",
      "verdict": "OVERSTATED",
      "reasoning": "4 tests exist, but only 2 of them can fail on the pre-fix behaviour. `A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end` passes with the fix fully reverted (finding ER-2). `A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk` also passes reverted — that one is defensible as a forward-looking guard on the new code's `droppedTables.Contains(holder)` skip (it would fail if that skip were deleted, which I did not test), but it contributes nothing to the claim that the commit's tests fail on the previous behaviour. Effective regression coverage of this commit is 2 tests, not 4."
    }
  ],
  "coverage_notes": "Environment: isolated worktree D:\\Develop\\AI\\_ClaudeCode\\SQL Compare\\.claude\\worktrees\\wf_5e18bc63-374-1. Restored to pristine at the end — `git status --porcelain` empty, `dotnet build DbDelta.sln -c Debug` 0 errors / 0 warnings, DbDelta.Core.UnitTests 312/312, DbDelta.Persistence.UnitTests 41/41.\n\nDocker WAS available (Docker Desktop 4.79.0, Testcontainers mssql 2022), so target 5 and the LiveDb trigger target both ran for real. Baselines established before any revert: Core 312, Persistence.Unit 41, Golden 31, Headless 52, Property 12, Cli.Acceptance ApplyCommandTests 5.\n\nTargets attempted: ALL SIX from the brief, plus five bonus reverts (rebuild outbound-FK re-add, rebuild tmp-table inline default, header redaction, permission-selection option, trigger parent join, app dependency-edge call sites). Each revert was a hand edit of source only; no test file was ever modified or disabled; the worktree was restored to pristine between targets and rebuilt clean each time.\n\nMethod notes. For target 6b I deliberately reverted the ORDERING (moved `EmitSchemaCreates` to the end of `Generate`) rather than deleting the feature, because deleting it would only have proved the presence assertion. For target 4 I hand-reconstructed the pre-fix structure from `git show ec6eb83` rather than using `git apply -R`, since commit 275660a later touched adjacent lines.\n\nCoupling observed — nothing unexpected. Every revert broke only tests that belong to the commit being reverted:\n- target 1 → 1 failure; target 2 → 1; target 3 → 3 (all in ColumnDependencyOrderingTests); target 4 → 2 (both in ForeignKeyDropOrderingTests); target 5 → 1 (the opt-out sibling correctly stayed green); target 6a → 1; target 6b → 1.\nNo golden test, property test, headless test or architecture test moved for any of them. That is itself informative: the 31 golden tests did not catch the rebuild temp-table inline-default regression, did not catch the FK-drop reordering, and did not catch the blocking-index regression. The unit tests added by these commits are the ONLY thing pinning the emission pipeline's order.\n\nTests I could NOT falsify by revert, and why:\n- `Build_orders_by_dependency_edges_when_they_are_supplied` — reverting the `dependencies` parameter breaks compilation of the test itself, so it cannot be revert-tested. I instead reverted the half of the commit that is actually about the app (both `DeployScriptBuilder.Build` call sites in MainWindowViewModel), which is untested → finding ER-3.\n- `Source_only_schema_is_reported_as_a_difference`, `Target_only_schema_is_reported_as_a_difference`, `Schemas_present_on_both_sides_produce_no_row` — reverting the ComparisonEngine schema pass makes all three fail trivially; not worth a run.\n- `Index_on_an_untouched_column_is_left_alone`, `Build_still_ignores_permissions_when_none_is_selected`, `Build_header_passes_through_a_plain_label_unchanged`, `A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk`, `ScriptManagesItsOwnTransaction_is_false_for_a_plain_script` — all negative/guard tests that pass both before and after by construction. Legitimate as over-fitting guards, but they inflate the per-commit test counts in the commit messages.\n- `TriggerReaderTests` other two tests, `ApplyCommandTests` other four — verified green at baseline only.\n\nNot covered by this lens (flagging for other reviewers): I did not audit whether `ScriptManagesItsOwnTransaction`'s `^\\s*BEGIN\\s+TRAN(SACTION)?\\b` multiline regex can false-POSITIVE on a script containing `BEGIN TRANSACTION` at the start of a line inside a `/* ... */` block comment — the negative-case Theory only covers a `--` line comment where the keyword is not at line start. A false positive there means `apply` skips the client transaction and the half-migration hole that target 5 closes reopens silently. Empirically untestable without a live server + a crafted script, so I leave it as an observation rather than a finding."
}
```

---

# Confutati (non lavorarci)

- **EXEC-5** (executor-transactions) — RolledBack never reaches the desktop user, and the dialog's own doc asserts the guarantee the flag exists to stop asserting
  - Two things kill this. (1) It is not in the subject diff and not a regression: `git diff --name-only 19131d8..HEAD -- src/` lists only AppStateViewModel.cs, MainWindowViewModel.cs and MainWindow.axaml under the App; ConfirmExecuteViewModel.cs is untouched, its doc comment at lines 92-97 is pre-existing, and the desktop behaviour is byte-for-byte what it was before. Not consuming a new optional record field (default false) changes no output — there is no failure scenario, only an unbuilt feature. (2) The substantive sub-claim — that the dialog's comment asserts an unverified guarantee — is now LESS true than before this diff, not more. The app builds its script through DeployScriptBuilder -> ScriptGenerator with ComparisonOptions.Default (NoTransactions is not in Default), so DeploymentScriptWriter emits SET XACT_ABORT ON + BEGIN TRANSACTION. Note that the executor aborts at the first failing batch and therefore never sends the script's own verdict/ROLLBACK footer — so before this diff the untouched-target claim rested entirely on XACT_ABORT, which does NOT fire for a compile-level error in a later batch (nothing executes, the transaction from earlier batches stays open, and dispose/sp_reset_connection was the only thing undoing it). The new TryRollbackAsync @@TRANCOUNT probe on the same connection is exactly what closes that hole. So this diff strengthened the very comment the finding attacks. Severity medium -> low, and it is a feature request for the undo work, not a defect.

- **EXEC-7** (executor-transactions) — RolledBack is false for the most careful hand-written script — SET XACT_ABORT ON plus a client transaction makes SqlTransaction.Rollback throw on an already-completed transaction
  - The premise is the opposite of what SqlClient does, and I verified it in the shipped binary rather than from memory. Microsoft.Data.SqlClient 6.0.1 (runtimes/win/lib/net9.0) SqlTransaction exposes a non-public property `Is2005PartialZombie` (the renamed IsYukonPartialZombie) alongside `IsZombied` and `ZombieCheck()`. Dumping the IL: `Rollback()` contains calls to BOTH get_Is2005PartialZombie and ZombieCheck (the two-branch shape: server already completed the transaction -> snip the internal transaction and return, no exception), whereas `Commit()` calls ZombieCheck only and has no partial-zombie call at all. SqlTransaction also does not override RollbackAsync, so DbTransaction.RollbackAsync just invokes Rollback(). Therefore in the described scenario — SET XACT_ABORT ON, client-owned transaction, server rolls back and sends the transaction-ended token — tx.RollbackAsync does NOT throw, TryRollbackAsync returns true, and RolledBack is correctly true for a provably clean target. The 'This SqlTransaction has completed' exception is the _internalTransaction == null case (client already committed/rolled back/disposed, or the connection died), which is precisely the 'we do not know' case the remarks at SqlExecutor.cs:162-173 document. Even if the analysis had been right, a conservative false on a status flag is an under-claim, not damage: low at most.

- **EXEC-8** (executor-transactions) — apply now wraps every non-self-managing script in a transaction, breaking scripts that were working, and ignores a script generated with ComparisonOptions.NoTransactions
  - Both halves fail. (a) The changed default is the deliberate, documented point of the commit, with an escape hatch shipped in the same diff: the ApplyCommand remarks (lines 13-24) state the reasoning, and the --no-transaction option's own Description names the exact scenario the finding raises ('Only needed for a script that cannot run inside one (e.g. it contains CREATE DATABASE or a backup)'). Note also that this creates no new exposure for DbDelta's own output: a generated script already wraps everything in BEGIN TRANSACTION (DeploymentScriptWriter.WritePreamble), so a CREATE DATABASE/ONLINE index build in a generated script failed before this diff too. (b) Unreachable. Grep for ComparisonOptions.NoTransactions across src and tests returns exactly two hits — the enum declaration and ScriptGenerator.cs:221 — no CLI option, no UI toggle, no test, and it is not part of ComparisonOptions.Default. Nothing in the shipped product can generate a NoTransactions script, so 'the generator's explicit intent is discarded' has no caller. The 'worse in (b)' mechanism is also wrong twice over: the executor's loop throws on the first failing batch and returns, so the script's trailing `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION` batch is never sent; and even in the contrived case where it did run, the subsequent tx.CommitAsync would throw (Commit has no partial-zombie exemption — verified in IL) and TryRollbackAsync's tx.RollbackAsync would then return TRUE via the partial-zombie path, not the claimed rolledBack:false.

- **SCHEMA-05** (schema-kind-and-persistence) — SchemaScriptEmitter.Emit(DifferencePair) is unreachable, and routing Schema through it later would silently move the DDL out of its fixed prologue/epilogue slots
  - The factual observation is accurate — grep for SchemaScriptEmitter across src and tests returns exactly ScriptGenerator.cs:675 and :689, both static calls, so the instance `Emit(DifferencePair)` has no caller. But this is not a finding: the report itself concedes 'No current failure — it is dead code', and the ground rules require a concrete failure scenario (specific inputs → specific wrong output or damage). There is none.

The 'trap for the next change' premise is also weaker than stated. `grep -rn ': IScriptEmitter' src` returns only SchemaScriptEmitter and TableScriptEmitter — the other nine emitters (View, Procedure, Function, Trigger, Sequence, Synonym, UDT, TableType, Permission) do not implement the interface at all, and DispatchBuild's arms call concrete emitter types, not the interface. So implementing IScriptEmitter does not make Schema 'look like an ordinary emitter kind'; nothing dispatches through that interface anywhere. The fixed-position requirement is documented twice in code the next editor cannot miss: the class remarks ('must give each emitted statement its own batch') and the ScriptGenerator ordering doc-comment at lines 12-14 and 38-39, plus the explicit topoKinds comment at line 180-186 stating why Schema is excluded. Unused code worth deleting, at most low.
