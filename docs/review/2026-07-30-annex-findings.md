# Annex — 96 finding verificati, testo integrale

Output grezzo della review del 2026-07-30. Ogni voce ha superato la confutazione adversariale.
Digest ordinato per impatto/effort: `2026-07-30-full-codebase-review.md`. ~20 voci sono duplicati
fra dimensioni (Appendice B del digest).

## [critical] deploy-script-header-leaks-plaintext-password  ·  app-ui-robustness

**Generated/executed alignment script embeds the full source+target connection strings, password in cleartext**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:598` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

MainWindowViewModel.DeployAsync: `string script = DeployScriptBuilder.Build(selected, AppState.SourceConnectionString ?? string.Empty, AppState.TargetConnectionString ?? string.Empty, DateTime.UtcNow);` then `await w.WriteAsync(script)` into the user-picked .sql file. DeployScriptBuilder.cs:36-37: `header.AppendLine($"-- Source    : {sourceEndpointSummary}"); header.AppendLine($"-- Target    : {targetEndpointSummary}");`. AppState.SourceConnectionString is the live string built by ProjectSetupViewModel.BuildConnectionString → `$"{baseCs};User Id={p.UserName};Password={p.Password}"`. Note the same method redacts for the *dialog* (line 641: `ConnectionStringRedactor.Redact(...)`) and MainWindow.axaml:455 redacts for the status bar — the .sql file is the one path with no redaction. DeployScriptBuilderTests only ever passes label-style summaries ("MYSERVER/MyDB"), so nothing pins this.

**Scenario di fallimento**

User compares PROD→TEST with SQL auth (sa / 'Pr0d!pass'), clicks "Genera script", saves DbDelta-20260730-1012.sql and mails it to a colleague / commits it to the repo. Line 4 of the file reads `-- Source    : Server=192.168.3.243;Database=ERP;Encrypt=False;TrustServerCertificate=True;User Id=sa;Password=Pr0d!pass`. The same header also travels to the server on the ExecuteOnTarget path (line 630), where the batch text is retained in the plan cache / Query Store / Extended-Events + audit traces — any principal with VIEW SERVER STATE on the target can read the source server's sa password out of sys.dm_exec_sql_text.

**Fix proposto**

Redact inside DeployScriptBuilder.Build (root cause, covers both callers): pass the two summaries through ConnectionStringRedactor.Redact, or better emit only `{DataSource}/{InitialCatalog}` parsed via SqlConnectionStringBuilder. Add a test asserting the built script contains no `Password=` other than `***`.

**Verifica adversariale**

Verified end-to-end. DeployScriptBuilder.cs:36-37 interpolates the two summary strings verbatim into `-- Source    : {…}` / `-- Target    : {…}`. Both call sites pass the LIVE connection strings unredacted: MainWindowViewModel.cs:598-602 (saved to the user-picked .sql via w.WriteAsync at :606) and MainWindowViewModel.cs:630-634 (the string handed to SqlExecutor at :643). Those strings carry the plaintext password: ProjectSetupViewModel.cs:316 `$"{baseCs};User Id={p.UserName};Password={p.Password}"`, captured into ProjectSetupDialog.LastSourceConnectionString (ProjectSetupDialog.axaml.cs:137-138) and assigned to AppState in App.axaml.cs and MainWindowViewModel.cs:541-542. The asymmetry the reviewer flags is real: the same method redacts for the dialog (MainWindowViewModel.cs:641-642) and MainWindow.axaml:456/462 redacts for the status bar; only the .sql file is raw. Also confirmed the header travels to the server: the `--` comment lines sit before the first `GO`, so SqlExecutor.SplitOnGo keeps them inside batch 1 (SqlExecutor.cs:124-156) → both passwords land in the target's plan cache / Query Store / XE text. No test pins it: DeployScriptBuilderTests.cs:37-47 only passes "MYSERVER/MyDB".

**Nota**: One correction to the proposed fix: ConnectionStringRedactor lives in DbDelta.Persistence.Util and Core must not depend on Persistence, so redacting *inside* DeployScriptBuilder needs a Core-local helper (or parse with SqlConnectionStringBuilder and emit only DataSource/InitialCatalog). Redacting at the two MainWindowViewModel call sites is the smaller diff and covers both paths.

---

## [critical] stale-comparison-stays-deployable-after-endpoint-change  ·  app-ui-robustness

**A failed re-compare leaves the previous comparison's rows selected and executable against the NEW target**

- file: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs:184` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

CompareAsync bails out on every failure path *before* touching the results: `if (!srcRes.IsSuccess) { LastError = srcRes.Error!.Message; return; }` (also 191-196, and `catch (Exception ex) { LastError = ex.Message; }`) — LastComparison / LastComparisonRaw keep their previous values. MainWindowViewModel only rebuilds on a LastComparison change: `if (e.PropertyName == nameof(AppStateViewModel.LastComparison)) { RebuildRows(); }`; the TargetConnectionString branch only does `ExecuteOnTargetCommand.NotifyCanExecuteChanged()`. EditProjectAsync (527-550) assigns the new Source/TargetConnectionString and CurrentProject, then awaits CompareCommand — with no invalidation if it fails. CanExecuteOnTarget() is just `Rows.Any(r => r.IsSelected) && !string.IsNullOrWhiteSpace(AppState.TargetConnectionString)`.

**Scenario di fallimento**

1) Compare DEV→STAGING; grid shows 40 diffs, user ticks 12 of them, including 5 "Solo destinazione" rows (which emit DROPs). 2) User clicks "Modifica", repoints Target at PROD (valid creds) and Source at a server that is down/renamed. 3) CompareAsync fails on the source load, writes LastError (which is displayed nowhere — see errors-invisible finding) and returns. The grid still shows the DEV→STAGING rows with the 12 ticks; the header strip and status bar now say PROD. 4) User clicks "Allinea destinazione" → the script is built from the stale DEV/STAGING DifferencePairs and executed against PROD, dropping the 5 objects that existed only in STAGING and creating/altering objects from a comparison PROD never took part in.

**Fix proposto**

Make the results a function of the endpoints they came from: in AppStateViewModel record the (src,tgt) pair used for the run, and clear LastComparison/LastComparisonRaw (or set a `ResultsAreStale` flag that gates CanDeploy/CanExecuteOnTarget and shows a banner) whenever SourceConnectionString/TargetConnectionString change or a compare fails. Cheapest correct version: in OnSourceConnectionStringChanged/OnTargetConnectionStringChanged set `LastComparison = null; LastComparisonRaw = null;` — RebuildRows already clears the grid on that.

**Verifica adversariale**

Every failure path in AppStateViewModel.CompareAsync returns before touching results (parse-fail :164-179, src load :185-189, tgt load :191-196, catch :214-217) — LastComparison/LastComparisonRaw keep the previous run's values. MainWindowViewModel.cs:45-55 only calls RebuildRows() on a LastComparison change; the TargetConnectionString branch just calls ExecuteOnTargetCommand.NotifyCanExecuteChanged(). EditProjectAsync (:541-549) assigns the new Source/TargetConnectionString + CurrentProject, then awaits CompareCommand with no invalidation on failure. CanExecuteOnTarget() (:658-660) is only `Rows.Any(IsSelected) && target non-empty`, and ExecuteOnTargetAsync builds the script from SelectedPairs() (:665-666) = the STALE DifferencePairs, then runs it against the NEW target. I also confirmed the path is reachable with an unreachable source: the OK button is gated on `IsValid` (ProjectSetupDialog.axaml:364) and DatabaseName is a free-text AutoCompleteBox/TextBox (:177-178, :191), so IsValid can be true with no successful connection — and after a project load the fields are prefilled from the .dbd. Compounded by lasterror-never-shown: the failure is invisible.

**Nota**: Selected 'Solo destinazione' rows do emit DROP TABLE (TableScriptEmitter.cs:170-171), so the scenario really is data loss on the new target.

---

## [critical] case-sensitive-object-pairing  ·  diff-engine-correctness

**Object and column pairing is case-SENSITIVE (ordinal) → a case-only name difference splits one object into OnlyInA+OnlyInB and the deploy DROPs the target table/column**

- file: `src/DbDelta.Core/ObjectModel/Table.cs` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

Table.cs:27  `public readonly record struct ObjectIdentity(string SchemaName, string ObjectName, string Kind);`  — record-struct equality uses EqualityComparer<string>.Default = ORDINAL. Every pairing dictionary keys on it: ComparisonEngine.cs:272 `var aByIdentity = a.Tables.ToDictionary(t => t.Identity);` / :273 same for b. Column pairing is equally ordinal: ComparisonEngine.cs:375 `var bByName = bx.ToDictionary(c => c.Name);` (no comparer). The emitter then acts on that pairing: TableScriptEmitter.cs:170 `EmitDrop(Table table) => $"DROP TABLE [{table.Schema}].[{table.Name}];"` and TableScriptEmitter.cs:219-226 `foreach (Column oldCol in oldT.Columns) { if (!newColsByName.ContainsKey(oldCol.Name)) { ... " DROP COLUMN [" ... } }`. Grep of tests/ for `OrdinalIgnoreCase|case[- ]?sensitiv` returns zero hits — no test covers this.

**Scenario di fallimento**

Server/DB collation is case-INSENSITIVE (the default). Target prod was created in 2014 by a script that typed `CREATE TABLE dbo.CLIENTI`, dev source was recreated later as `CREATE TABLE dbo.Clienti`. sys.tables.name preserves the typed case, so the two catalogs hold `CLIENTI` and `Clienti`; every query in both DBs works identically. Compare → identities `(dbo,Clienti,Table)` and `(dbo,CLIENTI,Table)` are unequal → the engine reports OnlyInA(Clienti) + OnlyInB(CLIENTI) instead of one Identical row. User selects all and clicks Esegui → script contains `DROP TABLE [dbo].[CLIENTI];` followed by `CREATE TABLE [dbo].[Clienti] (...)` → every row in the production table is destroyed and replaced by an empty table. Same mechanism at column level: prod column `descrizione` vs dev `Descrizione` → `ALTER TABLE ... DROP COLUMN [descrizione];` + `ADD [Descrizione] ...` → that column's data is silently lost while the deploy reports success.

**Fix proposto**

Make identifier comparison case-insensitive by default, in one place: give ObjectIdentity explicit `IEquatable` members using StringComparer.OrdinalIgnoreCase on all three components (plus a matching GetHashCode), and pass StringComparer.OrdinalIgnoreCase to the column/constraint/index name dictionaries in ComparisonEngine and TableScriptEmitter. Because a genuinely case-sensitive-collation DB may legally hold `Foo` and `foo`, replace `ToDictionary` with a GroupBy-based build that surfaces a duplicate-identifier warning instead of throwing ArgumentException. Ideally drive the comparer from the DB collation already read into Database.DefaultCollation.

**Verifica adversariale**

Verified. `ObjectIdentity` is a record struct of three strings (src/DbDelta.Core/ObjectModel/Table.cs:28 — reviewer cited :27, off by one) so equality/GetHashCode are ordinal. Every pairing dictionary keys on it with no comparer: ComparisonEngine.cs:272-273 (tables), :291-292 (modules), :560-561 (triggers). Column pairing is ordinal too (ComparisonEngine.cs:375 `bx.ToDictionary(c => c.Name)`), as is constraint (:451) and index (:507) pairing. The emitter then acts on that pairing and explicitly uses StringComparer.Ordinal: TableScriptEmitter.cs:195/197 (`existingColsByName`/`newColsByName`), :219-226 emits `ALTER TABLE … DROP COLUMN [oldCol]` for any target column not found by ordinal name, :253-259 re-ADDs the source-cased column, and :170 `EmitDrop => DROP TABLE …`. ScriptGenerator runs the DROP pass (line 146) before the CREATE pass (line 170), so a case-only table rename really does emit DROP TABLE [dbo].[CLIENTI] then CREATE TABLE [dbo].[Clienti] — the rows are destroyed and the script reports 'The database update succeeded' (DeploymentScriptWriter.cs:54). No guard exists anywhere: grep of tests for OrdinalIgnoreCase/case-sensitivity in the diff path returns only tests/DbDelta.Core.UnitTests/ObjectModel/TableTests.cs:10 `Identity_combines_schema_and_name_case_sensitively_by_default`, which LOCKS IN the ordinal semantics but says nothing about what a case-insensitive-collation database should do; no doc/BACKLOG entry declares case-only differences an accepted DROP+CREATE. Rows default to unselected (DifferenceRowViewModel.cs:32) so the user must tick the OnlyInB row — which is exactly what a user does when a grid says 'solo nella destinazione'. Note for the fix: a CI-collation DB cannot hold both `Foo` and `foo`, so ToDictionary cannot throw there; on a CS-collation DB the current ordinal behaviour is correct — the comparer must be collation-driven (Database.DefaultCollation exists, Database.cs:65).

**Nota**: Claim holds unchanged. Only correction: the ObjectIdentity declaration is Table.cs:28, and the existing test TableTests.cs:10 asserts today's ordinal behaviour, so the fix must rewrite that test.

---

## [critical] identity-rebuild-drops-indexes-and-triggers  ·  livedb-readers

**Identity rebuild does DROP TABLE and never recreates the table's indexes or triggers**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`EmitRebuild` (TableScriptEmitter.cs:312) emits `CREATE TABLE [X_tmp] (…)` (without named constraints), `INSERT INTO [X_tmp] … SELECT … FROM [X]`, then `sb.Append("DROP TABLE ").Append(qualifiedOld)` (line 371) and `EXEC sp_rename '[s].[X_tmp]', 'X'`; afterwards it re-adds only PK/UQ/CK/DEFAULT (`newT.Constraints.Where(IsNamedNonFkConstraint)`). Indexes are handled elsewhere and only as a delta: `case DifferenceStatus.Different … string indexDelta = EmitIndexDelta(tSrc, tTgt)` which emits nothing when both sides carry the same index set (ScriptGenerator.cs:199-206, EmitIndexDelta at :605). Triggers are separate DifferencePairs and an identical trigger yields `DifferenceStatus.Identical` → `pairs` excludes it (ScriptGenerator.cs:61-62), so no CREATE TRIGGER is emitted.

**Scenario di fallimento**

Target `dbo.Orders` has `Id int NOT NULL` (someone lost the identity in prod), source has `Id int IDENTITY(1,1)`; both have `IX_Orders_CustomerId`, `IX_Orders_OrderDate` and trigger `trg_Orders_Audit` (identical on both sides). `RequiresFullRebuild` returns true → the deploy copies rows into `Orders_tmp`, `DROP TABLE dbo.Orders` (which drops both indexes and the trigger), renames, re-adds only the PK. The script commits successfully. Production is left with no nonclustered indexes and no audit trigger, and a re-compare reports "Identical" so the loss is invisible.

**Fix proposto**

In `ScriptGenerator`, for tables in `rebuildTargets`, emit CREATE INDEX for **all** of `SideA.Indexes` (not the delta) after the rebuild batch, and force-emit CREATE TRIGGER for every trigger whose parent is a rebuilt table even when the pair is Identical. Cross-dimension: this also silently destroys anything the readers do not model (columnstore indexes, extended properties) — see the reader findings below.

**Verifica adversariale**

Reproduced exactly. `EmitRebuild` (TableScriptEmitter.cs:312-386): drops old named non-FK constraints (340-344), `EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false)` (348-350), INSERT…SELECT (358-364), `DROP TABLE [s].[X];` (371), `sp_rename` (372-373), then re-adds ONLY `newT.Constraints.Where(IsNamedNonFkConstraint)` = PK/UQ/CK/DEFAULT (377-384, 388-395). Indexes are never re-emitted for rebuild targets: I read the whole ScriptGenerator — `rebuildTargets` (69-78) is used ONLY for inbound-FK drop/re-add (79-111, 158-167, 265-273); the index pass (183-215) hits `case DifferenceStatus.Different` → `EmitIndexDelta(tSrc, tTgt)` (605-631), which emits nothing when both sides carry the same index set. Triggers: identical triggers get `DifferenceStatus.Identical` (ComparisonEngine.cs:574-592) and are filtered out at ScriptGenerator.cs:61-62, so no CREATE TRIGGER. No test covers it — tests/DbDelta.Core.UnitTests/ScriptGen/TableRebuildTests.cs and TableRebuildPkSwapTests.cs construct tables with `[], []` (empty constraints/indexes) and never assert index or trigger survival. ADDITIONAL LOSS the reviewer missed: the rebuilt table's OWN outbound foreign keys are destroyed too — `IsNamedNonFkConstraint` excludes ForeignKey (TableScriptEmitter.cs:388-395) so they are not re-added there, and `EmitFkDelta` (ScriptGenerator.cs:647-674) skips any FK where `existsOnTarget && !shapeChanged` (line 670), which is precisely the case for an unchanged FK on a rebuilt table.

**Nota**: Confirmed and worse than stated: nonclustered indexes, triggers AND the rebuilt table's own outbound FKs are all silently dropped by the DROP TABLE and never recreated. The fix must force-emit, for every table in `rebuildTargets`: all of SideA.Indexes, all of SideA's outbound FKs, and CREATE TRIGGER for every trigger whose ParentSchema/ParentTable matches — even when the pair is Identical.

---

## [critical] metadata-visibility-turns-into-drop-table  ·  livedb-readers

**No metadata-visibility preflight: a least-privilege connection makes objects invisible, and invisible source objects become DROP statements**

- file: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

Every reader filters only on `is_ms_shipped = 0` and nothing else: `FROM sys.tables AS t ... WHERE t.is_ms_shipped = 0` (TableReader.cs:18-21). There is no permission/visibility check anywhere in the provider or in `ConnectionTester.TestAsync` (which only runs `SELECT @@VERSION`, ConnectionTester.cs:28). SQL Server's metadata-visibility rule filters sys.* rows to securables the principal owns or has some permission on, so a partially-privileged login sees a partial catalog and `LoadAsync` returns `Result<Database>.Success` for it (LiveDbSource.cs:101). ScriptGenerator then drops everything only present on the target side: `foreach (ObjectIdentity id in createOrder.Reverse()) { if (pair.Status != DifferenceStatus.OnlyInB) continue; ... }` (ScriptGenerator.cs:145-152) → `EmitDrop(table) => $"DROP TABLE [{table.Schema}].[{table.Name}];"` (TableScriptEmitter.cs:171).

**Scenario di fallimento**

Source = DevDB read with a login that has SELECT on only 3 of its 50 tables (a DBA-issued read-only login, the setup the SICURO requirement encourages). Target = ProdDB read as db_owner. sys.tables returns 3 rows for the source, 50 for the target → 47 tables classify as OnlyInB → `dbdelta script`/`apply` emits `DROP TABLE` for 47 production tables and the deploy executes them inside the transaction, which commits. Total data loss, and the UI/report never hinted that the source read was incomplete.

**Fix proposto**

In `LiveDbSource.LoadAsync`, before reading, assert visibility and fail loudly: `SELECT CAST(HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION') AS bit), IS_MEMBER('db_owner'), IS_SRVROLEMEMBER('sysadmin')`; if none is true, return `Result<Database>.Failure(new Error(ErrorCode.AuthFailed, ...))` explaining that without VIEW DEFINITION the catalog is silently filtered and a comparison would be unsafe. Cheap belt-and-braces addition: also compare `COUNT(*) FROM sys.objects` against `SELECT COUNT(*) FROM sys.objects WHERE ...` is not possible, so the permission gate is the check.

**Verifica adversariale**

Verified end to end. `LiveDbSource.LoadAsync` (LiveDbSource.cs:25-123) does no permission/visibility check whatsoever — it opens the connection (line 29) and goes straight to the readers; the only catches are 4060/18456 → AuthFailed, 53/-2 → CannotConnect, else CatalogQueryFailed (lines 103-122). Every reader filters on `is_ms_shipped = 0` only (TableReader.cs:20, ReadTableObjectIdsAsync at LiveDbSource.cs:148). `ConnectionTester.TestAsync` only runs `SELECT @@VERSION` (src/DbDelta.Persistence/Sql/ConnectionTester.cs). SQL Server metadata-visibility does filter sys.* rows to securables the principal has some permission on, so a partial catalog returns `Result<Database>.Success` (line 101). The drop path is exactly as quoted: ScriptGenerator.cs:146-152 walks `createOrder.Reverse()` and emits for `OnlyInB`, dispatching to `TableScriptEmitter.EmitDrop` = `DROP TABLE [s].[t];` (TableScriptEmitter.cs:170-171). And the CLI closes the loop with no human gate: `ScriptCommand` calls `Generate(comparison, selection: null, …)` (ScriptCommand.cs:79-85), so `dbdelta script | dbdelta apply` emits and executes the DROPs. STRONG CORROBORATION the reviewer missed: `ErrorCode.InsufficientPermissions` exists in the enum (Core/Abstractions/Result.cs:11) and is mapped to CLI exit code 11 (CliErrorMapper.cs:23-24, ExitCodes.cs:11) but is **produced by no code path in src/** — the permission gate was designed and never implemented.

**Nota**: Confirmed as claimed. The unused ErrorCode.InsufficientPermissions + exit code 11 are the intended landing spot for the preflight. Note the GUI is slightly less silent (the grid would show N 'Solo in destinazione' rows), but the CLI script→apply path is fully silent.

---

## [critical] add-not-null-column-with-named-default-fails  ·  scriptgen-correctness

**ALTER TABLE ADD of a NOT NULL column omits its DEFAULT (added as a separate later statement) → fails on any populated table**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:255` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

EmitAlter step 4:

    foreach (Column newCol in newT.Columns) {
        if (existingColsByName.ContainsKey(newCol.Name)) { continue; }
        sb.Append("ALTER TABLE ").Append(qualifiedName).Append(" ADD ")
          .Append(FormatColumn(newCol, colsWithNamedDefault.Contains(newCol.Name))).AppendLine(";");
    }

and FormatColumn line 481: `if (!hasNamedDefault && !string.IsNullOrEmpty(c.DefaultExpression))` — with a named default the DEFAULT clause is dropped from the ADD. The constraint is only added afterwards in step 5 (line 265-276) as `ALTER TABLE … ADD CONSTRAINT [DF_x] DEFAULT … FOR [col];`. Since every live-read default is named (ConstraintReader.cs:275), the ADD is always emitted bare.

**Scenario di fallimento**

Source adds `Status int NOT NULL CONSTRAINT DF_Order_Status DEFAULT (0)` to dbo.Order; target's dbo.Order has 1.2M rows. Script emits `ALTER TABLE [dbo].[Order] ADD [Status] [int] NOT NULL;` → Msg 4901 "ALTER TABLE only allows columns to be added that can contain nulls, or have a DEFAULT definition specified" → whole deploy rolls back. The single most common real migration (add NOT NULL column with default) can never be deployed.

**Fix proposto**

Emit the default inline on the ADD, keeping the constraint name: `ALTER TABLE [dbo].[Order] ADD [Status] [int] NOT NULL CONSTRAINT [DF_Order_Status] DEFAULT ((0));` (optionally `WITH VALUES` for the nullable case), and suppress the corresponding step-5 ADD CONSTRAINT for that name.

**Verifica adversariale**

TableScriptEmitter.cs:253-260 matches the quote; colsWithNamedDefault is built at :191-192 from newT.Constraints.OfType<DefaultConstraint>(), and FormatColumn:481 gates the DEFAULT clause behind `!hasNamedDefault`, so a column that has a named default is emitted BARE. Step 5 (:265-276) then adds `ALTER TABLE … ADD CONSTRAINT [DF_x] DEFAULT … FOR [col];` as a *later* statement (FormatStandaloneConstraintBody:443). Since every live-read default is named (ConstraintReader.cs:265-281) the bare form is always what ships. `ALTER TABLE … ADD [c] [int] NOT NULL;` on a table with rows = Msg 4901, and DeploymentScriptWriter.cs:20 sets XACT_ABORT ON so the whole deploy rolls back. No test covers it: grep DEFAULT over tests/DbDelta.Core.UnitTests/ScriptGen/TableAlterDeltaTests.cs and TableScriptEmitterTests.cs returns nothing. Adding a NOT NULL column with a default is the single most common migration, so this is critical.

---

## [critical] create-table-named-default-is-invalid-tsql  ·  scriptgen-correctness

**CREATE TABLE emits a table-level `CONSTRAINT [x] DEFAULT … FOR [col]`, which is a T-SQL syntax error**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:66` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

TableScriptEmitter.EmitCreate suppresses the inline default (`colsWithNamedDefault`, line 33-34 + FormatColumn line 481) and instead emits it in the table-constraint list:

    case DefaultConstraint df:
        AppendLineSeparator(sb, ref firstLine);
        sb.Append("    CONSTRAINT [").Append(df.Name).Append("] DEFAULT ")
          .Append(df.Expression).Append(" FOR [").Append(df.ColumnName).Append(']');

The pinned golden file tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_check_and_default_constraints.verified.txt pins exactly this output:

    CREATE TABLE [dbo].[Person] (
        …
        CONSTRAINT [DF_Person_CreatedAt] DEFAULT (sysutcdatetime()) FOR [CreatedAt]
    );

SQL Server's `<table_constraint>` grammar only admits PRIMARY KEY / UNIQUE / FOREIGN KEY / CHECK. `DEFAULT … FOR col` is legal only in `ALTER TABLE … ADD CONSTRAINT`. It is always reached on live data: TableReader.cs:44 populates Column.DefaultExpression from sys.default_constraints AND ConstraintReader.cs:275 emits a DefaultConstraint for the same row, so every column with a default has both.

**Scenario di fallimento**

Source has dbo.Person(CreatedAt datetime2 NOT NULL CONSTRAINT DF_Person_CreatedAt DEFAULT (sysutcdatetime())); target does not have the table. Deploy emits `CREATE TABLE [dbo].[Person] ( … , CONSTRAINT [DF_Person_CreatedAt] DEFAULT (sysutcdatetime()) FOR [CreatedAt] );` → Msg 102 "Incorrect syntax near the keyword 'DEFAULT'". With SET XACT_ABORT ON the batch aborts and the whole deployment rolls back. Creating ANY new table that has a default constraint is impossible.

**Fix proposto**

Delete the `case DefaultConstraint df:` branch from the CREATE TABLE constraint loop. Instead pass the DefaultConstraint (not just a name set) into FormatColumn and emit the column-level form, which is valid and keeps the name: `[CreatedAt] [datetime2] (7) NOT NULL CONSTRAINT [DF_Person_CreatedAt] DEFAULT (sysutcdatetime())`. Re-accept the golden file. Add a ScriptDom parse assertion over every golden output (Microsoft.SqlServer.TransactSql.ScriptDom is already a Core dependency) so invalid DDL can never be pinned again.

**Verifica adversariale**

TableScriptEmitter.cs:66-70 is exactly as quoted — `case DefaultConstraint df:` appends `    CONSTRAINT [n] DEFAULT <expr> FOR [col]` INSIDE the CREATE TABLE column/constraint list (the block at 45-78 runs before `sb.AppendLine(");")` at 81). SQL Server's <table_constraint> grammar admits only PRIMARY KEY / UNIQUE / FOREIGN KEY / CHECK; `DEFAULT … FOR col` exists only in ALTER TABLE ADD → Msg 102. Reachability confirmed on both sides of the model: TableReader.cs:45-46 populates Column.DefaultExpression from sys.default_constraints AND ConstraintReader.cs:265-281 adds a DefaultConstraint for the same row, so every live column with a default has both, and colsWithNamedDefault (TableScriptEmitter.cs:33-35) suppresses the valid column-level form at :481. The invalid DDL is PINNED: tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_check_and_default_constraints.verified.txt:6. No parse validation exists anywhere — Microsoft.SqlServer.TransactSql.ScriptDom is a PackageReference in DbDelta.Core.csproj:3 but `grep 'using Microsoft.SqlServer'` over src/ returns ZERO hits, so it is referenced and never used. Nothing live has ever exercised it: `grep -i default tests/Fixtures/Parity/01-source.sql 02-source.sql` returns nothing (17-scenario parity fixture has no DEFAULT constraint), and CompatMatrixTests.cs:105-121 seeds tables with no defaults. Not documented as deliberate anywhere in CLAUDE.md, docs/BACKLOG.md, or code comments.

---

## [critical] rebuild-drops-indexes-and-then-fails-on-index-delta  ·  scriptgen-correctness

**Identity rebuild destroys the table's indexes; unchanged indexes are never recreated and changed indexes make the deploy fail**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:312` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

EmitRebuild creates `[X_tmp]` via `EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false)` — EmitCreate never emits indexes at all — then `DROP TABLE [X]` (line 371) and `sp_rename`. Only named non-FK constraints are re-added (line 377-384). Indexes are handled elsewhere, in ScriptGenerator.cs:183-215, which for a `Different` table calls EmitIndexDelta, and EmitIndexDelta (line 605-631) only emits DROP/CREATE for indexes that are missing on one side or whose shape changed:

    bool shapeChanged = stillThere && !IndexShapeEqual(t, s!);
    if (!stillThere || shapeChanged) { sb.AppendLine(_indexEmitter.EmitDrop(...)); }

The index pass also runs *after* the table pass, i.e. after the rebuild. No test in tests/DbDelta.Core.UnitTests/ScriptGen/TableRebuildTests.cs or TableRebuildPkSwapTests.cs combines a rebuild with any index.

**Scenario di fallimento**

(a) Silent loss: dbo.Invoice gets IDENTITY added to Id; IX_Invoice_InvoiceDate exists identically on both sides. Script rebuilds the table (DROP TABLE kills IX_Invoice_InvoiceDate) and EmitIndexDelta emits nothing for it → after deploy the index is gone from production, date-range reports table-scan, and no error was raised. (b) Hard failure: same rebuild but IX_Invoice_InvoiceDate became UNIQUE in source → the delta emits `DROP INDEX [IX_Invoice_InvoiceDate] ON [dbo].[Invoice];` after the rebuild, where the index no longer exists → Msg 3701 → whole deploy rolls back.

**Fix proposto**

In EmitRebuild, after sp_rename, emit `CREATE … INDEX` for every index on the source-side table (reuse IndexScriptEmitter). In ScriptGenerator, collect the rebuild-target set (already computed as `rebuildTargets`, line 69) and skip EmitIndexDelta for those tables so nothing is double-dropped.

**Verifica adversariale**

Verified end to end. EmitRebuild (TableScriptEmitter.cs:312-386) calls EmitCreate for `[X_tmp]` (:348-350) — EmitCreate emits columns + PK/UQ/CK/DF only, never a CREATE INDEX; then `DROP TABLE [X];` (:371) and sp_rename (:372-373); then re-adds only IsNamedNonFkConstraint items (:377-384, filter at :388-395 = PK/UQ/CK/Default — no TableIndex). PK/UQ survive as constraints; true indexes do not. The index pass is at ScriptGenerator.cs:183-215, i.e. AFTER the table CREATE/ALTER pass at :170-176, and for a Different table it calls EmitIndexDelta (:605-631) which only DROPs/CREATEs when `!stillThere || shapeChanged` (:616-621, :625-628). So an index identical on both sides is silently gone after the rebuild (silent wrong deploy), and a shape-changed index produces `DROP INDEX … ON [dbo].[X];` against a table that no longer carries it → Msg 3701 → XACT_ABORT rollback. Test gap confirmed: TableRebuildTests.cs and TableRebuildPkSwapTests.cs construct every table with `Indexes: []`; no test combines a rebuild with an index. `rebuildTargets` already exists at ScriptGenerator.cs:69 so the proposed fix is well-placed.

---

## [critical] deploy-script-header-leaks-plaintext-password  ·  sql-injection-security

**Saved .sql deploy script embeds the plaintext source AND target passwords in its header comment**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:598` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

SaveDeployScriptAsync:
        string script = DeployScriptBuilder.Build(
            selected,
            AppState.SourceConnectionString ?? string.Empty,
            AppState.TargetConnectionString ?? string.Empty,
            DateTime.UtcNow);

and ExecuteOnTargetAsync (line 630) does the same. DeployScriptBuilder.cs:36-37 writes those two arguments verbatim into the file:
        header.AppendLine($"-- Source    : {sourceEndpointSummary}");
        header.AppendLine($"-- Target    : {targetEndpointSummary}");
(the parameters are documented as "Human-readable label for the source endpoint (server+db)").
AppState.SourceConnectionString is the FULL string built in App.axaml.cs:88 from ProjectSetupDialog.LastSourceConnectionString -> ProjectSetupViewModel.BuildConnectionString, i.e. ProjectSetupViewModel.cs:316: $"{baseCs};User Id={p.UserName};Password={p.Password}".
The author knew redaction was needed 40 lines later in the SAME method - MainWindowViewModel.cs:641-642 does use ConnectionStringRedactor.Redact for the confirm dialog summaries - but the two DeployScriptBuilder.Build calls do not.

**Scenario di fallimento**

Operator compares DEV -> PROD with SQL auth (User Id=sa, Password=Pr0d!Secret), clicks "Genera script", saves DbDelta-20260730-1200.sql. The first lines of that file are:
-- Source    : Server=DEV01;Database=App;Encrypt=False;TrustServerCertificate=True;User Id=sa;Password=Dev!Secret
-- Target    : Server=PROD01;Database=App;Encrypt=False;TrustServerCertificate=True;User Id=sa;Password=Pr0d!Secret
The .sql is then committed to the change-management repo / mailed to the DBA for review, which is the entire point of generating a script instead of executing it. Both sa passwords are now in version control and in an inbox in cleartext.

**Fix proposto**

Wrap both arguments at the two call sites: ConnectionStringRedactor.Redact(AppState.SourceConnectionString ?? string.Empty). Better: add a helper that returns only DataSource/InitialCatalog from SqlConnectionStringBuilder (that is what the parameter name and XML doc promise) and use it for both the header and the dialog summary so there is one path. Add a DeployScriptBuilderTests case asserting the generated script does not contain "Password=" when a full connection string is passed in.

**Verifica adversariale**

Reproduced exactly. MainWindowViewModel.cs:598-602 and :630-634 pass `AppState.SourceConnectionString` / `TargetConnectionString` as the `sourceEndpointSummary`/`targetEndpointSummary` arguments; DeployScriptBuilder.cs:36-37 writes both verbatim into `-- Source    :` / `-- Target    :`. Those AppState values are the FULL strings: App.axaml.cs:88-93 seeds them from ProjectSetupDialog.axaml.cs:137-138 -> ProjectSetupViewModel.cs:316 `$"{baseCs};User Id={p.UserName};Password={p.Password}"` (same at MainWindowViewModel.cs:298-299 and :541-542 for the load-project path). The redaction asymmetry is real: :641-642 in the SAME method wrap the identical values in ConnectionStringRedactor.Redact for the dialog summary. No test guards it — DeployScriptBuilderTests.cs:45-46 asserts the labels look like "MYSERVER/MyDB", i.e. the test author assumed the XML-doc contract ("server+db") that the call sites violate. CLI is clean: ScriptCommand only calls ScriptGenerator.Generate, which never emits endpoints. One aggravating fact the reviewer missed: the header is part of the first GO-batch, so on "Esegui" SqlExecutor.SplitOnGo ships both plaintext passwords to the target server as SQL text (MainWindowViewModel.cs:643-647) where any XEvent/Profiler/Query-Store capture on PROD records them.

**Nota**: Claim holds in full. Also fix the execute path, not just the save path: the comment travels in batch 1 to the target server.

---

## [critical] no-down-script-no-snapshot-no-backup  ·  undo-rollback

**Nothing in the product can undo a committed deploy: no inverse DDL, no schema snapshot, no backup, no journal**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:630` · effort **L** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

The whole execute path is: build one script, run it, print a message.

```csharp
string script = DeployScriptBuilder.Build(selected, ...source..., ...target..., DateTime.UtcNow);
ConfirmExecuteViewModel vm = new(..., executeAsync: () => SqlExecutor.ExecuteAsync(
    AppState.TargetConnectionString!, script, CancellationToken.None, useOwnTransaction: false));
Views.ConfirmExecuteDialog dlg = new() { DataContext = vm };
await dlg.ShowDialog(owner).ConfigureAwait(true);
if (vm.Result is not null) { StatusText = vm.ResultMessage; }
```

`grep -i '(rollback|undo|backup|snapshot|journal|audit|revert)' src/` returns only: SqlExecutor's own XML doc comments, `DeploymentScriptWriter.cs:56` (`IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION` — the in-flight rollback, not an undo), the dialog's reassurance TextBlock, and unrelated words (`Database.cs` "snapshot" in a doc comment). There is no inverse-script generator, no `BACKUP DATABASE`, no schema serializer, no deploy log. `docs/BACKLOG.md` does not list undo/rollback either.

**Scenario di fallimento**

Target `PROD`, source `DEV`. In DEV the column `dbo.Customers.Notes` was dropped weeks ago. The user selects the `Customers` row (status Different), clicks Esegui. `TableScriptEmitter.EmitAlter` step 2 emits `ALTER TABLE [dbo].[Customers] DROP COLUMN [Notes];`, the transaction COMMITs, the dialog turns emerald: "Esecuzione completata — 14 batch in 812 ms." 4M rows of `Notes` are gone. The user realises 30 seconds later. DbDelta offers no undo command, no down script, no pre-deploy snapshot, and did not even persist the script or a record of the run — so the user cannot say what was changed, let alone reverse it. Recovery requires a DBA-side point-in-time restore that DbDelta neither took nor prompted for.

**Fix proposto**

Two layers, both cheap (see improvements `mvp-inverse-down-script` and `copy-only-backup-gate`): (1) generate the inverse script by swapping SideA/SideB + OnlyInA/OnlyInB on the selected `DifferencePair`s and re-running the existing `DeployScriptBuilder` — that is a full schema-level undo for ~everything except row data; (2) for the destructive subset, run `BACKUP DATABASE [db] TO DISK=... WITH COPY_ONLY, INIT` through the existing `SqlExecutor` before the deploy. Write up.sql + down.sql + meta.json to a per-run folder before executing.

**Verifica adversariale**

Reproduced exactly. MainWindowViewModel.cs:630-655 is the whole execute path: DeployScriptBuilder.Build → ConfirmExecuteViewModel(executeAsync: SqlExecutor.ExecuteAsync(..., CancellationToken.None, useOwnTransaction:false)) → ShowDialog → StatusText = vm.ResultMessage. Nothing else. My own greps corroborate: rollback/undo/backup/snapshot/journal/audit across src/ hits only SqlExecutor.cs:40/42 (XML doc), SqlExecutor.cs:100 (in-flight tx.RollbackAsync), DeploymentScriptWriter.cs:56 (IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION — in-flight only, inside the same script), ConfirmExecuteDialog.axaml:97 (reassurance text), MainWindowViewModel.cs:209 ('Snapshot current grid selection' — selection map, not schema), Database.cs:6 and ISchemaSource.cs:7 (doc-comment 'snapshot'). No inverse-DDL generator, no BACKUP DATABASE anywhere, no schema serializer, no deploy journal. File IO in the whole Avalonia project is exactly three lines (MainWindowViewModel.cs:340 File.Exists, :604-605 the manual Save-script picker) — nothing on the execute path. docs/BACKLOG.md: I read all of it; 'Snapshot' appears once, at line 85, as a v2 *source provider* (Scripts-Folder / Snapshot / Source-Control), not as an undo feature. No undo/rollback/backup item exists. Requirement 3 has zero implementation.

**Nota**: Claim holds in full, including 'did not even persist the script'. One nuance worth knowing for the fix: the script IS fully reconstructible from the same in-memory selection (DeployScriptBuilder.Build is pure and the separate Salva-script path at :598 calls it with identical arguments), so writing up.sql to a per-run folder is genuinely ~10 lines. The proposed inverse script by swapping SideA/SideB is schema-only — it cannot restore rows lost to DROP COLUMN / DROP TABLE / narrowing, so the COPY_ONLY backup layer is the part that actually satisfies 'undo every change'.

---

## [high] execute-runs-unseen-sql-no-preview-no-cancel-no-undo  ·  app-ui-robustness

**"Allinea destinazione" executes irreversible DDL the user has never seen, with no cancel and no undo path**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:618` · effort **M** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ExecuteOnTargetAsync builds the script and hands it straight to the dialog: `executeAsync: () => SqlExecutor.ExecuteAsync(AppState.TargetConnectionString!, script, CancellationToken.None, useOwnTransaction: false)`. ConfirmExecuteDialog.axaml shows counts ("5 solo destinazione") and the redacted endpoints — the script text is never displayed and there is no typed confirmation; the only gate is one click on the crimson "Esegui" button. CancellationToken.None means the operation cannot be cancelled, and ConfirmExecuteDialog.axaml.cs:22 actively refuses to close while running (`e.Cancel = true`). Nothing in the app generates a reverse/rollback script or takes a backup before running (no BACKUP DATABASE anywhere in src/, and the only rollback is the script's own in-transaction one on failure).

**Scenario di fallimento**

User ticks "seleziona tutto" on a PROD→PROD-copy comparison run in the wrong direction. 5 of the selected rows are "Solo destinazione": tables that exist only on the target and hold live data. Two clicks later ("Allinea destinazione" → "Esegui") the emitted DROP TABLE statements commit successfully; the dialog reports "Esecuzione completata — 43 batch in 12 s". The rows are gone, there is no reverse script, no pre-deploy backup, and the app offers no undo — recovery requires an out-of-band database backup the app never asked for. The user never saw the DROP statements before they ran.

**Fix proposto**

Three cheap guardrails in the existing dialog, no new subsystem: (a) show the script in a scrollable read-only TextBox inside ConfirmExecuteDialog (the string is already in hand); (b) when OnlyInTargetCount > 0, require typing the target database name to enable "Esegui"; (c) pass a real CancellationTokenSource into SqlExecutor and expose a Cancel button while IsRunning. Longer term: emit a reverse script from the same DifferencePairs (SideB → SideA) and offer to save it before executing.

**Verifica adversariale**

MainWindowViewModel.cs:643-647 hands the script straight to the dialog with `CancellationToken.None` and `useOwnTransaction: false`. ConfirmExecuteDialog.axaml shows the redacted endpoints, the four counts and a transaction note, but the script text appears nowhere in the file and there is no typed confirmation — the only gate is the crimson `Esegui` button bound to ExecuteCommand. ConfirmExecuteDialog.axaml.cs:22-28 cancels Closing while IsRunning and there is no Cancel/Abort control. On undo: `grep -rn 'BACKUP DATABASE'` over src/ returns nothing; the only rollback that exists is the script's own in-transaction one (DeploymentScriptWriter.cs:20 SET XACT_ABORT ON, :56 IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION), which protects a FAILED run only — a successful DROP commits and is unrecoverable. DROP TABLE for OnlyInB is real (TableScriptEmitter.cs:170-171). Nothing in docs/BACKLOG.md marks the missing reverse script/backup as deliberate.

**Nota**: Two precisions: the dialog is not contentless — it does show 'N solo destinazione' + redacted target + the transaction note; the accurate claim is that the *script text* is never shown. And a failed run does roll back, so the gap is strictly 'successful-but-unwanted execution has no undo' — which is exactly the owner's stated #1 concern.

---

## [high] lasterror-never-shown-silent-failures  ·  app-ui-robustness

**AppState.LastError is bound only in a view that is never instantiated — every comparison/project error is invisible**

- file: `src/DbDelta.App.Avalonia/Views/MainWindow.axaml:452` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`grep -rn LastError --include=*.axaml src/` returns only ConnectionPickerView.axaml:133/142 — and ConnectionPickerView is referenced by no other .axaml and constructed by no .cs (only its own code-behind), i.e. it is dead. MainWindow's two status surfaces both bind `{Binding AppState.StatusText}`, which is `IsBusy ? "Working…" : "Ready"`. The four writers (AppStateViewModel.cs:164/175/187/194/216, MainWindowViewModel.cs:194/281/342/561) therefore write to nothing. MainWindowViewModel.StatusText ("Script salvato in …", "Nessuna differenza selezionata.") is likewise bound nowhere — MainWindow.axaml:452 and :506 both bind AppState.StatusText, not the MainWindowViewModel one.

**Scenario di fallimento**

User clicks "Aggiorna" after a schema change. The target login has been revoked, so LiveDbSource.LoadAsync fails: LastError = "Login failed for user 'deploy'." The busy overlay disappears and the grid still shows the counts from the previous run ("12 differenze rilevate"). Nothing anywhere says the refresh failed, so the user treats the stale numbers as current — and, per the stale-comparison finding, can deploy them. Same silence for "Impossibile caricare il progetto: …" (corrupt .dbd via MRU) and "Nessun progetto attivo da salvare": the Salva button appears to do nothing.

**Fix proposto**

Add an error banner to MainWindow.axaml bound to AppState.LastError (IsVisible via StringConverters.IsNotNullOrEmpty) with a dismiss button, and bind the status-bar text to the MainWindowViewModel StatusText (falling back to AppState.StatusText) so deploy/save feedback lands somewhere. Pin it with a headless test that a failed compare leaves LastError non-empty and that the banner's converter shows it.

**Verifica adversariale**

`grep -rn LastError --include=*.axaml src/` returns only ConnectionPickerView.axaml:133 and :142, and ConnectionPickerView is referenced by no .axaml and constructed by no .cs (grep across src/ + tests/ finds only its own declaration + code-behind) — dead view. MainWindow.axaml's only two status surfaces (:452 status bar, :506 busy overlay) both bind `AppState.StatusText`, which is `IsBusy ? "Working…" : "Ready"` (AppStateViewModel.cs:137). So all 10 LastError writers (AppStateViewModel.cs:164/175/187/194/216; MainWindowViewModel.cs:194/281/342/561) write to nothing. Also confirmed MainWindowViewModel.StatusText (:574, written at :587/:608/:625/:654) is bound in no .axaml — the only StatusText bindings in the whole app are the two `AppState.StatusText` ones — so "Script salvato in …" and the execute outcome mirror are dead too. No headless test asserts any of this.

**Nota**: MainWindowViewModel.cs:166-168 contains a comment acknowledging "the status bar has no writable message channel (StatusText is computed from IsBusy)" — the gap is known, just never closed.

---

## [high] no-global-unhandled-exception-handler  ·  app-ui-robustness

**No global exception handler anywhere: any throw on an async void path kills the process silently**

- file: `src/DbDelta.App.Avalonia/Program.cs:9` · effort **M** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

Program.cs is `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)` with no try/catch, no `AppDomain.CurrentDomain.UnhandledException`, no `TaskScheduler.UnobservedTaskException`, no dispatcher exception filter; App.axaml.cs has none either. Reachable async void event handlers with no try/catch: App.axaml.cs:37 `desktop.MainWindow.Opened += async (_, _) => { await connections.LoadAsync…; await dialog.ShowDialog…; await appState.CompareCommand.ExecuteAsync… }`, MainWindow.axaml.cs:19 `private async void OnProjectMruSelectionChanged(...)` → `await vm.OpenRecentProjectCommand.ExecuteAsync((this, rp.Path))` whose body ends in `await _recentProjects.AddOrTouchAsync(path, …)` (file IO, uncaught), ProjectSetupDialog.axaml.cs:179 `OnLoadClick`, ConnectionManagerDialog.axaml.cs:15/42/78. There is no log file either — nothing is written anywhere on the way out.

**Scenario di fallimento**

%LOCALAPPDATA%\DbDelta\recent-projects.json is held open by a sync client (OneDrive/Dropbox) or the folder is read-only under a locked-down profile. User picks a project from the topbar MRU → the project loads and compares fine, then AddOrTouchAsync throws IOException inside the async void SelectionChanged handler → the window vanishes mid-session with no dialog, no log, no clue; if the user had unsaved selections they are gone. Same class of death for a corrupt .dbd via the setup dialog's "Carica" (see projectsetupdialog-load-crash).

**Fix proposto**

In Program.Main wrap StartWithClassicDesktopLifetime in try/catch that writes the exception to %LOCALAPPDATA%\DbDelta\crash.log and shows a message box; subscribe TaskScheduler.UnobservedTaskException + AppDomain.UnhandledException to the same sink; wrap each async void handler body in try/catch that sets AppState.LastError (once the banner from the lasterror finding exists).

**Verifica adversariale**

Program.cs:9 is a bare `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`; `grep -rn 'UnhandledException|UnobservedTaskException|CurrentDomain' src/` returns NOTHING across the whole solution. The reachable async void handlers are all as described and none has a try/catch: App.axaml.cs:37 (MainWindow.Opened), MainWindow.axaml.cs:19 OnProjectMruSelectionChanged (wired at MainWindow.axaml:171), ProjectSetupDialog.axaml.cs:179 OnLoadClick, ConnectionManagerDialog.axaml.cs:15/42/78. The IO throw is real: JsonRecentProjectsStore.AddOrTouchAsync → WriteAtomicAsync opens the temp file with FileShare.None and File.Move's it (JsonRecentProjectsStore.cs:105-119) with no catch; ReadDocumentAsync only catches JsonException, so IOException/UnauthorizedAccessException propagate out of MainWindowViewModel.cs:310 → through the awaited AsyncRelayCommand → out of the async void handler. I confirmed by IL that Avalonia 12.0.3's Dispatcher has UnhandledException/UnhandledExceptionFilter events — with no subscriber the exception is rethrown on the dispatcher loop, i.e. process death, and there is no log sink anywhere.

**Nota**: Cheaper fix than proposed: subscribe Avalonia's own `Dispatcher.UIThread.UnhandledException` (it exists in 12.0.3) plus TaskScheduler.UnobservedTaskException, and write to %LOCALAPPDATA%\DbDelta\crash.log. Note CommunityToolkit's AsyncRelayCommand rethrows onto the UI context by default, so unguarded [RelayCommand] bodies (e.g. SaveProjectAsync's store.SaveAsync at MainWindowViewModel.cs:226) are the same class of death.

---

## [high] app-script-loses-dependency-edges  ·  diff-engine-correctness

**The desktop app's alignment script is generated with NO dependency edges → cross-kind create order is wrong (new view over a new function always fails)**

- file: `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

DeployScriptBuilder.cs:52-56 `string body = _generator.Generate(syntheticResult, selection: null, options: Options.ComparisonOptions.Default | Options.ComparisonOptions.DoNotOutputCommentHeader);` — the 4th parameter `dependencies` is omitted, so ScriptGenerator.cs:60 `dependencies ??= [];` makes the resolver fall back to rank-then-name order. DependencyResolver.cs KindRank: `["View"] = 4, ["Function"] = 5`. The CLI does it right — ScriptCommand.cs:85 `dependencies: srcResult.Value!.Dependencies` — and LiveDbSource.cs populates `Dependencies` from sys.sql_expression_dependencies, but MainWindowViewModel.cs:598 and :630 (Deploy + Esegui) both route through DeployScriptBuilder.Build, which has no dependencies parameter at all.

**Scenario di fallimento**

Dev adds a scalar function `dbo.fnIvaCalcolata` and a new view `dbo.vFattureIva` whose SELECT calls it. Both are OnlyInA. In the app the user selects both and clicks "Esegui": createOrder is built with an empty edge list, so the SortedSet orders by KindRank → View(4) before Function(5) → the script emits `CREATE OR ALTER VIEW [dbo].[vFattureIva] AS SELECT dbo.fnIvaCalcolata(...)` BEFORE the function exists. CREATE VIEW binds at create time → Msg 4121 / "Cannot find either column 'dbo' or the user-defined function or aggregate 'dbo.fnIvaCalcolata'" → the whole deploy rolls back. The identical comparison run through `dbdelta script` succeeds, so the failure looks random to the user.

**Fix proposto**

Keep the source Database's `Dependencies` in AppStateViewModel when the compare loads it (AppStateViewModel.cs:199 already has `srcRes.Value!`), add an `IReadOnlyList<DependencyEdge> dependencies` parameter to DeployScriptBuilder.Build and forward it to Generate. ~10 lines across two files.

**Verifica adversariale**

Verified end to end. DeployScriptBuilder.Build (src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs:53-56) calls `_generator.Generate(syntheticResult, selection: null, options: Default | DoNotOutputCommentHeader)` — the 4th parameter `dependencies` (ScriptGenerator.cs:57) is omitted, and ScriptGenerator.cs:60 `dependencies ??= []` makes DependencyResolver.Order receive zero edges, so every node has in-degree 0 and the SortedSet emits in CompareNodes order = KindRank then schema/name (DependencyResolver.cs:14-40): View=4 before Function=5. Both app paths route through it — MainWindowViewModel.cs:598 (Deploy/save) and :630 (Esegui). The CLI is correct: ScriptCommand.cs:85 passes `dependencies: srcResult.Value!.Dependencies`, populated by LiveDbSource.cs:98 from DependencyReader (sys.sql_expression_dependencies). The app has the data available (AppStateViewModel.cs:184 `srcRes.Value!`) but keeps only the ComparisonResult (`LastComparisonRaw`), so the edges are discarded. A new view over a new function therefore emits CREATE VIEW first — views bind at create time, so it fails. No test covers this: the only ordering golden test (tests/DbDelta.ScriptGen.GoldenTests/DependencyOrderingGoldenTests.cs:36-40) calls ScriptGenerator.Generate directly WITH dependencies, and DeployScriptBuilderTests.cs has no dependency test at all.

**Nota**: Holds exactly as written, including the ~10-line fix. Same-rank cases are also wrong (view→view resolved alphabetically).

---

## [high] schemas-never-compared  ·  diff-engine-correctness

**Schemas are read but never diffed and never created → any object in a new schema produces a script that fails on the first statement**

- file: `src/DbDelta.Core/Diff/ComparisonEngine.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ComparisonEngine.Compare (lines 17-31) adds pairs for Tables, Views, Procedures, Functions, Triggers, Sequences, Synonyms, UDTs, TableTypes, Users, Roles, Permissions — `a.Schemas` / `b.Schemas` are never touched. `grep -rn "Schemas" src --include=*.cs` returns only Database.cs ctor parameters and a comment in ScriptGenerator.cs:118. `grep -rn "CREATE SCHEMA" src tests` returns nothing. KindCatalog.KnownKinds lists twelve kinds with no "Schema", even though ObjectModel/Schema.cs:8 already defines `Identity => new(SchemaName: Name, ObjectName: "", Kind: "Schema")`.

**Scenario di fallimento**

Dev adds a reporting area: `CREATE SCHEMA report;` then `CREATE TABLE report.Fatture(...)`, plus a view `report.vTotali`. Prod has no `report` schema. Compare reports only the table+view as OnlyInA; the missing schema is invisible. Generated script: `CREATE TABLE [report].[Fatture] (...)` → Msg 2760 "The specified schema name 'report' either does not exist or you do not have permission to use it". With `SET XACT_ABORT ON` + the writer's NOEXEC gate the whole deployment aborts and rolls back, and nothing in the UI told the user a schema was needed. The user cannot fix it from DbDelta at all — there is no way to make it emit CREATE SCHEMA.

**Fix proposto**

Add CompareSchemas to ComparisonEngine (presence/absence only; owner comparison optional), register "Schema" in KindCatalog, give DependencyResolver.KindRank a "Schema" = -1 slot so schemas emit before everything, and add a 15-line SchemaScriptEmitter (`CREATE SCHEMA [x] AUTHORIZATION [owner];` / `DROP SCHEMA`). All the plumbing (Database.Schemas, Schema.Identity) already exists.

**Verifica adversariale**

Verified. ComparisonEngine.Compare (lines 17-29) adds pairs for 12 kinds; `a.Schemas`/`b.Schemas` are never read — grep for `Schemas` across src returns only Database.cs ctor params (10, 72, 87) and the ScriptGenerator.cs:118 comment. That comment is itself misleading: it says 'Users, Roles, Permissions, and Schemas are excluded … so they are emitted in fixed positions (prologue / epilogue)', but there is no schema emitter at all — grep for `CREATE SCHEMA` across src+tests returns nothing, KindCatalog.KnownKinds (Reports/KindCatalog.cs:13-27) lists exactly 12 kinds with no Schema, and DependencyResolver.KindRank (lines 14-26) has no Schema slot. ObjectModel/Schema.cs already exposes `Identity => new(Name, "", "Schema")`, unused. So a table/view in a schema absent on the target emits `CREATE TABLE [report].[Fatture]` (TableScriptEmitter.cs:31) with no preceding CREATE SCHEMA → Msg 2760; DeploymentScriptWriter.cs:20 `SET XACT_ABORT ON` + the `IF @@ERROR <> 0 SET NOEXEC ON` gate (:38) then abort and roll back the whole deployment. This is a gap, not a documented exclusion: docs/00_overview.md:204 lists **Schema** as a supported object kind, and BACKLOG section D's out-of-scope list (CLR assembly, full-text, XML schemas, Service Broker, partition, filegroup) does not include schemas.

**Nota**: Holds. Worth also fixing the inaccurate ScriptGenerator.cs:118 comment, which claims schemas are emitted in fixed positions.

---

## [high] selected-permissions-silently-not-deployed  ·  diff-engine-correctness

**Permission differences are reported by the engine but silently dropped from the app's script (IgnorePermissions is in Default) → the tool reports success and the difference persists**

- file: `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ComparisonEngine.cs:29 always compares permissions: `pairs.AddRange(ComparePermissions(a.Permissions, b.Permissions));` (no options check anywhere in the engine). DeployScriptBuilder.cs:56 passes `ComparisonOptions.Default`, and ComparisonOptions.cs:36 `Default = IgnoreWhitespace | IgnoreComments | IgnoreFillFactor | IgnorePermissions | IgnoreStatistics`. ScriptGenerator.cs:278 `if (!options.HasFlag(ComparisonOptions.IgnorePermissions)) { EmitPermissions(writer, pairs); }` → never true from the app. MainWindowViewModel.cs:634-640 still counts those rows in the confirmation dialog (`objectCount: selected.Count`, `onlyInSourceCount: ...`).

**Scenario di fallimento**

On dev the DBA runs `GRANT EXECUTE ON dbo.uspChiudiPeriodo TO [app_writer]` plus two more grants. Compare shows three Permission rows as "solo nell'origine"; the user selects exactly those three and clicks Esegui. DeployScriptBuilder emits preamble + verdict only (EmitPermissions skipped), SqlExecutor runs it successfully, the dialog prints "The database update succeeded" and the status bar says 3 objects. Re-compare: the same three rows are still there. The user has no way to deploy a permission from the UI and no message explains why.

**Fix proposto**

In DeployScriptBuilder.Build, clear IgnorePermissions when the selection contains any pair with Identity.Kind == "Permission" (`opts = Default & ~IgnorePermissions` in that case) — the selection IS the user's intent. Longer term: stop reporting a kind in the grid that the script pipeline refuses to emit, or mark such rows as non-deployable in the UI.

**Verifica adversariale**

Verified. ComparisonEngine.cs:29 calls ComparePermissions unconditionally (the engine never consults IgnorePermissions anywhere). ComparisonOptions.cs:36-37 `Default = IgnoreWhitespace | IgnoreComments | IgnoreFillFactor | IgnorePermissions | IgnoreStatistics`. DeployScriptBuilder.cs:56 passes exactly `Default | DoNotOutputCommentHeader`, and ScriptGenerator.cs:278 `if (!options.HasFlag(IgnorePermissions)) EmitPermissions(...)` is therefore never entered from the app. Permission rows do reach the grid and ARE tickable: KindCatalog lists 'Permission', MainWindowViewModel.RebuildRows (:467-500) filters nothing by kind, and DifferenceRowViewModel.cs:140 `IsSelectable => !IsIdentical`. ConfirmExecuteViewModel is constructed with `objectCount: selected.Count` (MainWindowViewModel.cs:637-640) and never receives the script text, so nothing shows the user that the body is empty — SqlExecutor runs preamble+verdict, the dialog prints success and the status bar mirrors it (:654). Re-compare shows the same rows. The one nuance vs the write-up: this is a silent no-op, not damage.

**Nota**: Holds. Also true for the CLI's Compare call, but harmless there since the engine ignores the flag; the real bug is the emit gate in the app path only.

---

## [high] system-named-constraints-false-positive  ·  diff-engine-correctness

**Constraints are paired by name and is_system_named is never read → auto-named DEFAULT/CHECK constraints make almost every real table report Different and generate pointless DROP/ADD CONSTRAINT churn**

- file: `src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ConstraintReader DefaultsQuery (lines 72-84) and ChecksQuery (60-70) select `dc.name` / `cc.name` and never `is_system_named`. ComparisonEngine.cs:451 pairs strictly by that name: `var bByName = bx.ToDictionary(c => c.Name); ... if (!bByName.TryGetValue(left.Name, out Constraint? right)) { return false; }`. ComparisonOptions.cs:15 declares `IgnoreConstraintNames = 1 << 4` but grep shows the flag is never read anywhere in src. TableScriptEmitter.cs:202-214 then emits `ALTER TABLE ... DROP CONSTRAINT [<old name>]` for the unmatched one and :265-276 re-adds the source's name.

**Scenario di fallimento**

`ALTER TABLE Ordini ADD Stato int NOT NULL DEFAULT 0` was executed independently on dev and on prod (the normal way a column gets added twice). SQL Server generated `DF__Ordini__Stato__3B75D760` on dev and `DF__Ordini__Stato__1A14E395` on prod — the suffix is derived from the object id, so it essentially never matches. ConstraintsEqual finds no name match → the table is Different even though its shape is identical; the script emits `DROP CONSTRAINT [DF__Ordini__Stato__1A14E395]` + `ADD CONSTRAINT [DF__Ordini__Stato__3B75D760] DEFAULT ((0)) FOR [Stato]`, i.e. it hard-codes a system-looking name into prod. On a real 400-table ERP this fires on dozens of tables at once, burying the two changes the user actually cares about in noise they learn to click through.

**Fix proposto**

Add `is_system_named` to the DEFAULT/CHECK/key-constraint queries and carry it on the Constraint records. In ConstraintsEqual, pair system-named constraints by shape (column + normalized expression) instead of by name, and have the emitter reuse the target's existing name when only the auto-generated name differs. Wire ComparisonOptions.IgnoreConstraintNames to the same path so the user can force it for explicitly named constraints too.

**Verifica adversariale**

Verified. ConstraintReader.DefaultsQuery (src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs:73-85) and ChecksQuery (:60-71) project only `dc.name` / `cc.name` — no `is_system_named` — and the models carry no such field (DefaultConstraint/CheckConstraint). ComparisonEngine.ConstraintsEqual (:442-471) pairs strictly by name via `bx.ToDictionary(c => c.Name)` and returns false on the first miss, so a DF__ suffix mismatch (the suffix derives from the constraint's object_id, which differs per database) makes the whole table Different. ComparisonOptions.IgnoreConstraintNames (Options/ComparisonOptions.cs:15) is declared and never read anywhere in src (grep confirms). TableScriptEmitter.cs:202-214 then emits `ALTER TABLE … DROP CONSTRAINT [<target auto-name>]` and :265-276 re-adds with the SOURCE's auto-name hard-coded. This is an unimplemented feature, not a deliberate choice: docs/02_data_models.md:410 and :1914 both document `IgnoreSystemNamedConstraintAndIndexNames` as the intended escape hatch and that option does not exist in the enum; docs/00_overview.md:240 says the same for DEFAULTs. No test covers it (ConstraintDiffTests has no system-named case).

**Nota**: Holds. Emitted SQL is valid (no data loss) — the damage is false-positive noise plus baking a DF__-shaped name into prod, which then never matches again.

---

## [high] index-reader-skips-non-rowstore-and-view-indexes  ·  livedb-readers

**IndexReader silently ignores every index that is not a rowstore index on a base table (columnstore, XML, spatial, hash, indexed views)**

- file: `src/DbDelta.Providers.LiveDb/Readers/IndexReader.cs` · effort **L** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

IndexesQuery: `FROM sys.indexes AS i INNER JOIN sys.tables AS t ON t.object_id = i.object_id … WHERE t.is_ms_shipped = 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.type IN (1, 2) AND i.name IS NOT NULL`. Type 3 (XML), 4 (spatial), 5 (clustered columnstore), 6 (nonclustered columnstore) and 7 (hash) are dropped, and the `INNER JOIN sys.tables` drops indexes whose object_id is a view. Nothing anywhere records that rows were skipped, and `ClassifyTable` only looks at Columns/Constraints/Indexes (ComparisonEngine.cs:354-362).

**Scenario di fallimento**

Source has `CREATE CLUSTERED COLUMNSTORE INDEX CCI_Fact ON dbo.FactSales`, target has the same table as a heap. Both sides read zero indexes for `dbo.FactSales` → status Identical → the diff reports no difference and no DDL is generated; the target stays a heap and analytic queries regress by orders of magnitude, with the tool asserting the two databases match. Same shape for an indexed view: source `dbo.vTotals` WITH SCHEMABINDING + unique clustered index vs target plain view with byte-identical body → Identical, the materialization is never deployed.

**Fix proposto**

Widen the query to `i.type IN (1,2,3,4,5,6,7)` joined to `sys.objects` (type IN ('U','V')) and extend `TableIndex` with an `IndexType`/`IsColumnstore` discriminator so `IndexScriptEmitter` can emit the right CREATE and the comparer can flag a type change. Minimum viable step if that is too large: keep the filter but count the excluded rows and surface them as a per-table "unsupported index skipped" warning so the Identical verdict is no longer silent.

**Verifica adversariale**

Query verified verbatim at IndexReader.cs:26-37: `INNER JOIN sys.tables AS t ON t.object_id = i.object_id … AND i.type IN (1, 2) AND i.name IS NOT NULL`. Types 3/4/5/6/7 are dropped and the sys.tables join excludes view object_ids; nothing counts or reports the skipped rows. The same filter is hand-duplicated in the diff-viewer path (LiveDbObjectBodyResolver.cs:582). `ClassifyTable` only consults Columns/Constraints/Indexes (ComparisonEngine.cs:354-362), so a heap vs clustered-columnstore pair reads zero indexes on both sides → Identical, no DDL. The model cannot even express the difference: `TableIndex` (ObjectModel/TableIndex.cs) carries only Name/IsUnique/IsClustered/FilterExpression/KeyColumns/IncludedColumns — no type discriminator — and `IndexScriptEmitter.EmitCreate` hardcodes `CREATE [UNIQUE] CLUSTERED|NONCLUSTERED INDEX … (cols) [INCLUDE] [WHERE]`, which is why effort L is right. No documented exclusion: spec §1.2 lists Table "Indexes" in scope, §1.3 does not exclude columnstore, and docs/BACKLOG.md:69 only flags "filtered/columnstore indexes" as a *parity scenario worth adding* — an acknowledged test gap, not a scope decision. tests/DbDelta.Providers.LiveDb.IntegrationTests/IndexReaderTests.cs covers only rowstore NC/unique/filtered indexes.

**Nota**: Confirmed. The reviewer's minimum-viable step (count and surface skipped rows so the Identical verdict stops being silent) is the right S-sized down-payment; full modelling is L.

---

## [high] instead-of-triggers-on-views-skipped  ·  livedb-readers

**TriggerReader's INNER JOIN sys.tables silently discards every INSTEAD OF trigger on a view**

- file: `src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

TriggerQuery: `FROM sys.triggers AS tr … INNER JOIN sys.tables AS t ON t.object_id = tr.parent_id INNER JOIN sys.schemas AS ts ON ts.schema_id = t.schema_id WHERE tr.parent_class = 1 AND tr.is_ms_shipped = 0`. View triggers also have `parent_class = 1`, but their `parent_id` is a row in `sys.views`, so the join eliminates them. `Trigger` itself only carries `ParentSchema`/`ParentTable` strings and `TriggerScriptEmitter` just brackets them, so the model and emitter would handle a view parent unchanged.

**Scenario di fallimento**

Source has `CREATE TRIGGER dbo.trg_vOrders_Insert ON dbo.vOrders INSTEAD OF INSERT AS …` (the standard pattern for making a multi-table view updatable); the target has no such trigger. Both sides read zero triggers for it → no difference row, no DDL → after the "successful" deploy every INSERT against `dbo.vOrders` on the target fails with "View or function 'vOrders' is not updatable because the modification affects multiple base tables". The diff never mentioned the trigger.

**Fix proposto**

Replace the `sys.tables` join with `sys.objects AS po ON po.object_id = tr.parent_id AND po.type IN ('U','V')` (schema from `po.schema_id`). One-line change; add an integration test that creates an INSTEAD OF trigger on a view.

**Verifica adversariale**

Query verified verbatim at ModuleReader.cs:143-161: `FROM sys.triggers AS tr INNER JOIN sys.objects AS o … INNER JOIN sys.tables AS t ON t.object_id = tr.parent_id INNER JOIN sys.schemas AS ts ON ts.schema_id = t.schema_id WHERE tr.parent_class = 1 AND tr.is_ms_shipped = 0`. INSTEAD OF triggers on views are DML triggers with parent_class = 1 but parent_id in sys.views, so the sys.tables join silently eliminates them on both sides → no DifferencePair, no DDL. The reviewer's claim that the model and emitter would cope unchanged checks out: `Trigger` carries only ParentSchema/ParentTable strings (ObjectModel/Trigger.cs) and `TriggerScriptEmitter` scripts from the raw body via `ModuleHeader.ToCreateOrAlterScript(t.Body, …)` (TriggerScriptEmitter.cs:30-35), only bracketing ParentSchema/ParentTable in the ENABLE/DISABLE branch (line 50). Not a documented exclusion: Trigger.cs's doc comment excludes "DDL triggers, logon triggers, and CLR triggers … per spec §1.2" and the spec says "Trigger (DML only)" — an INSTEAD OF trigger on a view IS a DML trigger, so it is in scope by both documents. No test covers it: tests/DbDelta.Providers.LiveDb.IntegrationTests/TriggerReaderTests.cs has two cases, both AFTER triggers on dbo.Customer (a table).

**Nota**: Confirmed, and it is the cheapest high-severity fix in the set: swap the sys.tables join for `sys.objects AS po ON po.object_id = tr.parent_id AND po.type IN ('U','V')` with the schema taken from po.schema_id. Add the integration test the reviewer suggests.

---

## [high] masked-columns-invisible  ·  livedb-readers

**Dynamic data masking is not read, so a masked column compares equal to an unmasked one and a rebuild silently strips the mask**

- file: `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ColumnsQuery never projects `c.is_masked` / `c.masking_function` (`grep -rn "is_masked" src/` → no hits) and `Column` has no masking member (ObjectModel/Column.cs). `ColumnsEqual` therefore compares type/nullability/identity/default/computed/collation only (ComparisonEngine.cs:383-431). The spec's out-of-scope list names "Always Encrypted column awareness, TDE-aware behavior" but not dynamic data masking, which is a SQL 2016 feature inside the declared support floor.

**Scenario di fallimento**

Source `dbo.Customer.SSN varchar(11) MASKED WITH (FUNCTION = 'partial(0,"XXX-XX-",4)')`, target the same column unmasked. DbDelta reports the table Identical, so the operator ships a target where every user with SELECT reads full SSNs while believing prod matches dev. Worse in the other direction: when any identity change triggers `EmitRebuild`, the masked column is recreated from the model as a plain column (`CREATE TABLE [Customer_tmp]` … `DROP TABLE [Customer]`), so the deploy actively removes an existing masking control from production and reports success.

**Fix proposto**

Project `c.is_masked, c.masking_function` in both column readers, add them to `Column`, include them in `ColumnsEqual`/`ColumnShapeEqual`, and emit `MASKED WITH (FUNCTION='…')` in `TableScriptEmitter.FormatColumn` (plus `ALTER COLUMN ADD/DROP MASKED`). Gate on server major version ≥ 13 if 2014 support is ever added.

**Verifica adversariale**

Verified. `grep -rn 'is_masked|masking_function|MASKED|Masked' src/ tests/ --include=*.cs` → zero hits. TableReader.ColumnsQuery (TableReader.cs:24-51) does not project them, and `Column` (ObjectModel/Column.cs, read in full) has no masking member — so `ColumnsEqual` (ComparisonEngine.cs:365-440) and `ColumnShapeEqual` (TableScriptEmitter.cs:403-416) cannot see a mask, and `FormatColumn` (TableScriptEmitter.cs:448-486) cannot emit one. The active-removal path is real: `EmitRebuild` recreates the table from the model via `EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false)` then `DROP TABLE` the original (TableScriptEmitter.cs:348-371), so an existing MASKED WITH clause on the target is destroyed and never restored. No documented exclusion — spec §1.3 names "Always Encrypted column awareness, TDE-aware behavior" but not dynamic data masking, and DDM ships in SQL 2016 which is the declared floor (spec §1.2).

**Nota**: Confirmed. One extra wrinkle for the rebuild path: `INSERT INTO [X_tmp] … SELECT … FROM [X]` reads the masked column as the executing principal, so a deployer without UNMASK would copy masked literals into the rebuilt table — actual data corruption, not just loss of the control.

---

## [high] schemas-read-but-never-compared-or-created  ·  livedb-readers

**Schemas are read and then thrown away — no schema difference is ever reported and no CREATE SCHEMA is ever emitted**

- file: `src/DbDelta.Core/Diff/ComparisonEngine.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`SchemaReader` populates `Database.Schemas` (LiveDbSource.cs:33). `ComparisonEngine.Compare` compares tables, modules, triggers, sequences, synonyms, UDTs, table types, users, roles and permissions — there is no `CompareSchemas` call (ComparisonEngine.cs:18-29). `grep -rn "CREATE SCHEMA" src/ tests/` returns zero hits; ScriptGenerator's own comment says "Users, Roles, Permissions, and Schemas … are emitted in fixed positions (prologue / epilogue)" (ScriptGenerator.cs:118) but only `EmitUsers`/`EmitRoles` exist (:139-141).

**Scenario di fallimento**

Source DevDB has schema `sales` with `sales.Order`; target ProdDB has no `sales` schema. The diff shows `Table sales.Order — Only in source` and nothing about the schema. The generated script runs `CREATE TABLE [sales].[Order] (…)` → Msg 2760 "The specified schema name 'sales' either does not exist or you do not have permission to use it", the error gate fires and the whole transaction rolls back. The user cannot deploy a new schema at all, and README/spec advertise Schema as one of the 13 compared kinds.

**Fix proposto**

Add `CompareSchemas(a.Schemas, b.Schemas)` to `ComparisonEngine.Compare` (identity kind "Schema") and a `SchemaScriptEmitter` emitting `IF SCHEMA_ID('x') IS NULL EXEC('CREATE SCHEMA [x]');` in the prologue (before tables) and `DROP SCHEMA` in the epilogue for OnlyInB.

**Verifica adversariale**

Verified. `ComparisonEngine.Compare` (ComparisonEngine.cs:12-32) calls CompareTables/Modules×3/Triggers/Sequences/Synonyms/UDTs/TableTypeUdts/Users/Roles/Permissions — no CompareSchemas. `grep -rn 'CREATE SCHEMA|SCHEMA_ID|CompareSchemas' src/ tests/` → zero hits. `grep -rn 'Schemas' src/` returns only ObjectModel/Database.cs (the record member + ctor overloads) and the ScriptGenerator.cs:118 comment; there is no SchemaScriptEmitter in src/DbDelta.Core/ScriptGen/ (19 files, listed). `Schema.Identity` with `Kind: "Schema"` (ObjectModel/Schema.cs) is dead code — "Schema" is absent from ScriptGenerator's `topoKinds` (121-125). And it is NOT a documented exclusion: the spec's §1.2 table names Schema as the first of the 13 in-scope kinds (docs/superpowers/specs/2026-05-20-sql-compare-clone-design.md:32) and §1.3's non-goals list does not mention it; README.md advertises "13 object kinds". So `CREATE TABLE [sales].[Order]` against a target lacking `sales` fails Msg 2760 and the error gate rolls the whole deploy back.

**Nota**: Confirmed. Schemas are read (LiveDbSource.cs:33) into Database.Schemas and then completely unused — an entire declared object kind is unimplemented, not merely under-tested.

---

## [high] special-table-flavours-read-as-plain-tables  ·  livedb-readers

**sys.tables is read as if every row were a plain rowstore table — temporal, memory-optimized, external and FileTable flavours are invisible**

- file: `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs` · effort **L** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

TablesQuery selects only `s.name, t.name, t.object_id, t.modify_date … WHERE t.is_ms_shipped = 0`; ColumnsQuery selects only name/type/length/precision/scale/nullable/identity/default/computed/ordinal/collation. `grep -rn "temporal_type|is_memory_optimized|generated_always" src/ tests/` returns zero hits. History tables and system-versioned tables are ordinary `is_ms_shipped = 0` rows, so both appear as unrelated plain tables and the period columns appear as plain `datetime2(7) NOT NULL` columns.

**Scenario di fallimento**

Target ProdDB has `dbo.Employee` SYSTEM_VERSIONED with `PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)` + `dbo.EmployeeHistory`; source DevDB has the same column list without versioning. Both sides read identical column sets → status Identical → real drift (versioning on/off) is never reported (false negative). In the reverse direction, deploying the source's temporal pair onto an empty target emits two plain CREATE TABLEs — the target gets a non-temporal `Employee` plus a junk `EmployeeHistory` table and the tool reports convergence. And if any column difference is found on a system-versioned target, the emitted `ALTER TABLE dbo.Employee DROP COLUMN [ValidFrom]` fails with Msg 13575 (or `DROP TABLE` in the rebuild path fails because SYSTEM_VERSIONING is ON), rolling back the entire deploy.

**Fix proposto**

Read `t.temporal_type`, `t.history_table_id`, `t.is_memory_optimized`, `t.durability`, `t.is_external`, `t.is_filetable` and `c.generated_always_type`; model them on `Table`/`Column`; emit `PERIOD FOR SYSTEM_TIME` + `WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))`, skip history tables as independent objects, and refuse (loudly) to emit ALTER/rebuild DDL against a flavour the emitter cannot express instead of emitting DDL that fails or converts the table.

**Verifica adversariale**

Verified. TableReader.TablesQuery selects only `s.name, t.name, t.object_id, t.modify_date … WHERE t.is_ms_shipped = 0` (TableReader.cs:12-22); ColumnsQuery projects only name/type/len/prec/scale/nullable/identity/default/computed/persisted/ordinal/collation (24-51). `grep -rn 'temporal_type|is_memory_optimized|generated_always|is_external|is_filetable|history_table_id|SYSTEM_VERSIONING|PERIOD FOR' src/ tests/ --include=*.cs` → **zero hits**. So a system-versioned table and its history table are two ordinary `is_ms_shipped = 0` rows, and the hidden GENERATED ALWAYS period columns read as plain `datetime2(7) NOT NULL` — `ColumnsEqual` (ComparisonEngine.cs:365-440) compares type/nullability/identity/default/computed/persisted/collation only, so versioning on-vs-off is a genuine false negative. Not a documented exclusion for DbDelta: spec §1.3 (docs/superpowers/specs/…:44-53) excludes Always Encrypted/TDE and Tier-3 kinds but says nothing about temporal/Hekaton/external/FileTable; the temporal/memory-optimized rows in docs/00_overview.md:224-225 and docs/01_architecture.md:968-972 describe *Redgate's* behaviour and the DDL constraints, not a DbDelta opt-out.

**Nota**: Confirmed. Note the two directions differ in danger: source-temporal→empty-target silently under-deploys and reports convergence (false negative, requirement 2); target-temporal + any column change produces DDL that fails (Msg 13575 / DROP TABLE on a system-versioned table) and the error gate rolls back — annoying but safe. The 'refuse loudly rather than emit DDL the emitter cannot express' half of the proposed fix is the resilient part and is worth doing first.

---

## [high] command-timeout-60s-hardcoded  ·  redgate-parity

**Deploy batches time out after a hardcoded 60 s, making any production-sized table rebuild undeployable**

- file: `src/DbDelta.Persistence/Sql/SqlExecutor.cs` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```
private const int CommandTimeoutSeconds = 60;
...
await using SqlCommand cmd = tx is null
    ? new(batch, cn) { CommandTimeout = CommandTimeoutSeconds }
    : new(batch, cn, tx) { CommandTimeout = CommandTimeoutSeconds };
```
`grep -rn CommandTimeout src/ tests/` returns only these three lines — no overload, no option, no project setting, no CLI switch. Both entry points use it: `ApplyCommand.cs:67` and the Avalonia execute flow.

**Scenario di fallimento**

An IDENTITY flip on `dbo.Invoice` (50M rows) generates the rebuild block `CREATE TABLE [dbo].[Invoice_tmp] … INSERT INTO [dbo].[Invoice_tmp] … SELECT … FROM [dbo].[Invoice] … DROP TABLE … sp_rename` as a single GO batch. The INSERT takes ~8 minutes; at 60 s `ExecuteNonQueryAsync` throws `Execution Timeout Expired`, the whole transaction rolls back, and the dialog shows "Esecuzione fallita". There is no setting to raise the timeout, so this change can never be applied through DbDelta — the user has to hand-run the script in SSMS, losing the whole guarded execution flow. Same for a large `CREATE INDEX` or a `ALTER TABLE … ADD` with a non-null default on a big table.

**Fix proposto**

Add an optional `commandTimeoutSeconds` parameter to `SqlExecutor.ExecuteAsync` (default 0 = unlimited, matching what a DBA expects of a deploy script), surface it as `dbdelta apply --timeout` and a field in the execute dialog, and persist it in the project. Effort is genuinely small — the plumbing is one parameter through two call sites.

**Verifica adversariale**

SqlExecutor.cs:23 `private const int CommandTimeoutSeconds = 60;` and lines 86-88 are verbatim as quoted; there is no overload, option, or setting — ExecuteAsync's only knobs are connectionString/script/ct/useOwnTransaction (lines 44-48). Both entry points hit it with no way to override: ApplyCommand.cs:67 and MainWindowViewModel.cs:643-647 (the ConfirmExecuteDialog flow), both with useOwnTransaction:false. The batching claim checks out: DeploymentScriptWriter.WriteBatch (lines 29-40) wraps ONE emitter body between GO markers, and TableScriptEmitter.EmitRebuild (312-386) produces the whole CREATE _tmp / INSERT … SELECT / DROP / sp_rename sequence as a single body — hence one SqlCommand under one 60 s timeout. Same for a single large CREATE INDEX (ScriptGenerator.cs:190-196). High stands: on any production-sized table the guarded execution flow simply cannot complete, and the user is pushed to SSMS, losing the confirm/rollback envelope the owner built.

**Nota**: Correct one detail in the scenario: the rollback is not driven by the timeout itself. A command timeout leaves the script's own transaction open; it is the `await using SqlConnection` dispose (SqlExecutor.cs:75) → pool reset that rolls it back. Net effect is the same (fail-safe, no partial apply), but say it accurately. Default should be 0 (unlimited) as proposed — a deploy script has no business being killed by a client clock.

---

## [high] missing-create-schema-emission  ·  redgate-parity

**Source-only schemas are never created — every deploy into a multi-schema target fails at the first object**

- file: `src/DbDelta.Core/Diff/ComparisonEngine.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`ComparisonEngine.Compare` builds pairs for 12 kinds and never touches `a.Schemas`/`b.Schemas`:
```
pairs.AddRange(CompareTables(a, b, options));
pairs.AddRange(CompareModules(a.Views, b.Views));
... (no CompareSchemas)
```
`SchemaReader.cs` reads `sys.schemas` into `Database.Schemas`, but `grep -rn "\.Schemas" src/ --include=*.cs` (excluding obj) returns **zero** consumers, and `grep -rn "CREATE SCHEMA" src/ tests/` returns **zero** hits. `KindCatalog.KnownKinds` has no "Schema" entry. No test in `tests/` uses a non-dbo schema in the script-gen path (`grep -rn 'Schema: "' tests/DbDelta.Core.UnitTests | grep -v dbo` → empty; the parity fixture is all `dbo`).

**Scenario di fallimento**

Source `DbA` has `CREATE SCHEMA sales;` + `sales.OrderHeader`; target `DbB` has neither. DbDelta reports `sales.OrderHeader` as "Solo provenienza" and emits `CREATE TABLE [sales].[OrderHeader] (...)`. Applying the script → Msg 2760 "The specified schema name 'sales' either does not exist or you do not have permission to use it" → the `IF @@ERROR <> 0 SET NOEXEC ON` gate trips → `ROLLBACK TRANSACTION`, "The database update failed". The deploy can never succeed and the UI gives no hint why. Multi-schema databases are the norm in ERP/line-of-business SQL Server, so this blocks a large share of real comparisons.

**Fix proposto**

Add "Schema" as a 13th kind: compare `Database.Schemas` by name (plus `principal_id`→owner for `AUTHORIZATION`), a `SchemaScriptEmitter` emitting `CREATE SCHEMA [x] AUTHORIZATION [y]` / `DROP SCHEMA`, register it first in the ScriptGenerator prologue (before Users/Roles is fine — schemas have no dependencies except principals) and in `KindCatalog`. Minimum viable fix if a full kind is too much: in `ScriptGenerator`, collect the distinct schema names of every emitted object and prepend `IF SCHEMA_ID(N'x') IS NULL EXEC(N'CREATE SCHEMA [x]');`.

**Verifica adversariale**

Every element of the claim reproduces. ComparisonEngine.cs:17-31 builds pairs for 12 kinds; there is no schema pass and `a.Schemas` is never touched (grep for `.Schemas` across src/ returns ZERO hits outside ObjectModel/Database.cs:10,72,87 — the reader fills a property nobody reads; SchemaReader.cs does query sys.schemas). `grep -rn "CREATE SCHEMA" src/ tests/` returns nothing but a docs line (docs/03_core_modules.md:1147). ScriptGenerator has 14 emitters, none for schemas, and its own comment at ScriptGenerator.cs:118-120 asserts "Users, Roles, Permissions, and Schemas … are emitted in fixed positions (prologue / epilogue)" — the prologue (lines 137-141) emits only Users and Roles, so that comment is false. KindCatalog.cs KnownKinds is 12 entries, no "Schema". No SCHEMA_ID/CREATE SCHEMA guard anywhere in ScriptGen, and DeploymentScriptWriter.WritePreamble emits only SET statements + BEGIN TRANSACTION. Tests: the parity fixture (tests/Fixtures/Parity/01-source.sql) is 100% dbo and `grep 'Schema: "' tests/ | grep -v dbo` is empty — untested. docs/02_data_models.md §5.3 lists `Schema` as a first-class Redgate object type, so this is a gap, not a documented exclusion (BACKLOG.md D lists only Tier-3 kinds — Assembly, Full-text, XML schemas, Service Broker, Partition, Filegroup — schemas are not parked there). Failure mode holds: CREATE TABLE [sales].[X] against a target without `sales` → Msg 2760 → the IF @@ERROR gate + WriteVerdict's ROLLBACK path (DeploymentScriptWriter.cs:38,56) abort the whole deploy. Severity high, not critical: the failure is fail-safe (rollback, no partial apply, no data loss), but the emitted DDL is invalid on a very common shape.

**Nota**: Minor: the finding says schemas are "never created" — accurate. Add that ScriptGenerator.cs:118-120's comment actively claims they are emitted, which is worse than silence: it will mislead the next maintainer. The lazy fix (prepend `IF SCHEMA_ID(N'x') IS NULL EXEC(N'CREATE SCHEMA [x]');` for the distinct schemas of emitted objects) is S, not M; a full 13th kind with AUTHORIZATION/DROP is M.

---

## [high] non-rowstore-indexes-invisible  ·  redgate-parity

**Columnstore / XML / spatial / hash indexes are silently dropped from the model — a missing clustered columnstore reads as "Identical"**

- file: `src/DbDelta.Providers.LiveDb/Readers/IndexReader.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`IndexReader.IndexesQuery` hard-filters to rowstore only:
```
WHERE t.is_ms_shipped = 0
  AND i.is_primary_key = 0
  AND i.is_unique_constraint = 0
  AND i.type IN (1, 2)
  AND i.name IS NOT NULL
```
`sys.indexes.type` 3 = XML, 4 = spatial, 5 = clustered columnstore, 6 = nonclustered columnstore, 7 = nonclustered hash — all excluded. The same filter is duplicated at `src/DbDelta.Providers.LiveDb/ObjectBody/LiveDbObjectBodyResolver.cs:582` (`AND i.type IN (1, 2)`), so the diff viewer's synthesised CREATE TABLE hides them too. `ClassifyTable` then compares `a.Indexes`/`b.Indexes` — both empty for those index types. `grep -rni columnstore tests/ src/` → zero hits.

**Scenario di fallimento**

Source `dbo.FactSales` has `CREATE CLUSTERED COLUMNSTORE INDEX CCI_FactSales ON dbo.FactSales`; the target table has the identical column list as a rowstore heap. Both sides read zero indexes → `ClassifyTable` returns Identical → the row shows green "Identici", the deploy script contains nothing for that table, and the target silently stays rowstore (analytics queries stay 10-100x slower). Symmetrically, an XML index or a spatial index present on only one side is never reported and never deployed. This is a FALSE NEGATIVE — the failure mode the owner ranks worst.

**Fix proposto**

Widen the filter to `i.type IN (1,2,3,4,5,6,7)`, add `IndexType` to `TableIndex`, include it in `IndexesEqual`, and either emit the right DDL per type or (minimum honest behaviour) surface the object as Different with a "tipo di indice non supportato" note so the user is never told "identical" about something that isn't. Fix both call sites — `IndexReader` and `LiveDbObjectBodyResolver` carry independent copies of the query.

**Verifica adversariale**

IndexReader.cs:32-36 is verbatim as quoted, including `AND i.type IN (1, 2)`, and the duplicate exists at LiveDbObjectBodyResolver.cs:582 in ReadIndexesForObjectAsync (same five WHERE predicates) — so both the compared model and the diff pane's synthesised body drop types 3 (XML), 4 (spatial), 5/6 (columnstore), 7 (hash). ClassifyTable (ComparisonEngine.cs:354-362) compares only Columns/Constraints/Indexes, and TableIndex (used at IndexReader.cs:113-119) has no type member at all, so there is nowhere for the information to survive even if read. `grep -rni columnstore src/ tests/` returns zero. So a source table with a clustered columnstore index and a target rowstore heap with the same columns classifies Identical — a false negative, the failure class the owner ranks worst. BACKLOG.md:65-69 mentions "filtered/columnstore indexes" only as a *missing parity scenario*, i.e. acknowledged test-coverage gap, not a documented deliberate exclusion.

**Nota**: Fix must touch both query copies — IndexReader.cs:35 and LiveDbObjectBodyResolver.cs:582 — they are independent literals. The honest minimum (surface Different + "tipo di indice non supportato") is a much smaller diff than emitting per-type DDL, and already removes the false negative.

---

## [high] system-named-constraint-name-churn  ·  redgate-parity

**System-named DEFAULT/CHECK constraints make identical tables read "Different" forever — and no option can turn it off**

- file: `src/DbDelta.Core/Diff/ComparisonEngine.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`ConstraintsEqual` pairs constraints purely by name:
```
var bByName = bx.ToDictionary(c => c.Name);
foreach (Constraint left in ax) {
    if (!bByName.TryGetValue(left.Name, out Constraint? right)) { return false; }
```
`ConstraintReader.DefaultsQuery`/`ChecksQuery` select `dc.name` / `cc.name` but **never** `is_system_named`, so the model cannot tell an explicit name from a generated one. `ComparisonOptions.IgnoreConstraintNames` (bit 4) exists but `grep -rn "ComparisonOptions.IgnoreConstraintNames" src/` returns 0 hits — it is dead. Redgate ships `IgnoreConstraintAndIndexNames` (`icn`) and `IgnoreSystemNamedConstraintAndIndexNames` (`iscn`) precisely for this.

**Scenario di fallimento**

The same DDL `CREATE TABLE dbo.Customer (Id int NOT NULL, Status int NOT NULL DEFAULT 0)` is run on the dev and prod servers. SQL Server auto-names the default `DF__Customer__Statu__3A81B327` on one and `DF__Customer__Statu__5CD6CB2B` on the other. `Column.DefaultExpression` matches (so `ColumnsEqual` passes) but the name lookup misses → the table is reported Different, and the generated script emits a DROP CONSTRAINT + ADD CONSTRAINT for it. On a real 400-table schema with inline DEFAULTs and CHECKs this produces hundreds of spurious "Different" rows and a deploy script that churns constraints on every table — the exact false-positive class the owner's rule 2 forbids, with no escape hatch.

**Fix proposto**

Read `is_system_named` in `ConstraintReader` (all three constraint queries) and carry it on `Constraint`. In `ConstraintsEqual`, pair system-named constraints by shape (column + normalized expression) rather than by name, and pair explicitly-named ones by name. Then honour `IgnoreConstraintNames` for the explicit case too. The emitters must also omit the name when creating a system-named constraint.

**Verifica adversariale**

Quote is exact: ComparisonEngine.cs:451-457 pairs constraints via `bx.ToDictionary(c => c.Name)` and returns false on a name miss, before any shape comparison. ConstraintReader.cs DefaultsQuery (73-85) and ChecksQuery (60-71) select `dc.name`/`cc.name` and never `is_system_named` — `grep -rn is_system_named src/ tests/` is empty, so the model genuinely cannot distinguish. IgnoreConstraintNames has exactly one hit in all of src/ (its own declaration, ComparisonOptions.cs:15) — dead. The escape hatch IgnoreKeys exists (ComparisonEngine.cs:355) but is blunt (kills FK/PK comparison too) and unreachable: every call site hardcodes ComparisonOptions.Default (AppStateViewModel.cs:199, CompareCommand.cs:66, ReportCommand.cs:79, ScriptCommand.cs:80). Consequence verified in the emitter: TableScriptEmitter.EmitAlter pairs by name too (lines 187-214, 265-276), so a DF__…/DF__… name divergence produces `ALTER TABLE … DROP CONSTRAINT [old]` + `ADD CONSTRAINT [new] DEFAULT … FOR [col]`. The project's own spec documents this exact trap and prescribes a fix that was never built: docs/02_data_models.md:410 "System-named constraints (e.g. DF__MyTable__col1__5070F446) differ across databases even if semantically identical. Use IgnoreSystemNamedConstraintAndIndexNames to suppress." No test covers it (`grep DF__ tests/` empty). High stands: on a real inline-DEFAULT/CHECK schema this is a mass false positive with no usable toggle.

**Nota**: Two overstatements to trim: (1) the churn is one-shot, not "on every deploy" — after applying, the target carries the source's name, so the next compare is clean; (2) it is not data loss (DEFAULT drop/re-add is cheap), though a CHECK drop/re-add forces a full WITH CHECK validation scan on a big table. The pairing fix must also keep the Count equality short-circuit at ComparisonEngine.cs:446 in mind.

---

## [high] alter-column-blocked-by-dependent-index-pk-check  ·  scriptgen-correctness

**ALTER COLUMN is emitted without first dropping the index / PK / CHECK that depends on the column**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:244` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

EmitAlter step 3 emits the column change directly:

    sb.Append("ALTER TABLE ").Append(qualifiedName)
      .Append(" ALTER COLUMN [").Append(newCol.Name).Append("] ")
      .Append(SqlTypeFormatter.FormatColumnType(newCol.DataType));
    AppendCollation(sb, newCol);
    sb.Append(newCol.IsNullable ? " NULL" : " NOT NULL").AppendLine(";");

Dependent objects are only touched when they themselves changed: step 1 (line 202-214) drops a constraint only when `!stillPresent || shapeChanged`, and the index pass (ScriptGenerator.cs:183) runs *after* the table pass and only for indexes whose shape changed (EmitIndexDelta, line 614-622). A PK/CHECK/index that is identical on both sides is therefore never dropped, and SQL Server refuses the ALTER COLUMN (Msg 5074) for any type change on a column used by an index, PRIMARY KEY, or CHECK/UNIQUE constraint (only widening a var-length column in an index is exempt).

**Scenario di fallimento**

Source widens dbo.Orders.CustomerId from int to bigint. Target has IX_Orders_CustomerId on that column (identical shape both sides) and PK_Orders is unrelated. Script emits `ALTER TABLE [dbo].[Orders] ALTER COLUMN [CustomerId] [bigint] NOT NULL;` with no prior DROP INDEX → Msg 5074 "The index 'IX_Orders_CustomerId' is dependent on column 'CustomerId'. ALTER TABLE ALTER COLUMN CustomerId failed…" → whole deploy rolls back. Same failure for a PK column type change (`Code varchar(20)→char(20)`) and for `Amount decimal(18,2)→decimal(19,4)` when CK_Amount_Positive references Amount.

**Fix proposto**

Before emitting an ALTER COLUMN, compute the set of dependents of that column on the target side (indexes whose KeyColumns/IncludedColumns contain it, PK/UQ whose Columns contain it, CHECK constraints whose Expression mentions it) and emit DROP for them ahead of the ALTER + CREATE/ADD after it — reusing the existing IndexScriptEmitter and FormatStandaloneConstraintBody. Exclude these from the later index/constraint delta so nothing is double-emitted.

**Verifica adversariale**

TableScriptEmitter.cs:244-249 matches the quote — an unconditional `ALTER TABLE … ALTER COLUMN [c] <type> [COLLATE] NULL|NOT NULL;` for any shape change that is not computed/identity (:234-243). Dependents are only dropped when they themselves changed: step 1 at :202-214 requires `!stillPresent || shapeChanged`, and the index pass at ScriptGenerator.cs:183-215 both runs AFTER the table pass (:170-176) and only touches shape-changed indexes (:616-621). So for an int→bigint widening on an indexed column, or a PK column type change, or a column named in an identical CHECK, nothing is dropped first → Msg 5074 → XACT_ABORT rollback. Note the ordering makes it worse than claimed: even when the index DOES change shape, its DROP INDEX is emitted after the ALTER COLUMN, so it cannot unblock it. No test covers an ALTER COLUMN with a dependent index/PK/CHECK.

---

## [high] alter-column-silent-precision-loss  ·  scriptgen-correctness

**ALTER COLUMN narrowing scale/precision is emitted with no guard, and the preamble disables the one setting that would abort it**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:229` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

EmitAlter step 3 emits an unconditional ALTER COLUMN for any shape change (line 244-249); there is no narrowing detection anywhere in ScriptGen (grep for "data loss|destructive|warning" over src/DbDelta.Core finds only the four encrypted-module comments). DeploymentScriptWriter.WritePreamble line 18 emits `SET NUMERIC_ROUNDABORT OFF`, which is precisely the setting that would otherwise raise an error when an ALTER loses numeric precision.

**Scenario di fallimento**

Source dbo.Invoice.Amount is decimal(19,2); target (production) has decimal(19,4) because a hotfix widened it there and the change was never merged back. The user compares and deploys. Script emits `ALTER TABLE [dbo].[Invoice] ALTER COLUMN [Amount] [decimal] (19, 2) NOT NULL;`. With NUMERIC_ROUNDABORT OFF every stored value is silently rounded to 2 decimals — no error, no warning, deploy reports "The database update succeeded", and the discarded fractions are unrecoverable. Same silent truncation for datetime2(7)→datetime2(0) (fractional seconds) and float→real.

**Fix proposto**

Classify each ALTER COLUMN as widening / equal / narrowing (a small table over type family + length/precision/scale). For narrowing, emit a `-- WARNING: potential data loss` comment and surface it in the script/UI, and gate emission behind an explicit opt-in option (Redgate's equivalent of "allow data loss"). Keeping NUMERIC_ROUNDABORT OFF is Redgate parity, so the guard must live in the generator, not in the SET block.

**Verifica adversariale**

TableScriptEmitter.cs:229-249 emits ALTER COLUMN unconditionally for any non-computed/non-identity shape change; there is no narrowing classification anywhere — grep for `DeploymentWarning|narrow|destructive|dataLoss` over all of src/ returns only two unrelated XAML comments (DiffViewerView.axaml:82, MainWindow.axaml:482), and the only `-- WARNING` strings in ScriptGen are the four encrypted-module ones (FunctionScriptEmitter.cs:29, ProcedureScriptEmitter.cs:30, TriggerScriptEmitter.cs:33, ViewScriptEmitter.cs:34). DeploymentScriptWriter.cs:18 does emit `SET NUMERIC_ROUNDABORT OFF`, so a decimal(19,4)→decimal(19,2) ALTER rounds every stored value silently and the script then PRINTs 'The database update succeeded' (:54). Two things make it worse than written: the GUI's confirm dialog shows NO script preview (ConfirmExecuteDialog.axaml has one prose TextBlock at :26; ConfirmExecuteViewModel takes counts only, ConfirmExecuteViewModel.cs:18-35), so the operator never sees the statement before it runs, and there is no rollback-script generation anywhere in the repo — the loss is unrecoverable. Keeping NUMERIC_ROUNDABORT OFF is deliberate Redgate parity, so the reviewer is right that the guard must live in the generator.

---

## [high] drop-column-emitted-with-no-warning  ·  scriptgen-correctness

**DROP COLUMN is emitted unconditionally with no warning marker and no data preservation**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:219` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

    foreach (Column oldCol in oldT.Columns) {
        if (!newColsByName.ContainsKey(oldCol.Name)) {
            sb.Append("ALTER TABLE ").Append(qualifiedName)
              .Append(" DROP COLUMN [").Append(oldCol.Name).AppendLine("];");
        }
    }

No guard, no `-- WARNING` comment, no row-count check, no side-table copy. The only warning strings in ScriptGen are the four encrypted-module comments (grep confirms). The PhaseLabel for the batch is just "Altering Table [dbo].[X]" (ScriptGenerator.cs:289-307), so nothing in the script text tells the operator a column is about to be destroyed; the ConfirmExecuteDialog counts objects, not destructive statements.

**Scenario di fallimento**

A developer's local DB is the source and production is the target (a mis-ordered compare that DbDelta permits). Local dbo.Customer was created from an older migration and lacks TaxCode. Deploy emits `ALTER TABLE [dbo].[Customer] DROP COLUMN [TaxCode];` inside the transaction; every other statement succeeds, COMMIT runs, and 400k tax codes are gone. Nothing in the generated script, the phase labels, or the confirm dialog flagged the statement as destructive, and there is no rollback script to restore the data.

**Fix proposto**

Emit `-- WARNING: dropping column [X].[c] destroys its data` immediately above every DROP COLUMN / DROP TABLE, and expose a machine-readable destructive-operation list from Generate so DeployScriptBuilder / ConfirmExecuteViewModel can show a count and require a distinct confirmation. Pairs naturally with the narrowing classifier above.

**Verifica adversariale**

TableScriptEmitter.cs:219-226 is verbatim as quoted — unconditional `ALTER TABLE … DROP COLUMN [c];`, no guard, no comment, no preservation. PhaseLabel (ScriptGenerator.cs:289-307) yields only 'Altering Table [dbo].[X]'. ConfirmExecuteViewModel.cs:18-48 carries ObjectCount/DifferentCount/OnlyInTargetCount/OnlyInSourceCount — object-level counts, nothing about destructive statements — and ConfirmExecuteDialog.axaml contains no script preview at all (only the prose line at :26), so in the 'Esegui su target' flow the operator literally never sees the DROP COLUMN before it commits. Confirmed that the four encrypted-module comments are the only WARNING strings in ScriptGen (grep over src/DbDelta.Core/ScriptGen). Combined with the absence of any rollback-script generation this is a direct hit on the owner's SICURO + RESILIENTE requirements; the minimal `-- WARNING` comment above each DROP COLUMN / DROP TABLE is an S, the machine-readable destructive list for the dialog is an M.

---

## [high] drop-table-before-dropping-inbound-fks  ·  scriptgen-correctness

**Tables removed from source are dropped before any FK that references them is dropped → DROP TABLE always fails**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:146` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

The DROP pass runs early:

    foreach (ObjectIdentity id in createOrder.Reverse()) {
        DifferencePair pair = pairById[id];
        if (pair.Status != DifferenceStatus.OnlyInB) { continue; }
        string? body = DispatchBuild(id.Kind, pair);   // Table → "DROP TABLE [s].[n];"

All FK DROP statements are emitted only in the last pass, ScriptGenerator.cs:224-259 (EmitFkDelta, line 647-674). The doc comment at line 217 justifies this with "emitted last so referenced tables already exist" — true for ADDs, wrong for DROPs. The inbound-FK orchestration at line 82-111 only covers identity-*rebuild* targets, not dropped tables. DependencyResolver.Order skips FK edges entirely (`if (e.Kind == EdgeKind.ForeignKey) { continue; }`), so the reverse-topological order gives no help either.

**Scenario di fallimento**

Source removes the lookup table dbo.Currency. Target still has dbo.Invoice with FK_Invoice_Currency → dbo.Currency; on the source side Invoice no longer has that FK, so Invoice is Different and its FK drop *is* generated — but at the end of the script. Emitted order: `DROP TABLE [dbo].[Currency];` … (much later) `ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];`. The DROP TABLE raises Msg 3726 "Could not drop object 'dbo.Currency' because it is referenced by a FOREIGN KEY constraint" and the whole deploy rolls back. Removing any referenced table is impossible.

**Fix proposto**

Split the FK pass in two: emit all FK DROPs (target-side FKs that disappeared/changed, plus every target-side FK whose ReferencedTable is in the OnlyInB set — found by scanning result.Differences unfiltered) in a pass *before* the DROP pass; keep only the FK ADDs at the end.

**Verifica adversariale**

Ordering verified in ScriptGenerator.cs: DROP pass at :146-152 (`foreach (ObjectIdentity id in createOrder.Reverse())` → DispatchBuild → TableScriptEmitter.EmitDrop:170-171 = `DROP TABLE [s].[n];`), FK pass at :224-259 which is the LAST object pass, and EmitFkDelta (:647-674) is the only place a target-side FK DROP is produced (:662). The doc comment at :217-218 confirms 'emitted last so referenced tables already exist' — correct for ADDs, wrong for DROPs. Reverse-topological order gives no help: DependencyResolver.Order skips FK edges outright (Dependency/DependencyResolver.cs:62 `if (e.Kind == EdgeKind.ForeignKey) { continue; }`). The #33 inbound-FK orchestration (:79-111) is keyed off `rebuildTargets` only, so it never covers OnlyInB tables. Worse than described: if the referencing table is Identical it is filtered out at :61-62 and its FK is never dropped at all. No test covers FK-drop-before-table-drop — the only DROP-ordering property test is ScriptGeneratorProperties.cs:112-120 (table-before-sequence).

---

## [high] gui-deploy-script-has-no-dependency-ordering  ·  scriptgen-correctness

**The desktop app's deploy script is generated with an empty dependency list, so cross-object CREATE ordering is lost**

- file: `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs:53` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

DeployScriptBuilder.Build calls Generate with three arguments only:

    string body = _generator.Generate(
        syntheticResult,
        selection: null,
        options: Options.ComparisonOptions.Default | Options.ComparisonOptions.DoNotOutputCommentHeader);

The `dependencies` parameter is omitted → ScriptGenerator.cs:60 `dependencies ??= [];` → DependencyResolver.Order receives no edges and every node has in-degree 0, so the order degenerates to KindRank-then-schema-then-name (DependencyResolver.CompareNodes). Only the CLI passes real edges: src/DbDelta.Cli/Commands/ScriptCommand.cs:85 `dependencies: srcResult.Value!.Dependencies`. Both GUI entry points go through DeployScriptBuilder — src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:598 (save script) and :630 (Esegui su target).

**Scenario di fallimento**

Source has two new views: dbo.vw_ActiveCustomers selects from dbo.vw_Customers. In the GUI the user selects both and clicks "Allinea destinazione". Alphabetical order emits `CREATE OR ALTER VIEW [dbo].[vw_ActiveCustomers]` first → Msg 208 "Invalid object name 'dbo.vw_Customers'" (CREATE VIEW does not get deferred name resolution) → whole deploy rolls back. Equally: a new view calling a new scalar UDF always fails, because KindRank ranks View (4) before Function (5). The same comparison scripted via the CLI succeeds, so the failure only shows up in the primary UI.

**Fix proposto**

Add an `IReadOnlyList<DependencyEdge> dependencies` parameter to DeployScriptBuilder.Build and pass it through to Generate; supply AppState's source-side Database.Dependencies from MainWindowViewModel at both call sites.

**Verifica adversariale**

DeployScriptBuilder.cs:53-56 calls Generate with exactly three named arguments and omits `dependencies`; ScriptGenerator.cs:60 then does `dependencies ??= [];`, so DependencyResolver.Order gets zero edges, every in-degree is 0 (:53-57, :71-74) and the result is pure CompareNodes order = KindRank then schema then name (DependencyResolver.cs:14-40) — View is rank 4, Function rank 5, so a new view over a new UDF is always emitted first, and vw_ActiveCustomers sorts before vw_Customers. CREATE VIEW has no deferred name resolution → Msg 208. Only the CLI passes real edges (src/DbDelta.Cli/Commands/ScriptCommand.cs:85 `dependencies: srcResult.Value!.Dependencies`); both GUI paths go through DeployScriptBuilder (MainWindowViewModel.cs:598 save-script, :630 Esegui su target). Effort raised to M: AppStateViewModel.cs:184-200 loads srcRes/tgtRes locally and keeps only the ComparisonResult, so Database.Dependencies must first be retained on AppState — 3-4 files plus a test, not 1-2. No test pins ordering through DeployScriptBuilder (DeployScriptBuilderTests.cs:94-123 asserts only intra-table order for a single pair).

---

## [high] inbound-fk-orchestration-blind-outside-selection  ·  scriptgen-correctness

**The rebuild's inbound-FK drop only sees objects present in the ComparisonResult, which in the GUI is just the user's selection**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:84` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

The #33 orchestration scans the unfiltered result:

    foreach (DifferencePair p in result.Differences.Where(x => x.Identity.Kind == "Table")) { … }

but DeployScriptBuilder.cs:52 constructs the result *from the selection*: `ComparisonResult syntheticResult = new(selectedPairs);` and MainWindowViewModel.cs:665 defines selectedPairs as `[.. Rows.Where(r => r.IsSelected).Select(r => r.Pair)]`. Identical tables (and any difference the user left unchecked) are therefore absent from result.Differences, so their inbound FKs are invisible. The passing unit test TableRebuildPkSwapTests.Rebuild_drops_inbound_FK_from_Identical_table_before_rebuild only works because it hands the Identical pair to Generate directly.

**Scenario di fallimento**

dbo.Invoice.Id gains IDENTITY; dbo.InvoiceLine is Identical on both sides and holds FK_InvoiceLine_Invoice → Invoice(Id). The user checks only the Invoice row and clicks Esegui. `inboundFkDrops` is empty, so the script goes straight to the rebuild: `ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [PK_Invoice]` … `DROP TABLE [dbo].[Invoice];` → Msg 3726 (referenced by FK_InvoiceLine_Invoice) → rollback. The same comparison deployed with every row selected works, so the bug looks nondeterministic to the user.

**Fix proposto**

Give Generate an explicit "full catalog context" input (the complete pair list, or the target Database) that the inbound-FK scan uses, independent of the selection; DeployScriptBuilder should pass the full comparison alongside the selection instead of wrapping the selection in a synthetic ComparisonResult.

**Verifica adversariale**

ScriptGenerator.cs:84 scans `result.Differences` for the inbound-FK drop/add, and DeployScriptBuilder.cs:52 builds that result FROM the selection (`ComparisonResult syntheticResult = new(selectedPairs);`), where selectedPairs = MainWindowViewModel.cs:665-666 `[.. Rows.Where(r => r.IsSelected).Select(r => r.Pair)]` (per-row checkboxes, ResultsGridView.axaml:200). So in the GUI `result.Differences` IS the selection: any Identical or unchecked table holding an inbound FK is invisible, inboundFkDrops stays empty (:82 short-circuits on rebuildTargets.Count but the FK scan itself is what starves), and the rebuild reaches `DROP TABLE` (TableScriptEmitter.cs:371) with the FK still in place → Msg 3726 → rollback. The reviewer's read of the passing test is right: TableRebuildPkSwapTests.cs:118-124 hands the Identical InvoiceLine pair directly to Generate, which the GUI never does. Also note the same selection-scoping silently disables ScriptGenerator's own comment at :67-68 ('Looking up via result.Differences (unfiltered) covers FKs held by Identical tables').

---

## [high] no-create-schema-emitted-ever  ·  scriptgen-correctness

**Schemas are read but never compared and never created — any object in a schema missing on the target cannot deploy**

- file: `src/DbDelta.Core/Diff/ComparisonEngine.cs:12` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ComparisonEngine.Compare compares 12 collections and never touches Schemas:

    pairs.AddRange(CompareTables(a, b, options));
    pairs.AddRange(CompareModules(a.Views, b.Views));
    … CompareUsers / CompareRoles / ComparePermissions

`Database.Schemas` (ObjectModel/Database.cs) is populated by SchemaReader (LiveDbSource.cs:33) and then unused. `grep -rn "CREATE SCHEMA" --include=*.cs --include=*.sql .` over the whole repo returns zero hits, and DispatchBuild (ScriptGenerator.cs:457-470) has no "Schema" case (`_ => null`). ScriptGenerator's own doc comment claims "Users, Roles, Permissions, and Schemas … are emitted in fixed positions (prologue / epilogue)" — Schemas are not emitted anywhere.

**Scenario di fallimento**

Source (dev) has a new reporting schema with reporting.SalesSummary and reporting.vw_MonthlyTotals. Target (prod) has no reporting schema. The diff correctly lists the two new objects; the deploy emits `CREATE TABLE [reporting].[SalesSummary] (…)` → Msg 2760 "The specified schema name 'reporting' either does not exist or you do not have permission to use it." → whole deploy rolls back. There is no way to get the deployment through from DbDelta.

**Fix proposto**

Add CompareSchemas to ComparisonEngine (presence/absence + owner) and a two-line SchemaScriptEmitter (`CREATE SCHEMA [x] AUTHORIZATION [owner];` / `DROP SCHEMA [x];`), emitted in the prologue before Users/Roles and in the DROP pass at the very end. Minimum viable fix: emit `IF SCHEMA_ID('x') IS NULL EXEC('CREATE SCHEMA [x]');` for every distinct schema referenced by an OnlyInA object.

**Verifica adversariale**

ComparisonEngine.Compare (Diff/ComparisonEngine.cs:17-31) adds 12 collections and never touches a.Schemas/b.Schemas. Database.Schemas is populated (LiveDbSource.cs:33 `new SchemaReader().ReadAsync(...)`, passed at :89) and then completely unused: a repo-wide grep for `Schemas` in src/ returns only Database.cs:10,72,76,87,93 (the record members + ctor plumbing) and the ScriptGenerator comment at :118. Repo-wide grep for 'CREATE SCHEMA' over *.cs/*.sql hits nothing (only docs/03_core_modules.md:1147, a design note about batch separators). DispatchBuild (:457-470) has no 'Schema' case → `_ => null`. So any OnlyInA object in a schema the target lacks emits `CREATE TABLE [reporting].[…]` → Msg 2760 → whole deploy rolls back, with no workaround inside DbDelta. The ScriptGenerator doc comment at :117-120 claiming Schemas 'are emitted in fixed positions (prologue / epilogue)' is simply false. Effort M as claimed, though the minimum viable `IF SCHEMA_ID(...) IS NULL EXEC(...)` prologue is an S.

---

## [high] rebuild-collides-on-named-default-constraint  ·  scriptgen-correctness

**Identity rebuild puts an auto-named DEFAULT on the temp table, then re-adds the named one → "Column already has a DEFAULT bound to it"**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:348` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

EmitRebuild creates the temp table with `includeNamedConstraints: false`:

    sb.Append(EmitCreate(newT with { Name = tmpName }, includeNamedConstraints: false));

With that flag, EmitCreate sets `colsWithNamedDefault` to the empty set (line 33-35), so FormatColumn's `if (!hasNamedDefault && !string.IsNullOrEmpty(c.DefaultExpression))` (line 481) DOES emit an inline, auto-named `DEFAULT <expr>` for every column that has one. Then after sp_rename, line 377-384 re-adds each named non-FK constraint from newT, and IsNamedNonFkConstraint (line 388-395) includes `DefaultConstraint => true`, producing `ALTER TABLE [dbo].[X] ADD CONSTRAINT [DF_X_c] DEFAULT (…) FOR [c];` on a column that already carries the auto-named default.

**Scenario di fallimento**

dbo.Invoice(Id int → IDENTITY, CreatedAt datetime2 NOT NULL CONSTRAINT DF_Invoice_CreatedAt DEFAULT (sysutcdatetime())). Rebuild emits `CREATE TABLE [dbo].[Invoice_tmp] (… [CreatedAt] [datetime2] (7) NOT NULL DEFAULT (sysutcdatetime()) …)` then, after sp_rename, `ALTER TABLE [dbo].[Invoice] ADD CONSTRAINT [DF_Invoice_CreatedAt] DEFAULT (sysutcdatetime()) FOR [CreatedAt];` → Msg 1781 "Column already has a DEFAULT bound to it." The rebuild has already dropped and renamed the table, so the transaction rolls back mid-flight; if NoTransactions is used the table is left with a DF__Invoice_tmp__… constraint name and every subsequent compare reports it Different forever.

**Fix proposto**

Thread the named-default column set into the temp-table create (e.g. add a parameter or always compute colsWithNamedDefault from the table regardless of includeNamedConstraints) so the temp table is created with no inline default for columns whose default will be re-added by name.

**Verifica adversariale**

Chain verified: EmitRebuild passes includeNamedConstraints:false (TableScriptEmitter.cs:348-350) → EmitCreate sets `colsWithNamedDefault = []` (:33-35) → FormatColumn is called with hasNamedDefault:false (:42) → :481 `if (!hasNamedDefault && !string.IsNullOrEmpty(c.DefaultExpression))` emits an inline, auto-named `DEFAULT <expr>` on the tmp table. After sp_rename, :377-384 re-adds every IsNamedNonFkConstraint, and :393 includes `DefaultConstraint => true`, producing `ALTER TABLE [dbo].[X] ADD CONSTRAINT [DF_x] DEFAULT (…) FOR [c];` on a column that already carries the inline default → Msg 1781 'Column already has a DEFAULT bound to it.' Both model fields are populated live (TableReader.cs:45-46 for Column.DefaultExpression, ConstraintReader.cs:265-281 for the DefaultConstraint), so any rebuilt table with a defaulted column hits it. Distinct from finding #1 (that one needs includeNamedConstraints:true). Tests only ever build rebuild tables with no defaults.

---

## [high] rebuild-silently-drops-triggers  ·  scriptgen-correctness

**Identity rebuild silently destroys every trigger on the table when the trigger is identical on both sides**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:61` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ScriptGenerator.Generate filters the work list to non-identical pairs:

    List<DifferencePair> pairs = [.. (selection ?? result.Differences)
        .Where(p => p.Status != DifferenceStatus.Identical)];

EmitRebuild (TableScriptEmitter.cs:371) issues `DROP TABLE [schema].[X];`, which in SQL Server drops all DML triggers defined on X. Nothing in EmitRebuild or in ScriptGenerator re-emits triggers whose Trigger.ParentTable is a rebuild target — the trigger pair is Identical, so it was filtered out at line 61 and never reaches DispatchBuild. Grep of tests/ for a rebuild+trigger case returns nothing.

**Scenario di fallimento**

dbo.Invoice.Id gains IDENTITY(1,1) in source. Production dbo.Invoice carries trg_Invoice_Audit (byte-identical on both sides, so classified Identical). The deploy rebuilds Invoice; the trigger is dropped with the table and never recreated. Result: the audit table silently stops receiving rows, the deploy reports "The database update succeeded", and a re-compare still reports the trigger as Identical (it is absent from the target but the comparison of the *new* target would then show OnlyInA — after the deploy the diff shows a phantom regression). Auditing/soft-delete logic is silently disabled in production.

**Fix proposto**

After computing `rebuildTargets` in ScriptGenerator, look up every Trigger in `result.Differences` (unfiltered) whose (ParentSchema, ParentTable) is a rebuild target and force-emit its CREATE after the rebuild batch, mirroring the existing inbound-FK re-add pattern.

**Verifica adversariale**

ScriptGenerator.cs:61-62 filters `p.Status != DifferenceStatus.Identical` exactly as quoted, so an identical trigger never reaches DispatchBuild (:457-470). EmitRebuild issues `DROP TABLE [schema].[X];` (TableScriptEmitter.cs:371), which in SQL Server drops the table's DML triggers with it, and nothing in EmitRebuild (:312-386) or in ScriptGenerator re-emits triggers — the only rebuild-aware orchestration is the inbound-FK block (:79-111, :158-167, :265-273), which is FK-only. Trigger.ParentSchema/ParentTable exist on the model (ObjectModel/Trigger.cs:28-29) so the proposed fix is implementable. No test: TableRebuildTests.cs / TableRebuildPkSwapTests.cs never involve a trigger. One correction to the write-up: after the deploy the trigger is absent from the target, so a RE-compare reports it OnlyInA, not Identical — the loss is discoverable after the fact, it is just never flagged during the deploy that reports 'The database update succeeded' (DeploymentScriptWriter.cs:54).

---

## [high] identifier-bracket-injection-no-escape  ·  sql-injection-security

**No emitter escapes ']' when bracket-quoting identifiers - a catalog name containing ']' breaks out into arbitrary DDL**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:107` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

The only quoting helper in the whole ScriptGen namespace is:
    private static string Bracket(string identifier) => $"[{identifier}]";
No call to Replace("]", "]]") exists anywhere in src/ (grep 'Replace("]"' returns nothing). Every emitter concatenates raw catalog strings between brackets, ~40 sites, e.g.
TableScriptEmitter.cs:31  sb.Append("CREATE TABLE [").Append(table.Schema).Append("].[").Append(table.Name)
TableScriptEmitter.cs:451 sb.Append('[').Append(c.Name).Append("] ")
IndexScriptEmitter.cs:47  return $"DROP INDEX [{ix.Name}] ON [{schema}].[{table}];";
ForeignKeyScriptEmitter.cs:21-25, PermissionScriptEmitter.cs:31/34/60-63, UserScriptEmitter.cs:22/28/54/60, RoleScriptEmitter.cs:16/26/39/42, ScriptGenerator.cs:163-164/662, TableTypeUdtScriptEmitter.cs:17/21, SequenceScriptEmitter.cs:19/80 ...
The names arrive raw from the catalog readers (TableReader/ConstraintReader do r.GetString(n) with no sanitisation) and the resulting script is executed against the target by SqlExecutor.ExecuteAsync (MainWindowViewModel.cs:643).
Grep of tests/ for "]]" returns zero hits - no test covers a ']' in any identifier.

**Scenario di fallimento**

A developer with CREATE TABLE rights on the source/DEV database runs:
  CREATE TABLE dbo.T ([Id]] INT NOT NULL, [x] AS (1) --] int NULL);
which creates a legal column literally named `Id] INT NOT NULL, [x] AS (1) --`. DbDelta emits
  CREATE TABLE [dbo].[T] (\n    [Id] INT NOT NULL, [x] AS (1) --] int NULL\n);
The DBA reviews the diff grid (which shows the name as innocuous text), clicks "Esegui", and the crafted fragment is executed on PROD under the DBA's sysadmin credentials. Same vector via a role member name (RoleScriptEmitter.cs:39 ALTER ROLE [x] ADD MEMBER [y]) reaches ALTER ROLE / GRANT statements. Benign variant (a name that merely contains ']', e.g. a column called `a]b`): the batch is a syntax error, the deploy aborts mid-transaction and the operator gets an opaque "Incorrect syntax near" failure with no indication which object caused it.

**Fix proposto**

Add one shared helper (e.g. DbDelta.Core.ScriptGen.SqlIdentifier.Quote(string id) => "[" + id.Replace("]", "]]", StringComparison.Ordinal) + "]") and route every bracketing site through it - the mechanical part is a find/replace of `["` + name patterns in the 15 emitter files. Then add an Architecture.Tests rule forbidding the literal "[" concatenation in ScriptGen, plus a golden test with a table/column/constraint named `ev]il`.

**Verifica adversariale**

Verified the absence of any escape: `Bracket` at TableScriptEmitter.cs:107 is `$"[{identifier}]"`, it is the only quoting helper in ScriptGen, and grep for `Replace("]"` across src/ returns nothing. Counted 44 raw bracket-concatenation sites across 16 files in src/DbDelta.Core/ScriptGen (TableScriptEmitter.cs:31/451, FormatStandaloneConstraintBody:440-443, RoleScriptEmitter.cs:16/26/39/42 confirmed by reading, ScriptGenerator.cs:163-164, etc.). Grep of tests/ for `]]` returns zero. The crafted example parses: `[Id]] INT NOT NULL, [x] AS (1) --]` is a legal delimited identifier whose value is `Id] INT NOT NULL, [x] AS (1) --`, and re-emitting it as `[` + name + `] ` + type yields syntactically valid but semantically attacker-chosen DDL, executed on the target by SqlExecutor. Not a documented deliberate simplification — the opposite: docs/01_architecture.md:1431 (§13.2 Script Injection Protection) requires "Always bracket-quote all identifiers using the pattern QUOTENAME(@name) or its equivalent", and QUOTENAME doubles `]`. §13.2 item 2's "DDL bodies are not an additional attack surface" waiver covers module bodies only, not identifiers, and does not apply here because the escalation is source-DDL-writer -> target-sysadmin (DEV rights -> PROD deploy). The benign variant (a legitimate name containing `]`) is a plain failed deploy.

**Nota**: Holds. The doc waiver at 01_architecture.md:1432 covers proc/view bodies, not identifiers, so this is a gap against the project's own §13.2 item 1.

---

## [high] roundtrip-covers-3-of-13-kinds  ·  tests-cicd-arch

**Apply-then-recompare round-trip covers 3 of 13 object kinds; 8 kinds are never deployed to a real server in any test**

- file: `tests/DbDelta.Compat.Tests/CompatMatrixTests.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

The only two apply→reload→recompare tests both filter the convergence assertion down to a handful of kinds. CompatMatrixTests: `.Where(d => d.Identity.Kind is "Table" or "View" or "Procedure")` and its fixture seeds only 2 tables + 1 view + 1 proc. DependencyRoundTripTests: `.Where(d => d.Identity.Kind is "Table" or "View" or "Function")`. KindCatalog.KnownKinds lists 12 kinds (+TableType = 13). Trigger, Sequence, Synonym, UserDefinedType, TableType, User, Role and Permission are never emitted-then-applied-then-recompared anywhere. Grep of tests/ for SequenceReader / SynonymReader / UserReader / RoleReader / PermissionReader / UserDefinedTypeReader returns zero hits — six of the thirteen catalog readers have no assertion on their output at all; they only get *executed* incidentally via LiveDbSource.LoadAsync.

**Scenario di fallimento**

Source has `CREATE SEQUENCE dbo.OrderNo AS bigint START WITH 100`, target has it at `START WITH 1`. ScriptGenerator emits `ALTER SEQUENCE … RESTART WITH 100` (unit-tested as a string). Nothing verifies the statement actually applies, nor that SequenceReader reads back the value it wrote, so a wrong reader column index or a non-applicable ALTER clause ships undetected: user applies the script, the tool reports success, the next compare still shows the sequence as Different (or worse, as Identical while the server disagrees). Same class of hole for triggers (enable/disable state), roles (member deltas) and permissions (GRANT/REVOKE).

**Fix proposto**

One parameterised round-trip test in `DbDelta.Providers.LiveDb.IntegrationTests`: `[Theory]` over a per-kind seed SQL snippet → seed source, LoadAsync both, Generate, SqlExecutor.ExecuteAsync, reload target, assert `re.Differences.All(d => d.Status == Identical)` **without any kind filter**. Thirteen seed strings, one test body — and drop the `.Where(kind is …)` filters so a newly supported kind cannot silently opt out of convergence.

**Verifica adversariale**

Both filters read exactly as quoted. tests/DbDelta.Compat.Tests/CompatMatrixTests.cs:97-100 — `SeededDrift` = `.Where(d => d.Status != Identical).Where(d => d.Identity.Kind is "Table" or "View" or "Procedure")`, and SeedSourceSchemaAsync (:102-136) seeds exactly 2 tables + 1 view + 1 proc. tests/DbDelta.Providers.LiveDb.IntegrationTests/DependencyRoundTripTests.cs:46-49 — `.Where(d => d.Identity.Kind is "Table" or "View" or "Function")`. Those are the only two apply→reload→recompare tests in the repo (grep for useOwnTransaction across tests/ returns only these two plus SqlExecutor/DeployErrorHandling tests, which apply hand-written scripts, not generated ones). KindCatalog.KnownKinds (src/DbDelta.Core/Reports/KindCatalog.cs:13-27) lists 12 kinds including TableType, so Trigger, Sequence, Synonym, UserDefinedType, TableType, User, Role and Permission are indeed never emitted→applied→recompared. The finding's own failure scenario is independently proven real by the SequenceReader bigint-CAST defect, which this exact gap is what lets it ship.

**Nota**: One sub-claim is wrong: it is FIVE readers with no output assertion, not six. tests/DbDelta.Providers.LiveDb.IntegrationTests/TableTypeUdtReaderTests.cs:44-49 asserts TableTypeUdtReader output (schema, 3 columns, names, DataType, IsNullable) and TriggerReaderTests.cs covers triggers. Genuinely unasserted readers: SequenceReader, SynonymReader, UserReader, RoleReader, PermissionReader (grep of tests/ for those five names returns zero hits — verified). Severity kept high because the sequence defect below is the proof that the gap ships bugs; effort M is right (the parameterised theory is a real test-infra addition, and dropping the .Where filters will surface incidental users/roles/permissions noise that CompatMatrixTests.cs:93-96 documents as the reason the filter exists — that noise has to be handled, not just deleted).

---

## [high] sequence-reader-bigint-cast-overflow  ·  tests-cicd-arch

**SequenceReader CASTs sql_variant bounds to bigint — a decimal(38,0) sequence aborts the entire comparison**

- file: `src/DbDelta.Providers.LiveDb/Readers/SequenceReader.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`CAST(seq.start_value AS bigint) AS StartValue, CAST(seq.increment AS bigint), CAST(seq.minimum_value AS bigint), CAST(seq.maximum_value AS bigint)` — sys.sequences stores these as sql_variant typed to the sequence's own base type. SQL Server allows `decimal`/`numeric(38,0)` sequences, whose default minimum/maximum are ±99999999999999999999999999999999999999, far outside bigint. There is no test for this reader (`grep -rn SequenceReader tests/` → 0 hits) and the `Sequence` model types the bounds as `long?`, so the overflow is baked into the object model too.

**Scenario di fallimento**

Source DB contains `CREATE SEQUENCE dbo.BigCounter AS decimal(38,0);`. `LiveDbSource.LoadAsync` runs SequenceReader → SQL Server raises 8115 'Arithmetic overflow error converting numeric to data type bigint' → the `catch (SqlException ex)` at LiveDbSource.cs:117 turns it into `ErrorCode.CatalogQueryFailed` → the **whole compare of both databases fails** with an arithmetic-overflow message that names no object, and the user cannot compare that database at all. Exit code 20 in the CLI; a raw SQL message in the UI's LastError.

**Fix proposto**

Read the bounds as `SqlDecimal`/`decimal` (`CAST(... AS decimal(38,0))`) and widen `Sequence.MinValue/MaxValue/StartValue/Increment` to `decimal?`, emitting via InvariantCulture as today. Cheaper interim guard: `TRY_CAST(... AS bigint)` so an out-of-range bound reads NULL (→ `NO MINVALUE`) instead of killing the load — but that silently changes semantics, so prefer the decimal widening. Add the seed to the per-kind round-trip theory above.

**Verifica adversariale**

src/DbDelta.Providers.LiveDb/Readers/SequenceReader.cs:19-22 is verbatim as quoted: `CAST(seq.start_value AS bigint)`, `CAST(seq.increment AS bigint)`, `CAST(seq.minimum_value AS bigint)`, `CAST(seq.maximum_value AS bigint)`. sys.sequences stores those four columns as sql_variant typed to the sequence's own base type, and SQL Server permits decimal/numeric sequences. src/DbDelta.Core/ObjectModel/Sequence.cs:15-18 types them `long StartValue, long Increment, long? MinValue, long? MaxValue`, so the overflow is baked into the model as claimed. The blast radius is confirmed: SequenceReader is called unconditionally at src/DbDelta.Providers.LiveDb/LiveDbSource.cs:71, inside the single try whose last handler is `catch (SqlException ex)` at :117 → `ErrorCode.CatalogQueryFailed` with the raw ex.Message and no Remediation → CliErrorMapper maps CatalogQueryFailed to ExitCodes.SchemaReadFailure (20), and the UI sets `LastError = srcRes.Error!.Message` (AppStateViewModel.cs:187). So one wide sequence makes the entire database uncomparable. No test touches this reader (grep SequenceReader tests/ → 0 hits; tests/DbDelta.Core.UnitTests/ScriptGen/SequenceAlterTests.cs only exercises the emitter on hand-built models).

**Nota**: Correction that makes it MORE reachable, not less: the threshold is precision >= 19, not 38. `CREATE SEQUENCE dbo.S AS decimal(19,0)` gets default max 9999999999999999999 > bigint max 9223372036854775807 → error 8115. Only `AS decimal`/`decimal(18,0)` and narrower stay inside bigint. Prefer the decimal widening over the TRY_CAST interim: TRY_CAST turning an out-of-range MINVALUE into NULL makes SequenceScriptEmitter emit `NO MINVALUE` (src/DbDelta.Core/ScriptGen/SequenceScriptEmitter.cs:24,32 only emit MINVALUE/MAXVALUE when the nullable has a value), i.e. it converts a hard failure into a silently wrong deploy — worse against requirement 2. The emitter already writes every numeric with CultureInfo.InvariantCulture, so widening to decimal? needs no formatting change.

---

## [high] destructive-ddl-emitted-unflagged  ·  undo-rollback

**Row-destroying DDL (DROP COLUMN, DROP TABLE, type narrowing, table rebuild) is emitted and executed with no warning anywhere in the UI**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:219` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`TableScriptEmitter.EmitAlter` emits unguarded destructive statements:

```csharp
// ── 2) DROP columns present only on target.
foreach (Column oldCol in oldT.Columns)
    if (!newColsByName.ContainsKey(oldCol.Name))
        sb.Append("ALTER TABLE ").Append(qualifiedName).Append(" DROP COLUMN [").Append(oldCol.Name).AppendLine("];");
```
plus step 3 `ALTER COLUMN` with the *source* type (narrowing included), step 3's `DROP COLUMN` + `ADD` for computed-flag flips, `EmitDrop` = `DROP TABLE [{schema}].[{name}];` (line 171), and `EmitRebuild` (line 312) which does `INSERT INTO …_tmp SELECT … FROM original; DROP TABLE original; EXEC sp_rename …`.

The confirmation dialog shows only status counts — `ObjectCount`, `DifferentCount`, `OnlyInTargetCount`, `OnlyInSourceCount` (`ConfirmExecuteViewModel.cs:37-47`) — and one reassuring sentence (`ConfirmExecuteDialog.axaml:97`). `grep -rn 'AbortOnWarnings' src/` → no matches, even though `docs/01_architecture.md:1154` specifies it and names the exact serious warnings: *"no rollback possible (e.g., DROP TABLE with data), object rebuild required (data copy might lose data if column mapping is wrong)"*. `RequiresFullRebuild` (line 289) only detects identity changes, so a plain→computed column transition falls to the step-3 DROP COLUMN + ADD path.

**Scenario di fallimento**

Selection contains one table pair. Source has (a) removed column `Notes`, (b) `Code nvarchar(200)` → `nvarchar(50)`, and one target-only table `dbo.Archive_2019` holding 12M rows. The dialog reports "Oggetti selezionati: 2 — 1 diversi, 1 solo destinazione" and promises the target "resta invariata" on error. Executing runs `DROP COLUMN [Notes]`, `ALTER COLUMN [Code] nvarchar(50)` and `DROP TABLE [dbo].[Archive_2019]` inside one transaction that COMMITs successfully. Three irreversible data losses, zero warnings shown before, during, or after. (The narrowing is the only one partly guarded — ANSI_WARNINGS ON makes it error 8152 and roll back *if* some value exceeds 50 chars; if they all fit it silently succeeds and the headroom is gone.)

**Fix proposto**

Compute a `DeployRisk` list from the same `selectedPairs` `DeployScriptBuilder.Build` already receives (drop-column, drop-table, narrowing ALTER COLUMN, `RequiresFullRebuild` targets, computed-flag flips). Pass it into `ConfirmExecuteViewModel` as `Risks`; render a Warning-brush band listing them and gate `CanExecute` on a new `AcknowledgedRisk` checkbox. Same list drives `--abort-on-warnings` in `ApplyCommand`/`ScriptCommand`.

**Verifica adversariale**

Every code claim checks out. TableScriptEmitter.cs:219-226 emits unguarded ALTER TABLE … DROP COLUMN for target-only columns; :244-249 emits ALTER COLUMN with the *source* type (no width comparison, so narrowing passes through); :234-243 DROP COLUMN + ADD on any computed-expression change; :171 EmitDrop = DROP TABLE with no guard; :312-386 EmitRebuild = INSERT INTO _tmp SELECT … / DROP TABLE original / sp_rename. RequiresFullRebuild (:289-304) really only inspects IsIdentity / IdentitySeed / IdentityIncrement, so a plain→computed flip does fall to the :234 DROP COLUMN + ADD path. UI side: ConfirmExecuteViewModel exposes only ObjectCount/DifferentCount/OnlyInTargetCount/OnlyInSourceCount (:37-47) and ConfirmExecuteDialog.axaml renders exactly those plus the :97 transaction note — no risk list, no acknowledgement gate. grep AbortOnWarnings over src/ → zero hits, while docs/01_architecture.md §9.4 (line ~1154) specifies it and names 'no rollback possible (e.g., DROP TABLE with data), object rebuild required'. ANSI_WARNINGS ON is indeed set (DeploymentScriptWriter.cs:19), so the narrowing-overflow-errors-out-only-if-a-value-is-too-long nuance is right too.

**Nota**: Downgraded critical→high: the emitted DDL is correct for the diff the user selected, so this is a missing-warning/missing-gate defect rather than a wrong-deploy defect, and it is partly compensated — the dialog does open with a crimson 'Esecuzione diretta sul database di destinazione' alert band and does show the drop count as 'N solo destinazione'. So 'zero warnings shown before' is overstated: what is genuinely invisible is the per-risk detail (which columns get dropped, which types narrow, which tables get rebuilt), all of which is folded into the single 'N diversi' number. Note also that DROP COLUMN can be undone if a warning is heeded, but not after commit — this finding and no-down-script-no-snapshot-no-backup are the same wound from two sides; a risk list is the cheap half.

---

## [medium] connection-string-built-by-concatenation  ·  app-ui-robustness

**Connection strings are string-concatenated from user input, so a password containing ';' can silently retarget the deploy**

- file: `src/DbDelta.App.Avalonia/ViewModels/ProjectSetupViewModel.cs:309` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`private static string BuildConnectionString(ProjectEndpointPanelViewModel p) { string baseCs = $"Server={p.ServerName};Database={p.DatabaseName};Encrypt={p.Encrypt};TrustServerCertificate={p.TrustServerCertificate}"; … => $"{baseCs};User Id={p.UserName};Password={p.Password}" }` — no quoting/escaping. Same pattern in ProjectEndpointPanelViewModel.BuildConnectionString (447-461) and ConnectionEditViewModel.BuildConnectionString (172-178). The result becomes AppState.TargetConnectionString and is handed to SqlExecutor.ExecuteAsync, which re-parses it with SqlConnectionStringBuilder — where a later duplicate keyword wins over an earlier one.

**Scenario di fallimento**

Target password is `S3cret;Database=master` (or any password containing a semicolon followed by a keyword — legal in SQL Server, and users do paste generated passwords). The built string ends `…Database=ERP_PROD;…;User Id=deploy;Password=S3cret;Database=master`; SqlConnectionStringBuilder resolves InitialCatalog to `master` while the header strip and the confirm dialog still display ERP_PROD (they read CurrentProject.Target.Connection.DatabaseName / the same string's earlier Database token). The alignment DDL — CREATE/ALTER/DROP — is then applied to master. The benign variant (password with a bare ';') produces the cryptic "Keyword not supported" parse error the compare path already has bespoke debugging code for (AppStateViewModel.cs:158-179).

**Fix proposto**

Build every connection string with SqlConnectionStringBuilder (`new SqlConnectionStringBuilder { DataSource = …, InitialCatalog = …, UserID = …, Password = …, Encrypt = …, TrustServerCertificate = … }.ConnectionString`) — it quotes values correctly. One shared helper for the three call sites (CLAUDE.md DRY rule). Test: password `a;b=c` round-trips to the same DataSource/InitialCatalog.

**Verifica adversariale**

All three sites concatenate raw user input with no quoting: ProjectSetupViewModel.cs:309-319, ProjectEndpointPanelViewModel.cs:447-461, ConnectionEditViewModel.cs:172-178. The result is consumed unparsed-then-reparsed by LiveDbSource (AppStateViewModel.cs:181-182) and by SqlExecutor.ExecuteAsync (SqlExecutor.cs:62), where DbConnectionStringBuilder's dictionary semantics make a later duplicate keyword win. So a password containing `;Database=…` does silently move InitialCatalog, and a password containing a bare `;` produces exactly the parse failure the code already has bespoke debug handling for (AppStateViewModel.cs:158-179). No quoting or escaping exists anywhere in between.

**Nota**: Two corrections. (a) The retarget is less silent than described: the SAME poisoned string feeds LiveDbSource, so the comparison itself runs against master and the grid would show a wildly different diff before any deploy — the header/dialog mismatch is real but self-revealing. (b) The realistic day-to-day outcome is the benign `;` case, whose parse error is written to LastError and therefore shown NOWHERE (see lasterror-never-shown) — that is the stronger reason to fix it. Effort is M, not S: three call sites in three files plus tests, and CLAUDE.md's DRY rule wants one shared helper.

---

## [medium] credential-autofill-sends-previous-server-password-to-new-host  ·  app-ui-robustness

**Auto-connect fires 450 ms after a server-name edit while the previous server's password is still in the box**

- file: `src/DbDelta.App.Avalonia/ViewModels/ProjectEndpointPanelViewModel.cs:121` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

OnServerNameChanged: `_ = TryAutoFillCredentialsAsync(value); ScheduleAutoConnect();`. TryAutoFillCredentialsAsync returns early without touching the fields when the new server has no stored secret (`if (string.IsNullOrEmpty(blob)) { return; }`), and is additionally suppressed entirely while another call is in flight (`_autoFillFromCredentialsInFlight`). ScheduleAutoConnect then fires LoadDatabasesAsync after AutoConnectDebounceMs=450, whose IsAutoConnectEligible() only checks that ServerName/UserName/Password are non-empty — never that they belong to the same server. LoadDatabasesAsync then calls TryPersistCredentialsAsync, which writes `$"{UserName}|{Password}"` under CredentialKey(ServerName) when RememberCredentials is set (and TryAutoFillCredentialsAsync sets `RememberCredentials = true` on any successful autofill, line 628).

**Scenario di fallimento**

User selects PROD-SQL in the source panel; DPAPI autofill puts sa / production password in the fields. They then retype the server as `PROD-SQL2` (a typo, or a colleague's dev box on the same LAN) which has no stored secret → the fields keep the production sa password → 450 ms later, with no click, LoadDatabasesAsync opens a connection to that host with the production credentials (TrustServerCertificate=True by default, Encrypt=False), handing the password to a machine the user never intended to authenticate against. If the login happens to succeed, the production credentials are then persisted under the new server's key in Windows Credential Manager.

**Fix proposto**

Clear Password (and UserName when it came from the store) at the top of OnServerNameChanged before the autofill attempt, and require IsAutoConnectEligible to check that the credentials were filled for the current ServerName (track the server the current Password belongs to). Drop the `_autoFillFromCredentialsInFlight` early-exit in favour of cancelling the previous lookup, so the last keystroke always wins.

**Verifica adversariale**

Every link verified. ProjectEndpointPanelViewModel.OnServerNameChanged (:107-123) clears HasDatabases/AvailableDatabases but never Password/UserName, then fires TryAutoFillCredentialsAsync (:121) and ScheduleAutoConnect (:122). TryAutoFillCredentialsAsync returns without touching the fields when the new server has no stored blob (:613) and is fully suppressed while another lookup is in flight (:602). IsAutoConnectEligible (:438-443) checks only non-empty ServerName/UserName/Password — never that they belong to the same host — and HasDatabases was just reset, so it passes; AutoConnectAfterDelayAsync fires LoadDatabasesAsync 450 ms later with no user action (:413-436). LoadDatabasesAsync builds `Server={new};User Id={old};Password={old};Encrypt=False;TrustServerCertificate=True` (:447-461, Encrypt defaults false at :49, Trust true at :50) and on success calls TryPersistCredentialsAsync, which writes `"{UserName}|{Password}"` under CredentialKey(NEW ServerName) when RememberCredentials is set (:646-676) — and TryAutoFillCredentialsAsync sets RememberCredentials = true on any successful autofill (:628). ServerName updates per keystroke (ProjectSetupDialog.axaml:100/217 UpdateSourceTrigger=PropertyChanged), so a typo or a paused mid-typing prefix is enough.

**Nota**: Worth noting the exposure also happens while typing a brand-new hostname from scratch: any prefix the user pauses on for 450 ms gets a login attempt with whatever credentials are currently in the boxes.

---

## [medium] diffviewer-load-race-shows-wrong-object-body  ·  app-ui-robustness

**Fire-and-forget diff loads have no cancellation or generation guard — the pane can show object B's name with object A's SQL**

- file: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs:122` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

OnSelectedRowChanged: `_ = DiffViewer.LoadAsync(value, CancellationToken.None);` — no token, no await, no in-flight tracking. DiffViewerViewModel.LoadAsync sets the label first and the payload last: `ObjectQualifiedName = row.QualifiedName; SourceBody = await _resolver.ResolveSourceBodyAsync(...); TargetBody = await ...; Rows = LineDiffer.Compute(SourceBody, TargetBody);` with no check that `row` is still the selected one. ResultsGridView.axaml:32 binds `SelectedItem="{Binding AppState.SelectedRow, Mode=TwoWay}"`, so this fires on every arrow-key move through the grid.

**Scenario di fallimento**

User arrows down the results grid: row A = dbo.usp_LargeReport (body query takes 900 ms on a busy server), row B = dbo.vw_Small (80 ms). Load(A) starts, user presses Down, Load(B) starts and finishes first, then Load(A) finishes and overwrites SourceBody/TargetBody/Rows/Sections — while ObjectQualifiedName is still "dbo.vw_Small" (set last, by B). The viewer header says vw_Small while the diff shown is usp_LargeReport's. The user concludes vw_Small differs (or does not), ticks/unticks the row on that basis, and deploys the wrong object.

**Fix proposto**

Give DiffViewerViewModel a `CancellationTokenSource? _inFlight`: on each LoadAsync cancel+dispose the previous one, pass its token down, and bail out (`if (ct.IsCancellationRequested) return;`) before every property assignment. AppStateViewModel then calls `_ = DiffViewer.LoadAsync(value, CancellationToken.None)` unchanged. Test: two-call stub resolver where the first completes after the second.

**Verifica adversariale**

AppStateViewModel.cs:122 is `_ = DiffViewer.LoadAsync(value, CancellationToken.None);` — fire-and-forget, no token, no in-flight tracking. DiffViewerViewModel.LoadAsync sets the label first (`ObjectQualifiedName = row.QualifiedName`, :64) and the payload after two awaits (:65-71), with no re-check that `row` is still selected, so with a slow A and a fast B the final state is Name=B + SourceBody/TargetBody/Rows/Sections=A, and NavigateToRowRequested (:75) even scrolls to A's first section. Reachable: ResultsGridView.axaml:32 `SelectedItem="{Binding AppState.SelectedRow, Mode=TwoWay}"` fires on every arrow-key move, and the live resolver (LiveDbObjectBodyResolver, wired at AppStateViewModel.cs:206) does real DB round-trips. No test covers concurrent loads (DiffViewerViewModelTests' StubResolver returns Task.FromResult).

**Nota**: Downgraded high→medium: the viewer is read-only, so the damage is a misread, not a wrong payload — the deploy script is built from Rows' DifferencePairs, not from the viewer. Still a real 'affidabile' defect since the user ticks/unticks on what the pane shows.

---

## [medium] projectsetupdialog-load-crash  ·  app-ui-robustness

**"Carica" in the setup dialog is an async void with no try/catch around XmlProjectStore.LoadAsync — a bad .dbd kills the app**

- file: `src/DbDelta.App.Avalonia/Views/ProjectSetupDialog.axaml.cs:179` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`private async void OnLoadClick(object? sender, RoutedEventArgs e) { … DbDeltaProject project = await store.LoadAsync(pickedPath, CancellationToken.None).ConfigureAwait(true); if (DataContext is ProjectSetupViewModel vm) { vm.LoadFrom(project); } }` — no try/catch. XmlProjectStore.LoadAsync throws: `XDocument.Parse(xml)` (XmlException) and `… ?? throw new InvalidDataException($"'{filePath}' is not a valid DbDelta project file.")`. Contrast MainWindowViewModel.LoadProjectFromPathAsync:279-283, which does catch and set LastError for the exact same call.

**Scenario di fallimento**

The setup dialog is the first thing shown at startup. User clicks "Carica", browses to a .dbd on a network share that was truncated by a failed sync (or hand-edited, or written by a future schema version) → XmlException from XDocument.Parse propagates out of the async void handler → no global handler → the process disappears before the user has even reached the main window, with no message and no log.

**Fix proposto**

Wrap the body in try/catch and surface the message in the dialog (there is already a status TextBlock pattern in this dialog); same treatment for OnSaveClick's `_ = SaveAsAsync()`.

**Verifica adversariale**

ProjectSetupDialog.axaml.cs:179-203 is `private async void OnLoadClick` with no try/catch around `store.LoadAsync(pickedPath, …)`. XmlProjectStore.LoadAsync throws on bad input: `XDocument.Parse(xml)` (XmlException) at XmlProjectStore.cs:30, `?? throw new InvalidDataException` at :32, plus another InvalidDataException at :342 inside the parse. Combined with the confirmed absence of any global handler, the throw escapes the async void and kills the process. The contrast the reviewer draws holds: MainWindowViewModel.LoadProjectFromPathAsync:276-283 wraps the identical call in try/catch and sets LastError. The dialog is indeed the first window shown (App.axaml.cs:37-73).

**Nota**: Surfacing the message inside the dialog is the right fix, but note LastError is not a usable channel here — the setup dialog has no LastError binding either; use the panels' existing ConnectionStatusMessage/ScanStatusMessage TextBlock pattern.

---

## [medium] saveas-fire-and-forget-silent-failure  ·  app-ui-robustness

**Setup dialog's "Salva" is fire-and-forget: a failed write is swallowed and the user believes the project was saved**

- file: `src/DbDelta.App.Avalonia/Views/ProjectSetupDialog.axaml.cs:145` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`private void OnSaveClick(object? sender, RoutedEventArgs e) => _ = SaveAsAsync();` and SaveAsAsync itself has no try/catch around `await store.SaveAsync(path, project, CancellationToken.None)` / `await mru.AddOrTouchAsync(path, …)`. XmlProjectStore.SaveAsync writes to a temp file and rethrows on failure (`catch { try { File.Delete(tmp); } catch {} throw; }`). The faulted Task is discarded, so with no TaskScheduler.UnobservedTaskException hook the exception is lost entirely and the dialog shows no error.

**Scenario di fallimento**

%LOCALAPPDATA%\DbDelta\Projects is redirected to a full or read-only roaming profile (common in managed corporate images). User configures both endpoints, clicks "Salva", types "Prod vs Collaudo", presses OK — the name dialog closes and nothing else happens. No file is written, no error is shown, and the project is absent from the MRU next session; the user re-enters the whole endpoint + credential configuration by hand.

**Fix proposto**

Make the handler `async void` with try/catch (or await inside a small helper) and show the failure in the dialog; same for MainWindowViewModel.SaveProjectAsync, which also awaits store.SaveAsync unguarded.

**Verifica adversariale**

ProjectSetupDialog.axaml.cs:145 `private void OnSaveClick(…) => _ = SaveAsAsync();` and SaveAsAsync (:147-175) has no try/catch around `store.SaveAsync` (:169) or `mru.AddOrTouchAsync` (:174). XmlProjectStore.SaveAsync writes to a temp file and rethrows (verified :42-60 + the catch/delete/throw tail), and JsonRecentProjectsStore.WriteAtomicAsync throws on a read-only/full profile. Because the Task is discarded and no TaskScheduler.UnobservedTaskException hook exists anywhere in src/, the exception is lost entirely (.NET no longer escalates unobserved faults) and the dialog shows nothing.

**Nota**: The sibling claim about MainWindowViewModel.SaveProjectAsync (:226) is different in kind: it is inside an AsyncRelayCommand, which by default rethrows onto the UI context — that one crashes rather than going silent, so it belongs with the global-handler finding.

---

## [medium] collation-mass-diff-unsuppressable  ·  diff-engine-correctness

**Column collation participates in equality with no working escape hatch → comparing two DBs with different default collations flags every string table and emits ALTER COLUMN COLLATE statements that fail on indexed columns**

- file: `src/DbDelta.Core/Diff/ComparisonEngine.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ComparisonEngine.cs:428 `if (!string.Equals(col.Collation, other.Collation, StringComparison.OrdinalIgnoreCase)) { return false; }`. sys.columns.collation_name (TableReader.cs ColumnsQuery, `c.collation_name AS CollationName`) always returns the EFFECTIVE collation for char columns, i.e. the DB default when no explicit COLLATE was used. ComparisonOptions.cs:13 declares `IgnoreCollations` but grep shows it is never read; every call site hardcodes Default (AppStateViewModel.cs:199, CompareCommand.cs:66, ReportCommand.cs:79, ScriptCommand.cs:80). TableScriptEmitter.cs:244-249 then emits `ALTER TABLE ... ALTER COLUMN [c] [nvarchar] (100) COLLATE <src collation> NOT NULL;` (AppendCollation at :494).

**Scenario di fallimento**

Dev was installed with `Latin1_General_CI_AS`, prod with `SQL_Latin1_General_CP1_CI_AS` (two different installer defaults — extremely common). No column in either DB has an explicit COLLATE. Compare → every table containing a char/nvarchar column is Different; the alignment script contains hundreds of `ALTER COLUMN ... COLLATE Latin1_General_CI_AS` statements. The first such column that participates in an index, PK, UQ or CHECK fails with Msg 5074 "The index 'IX_Clienti_Nome' is dependent on column 'Nome'" → XACT_ABORT rolls the entire deploy back, so nothing at all is deployed. For the columns that would succeed, the statement silently re-collates production data, changing sort order and comparison semantics. The user has no option to say "ignore collation": the flag exists in the enum but does nothing.

**Fix proposto**

Honour ComparisonOptions.IgnoreCollations in ColumnsEqual (and in TableScriptEmitter.ColumnShapeEqual/AppendCollation), and compare against Database.DefaultCollation so a column that merely inherits each DB's default is treated as equal unless the user asks for strict collation compare. Independently, when a collation change IS wanted, the emitter must drop and recreate dependent indexes/constraints around the ALTER COLUMN or refuse and warn.

**Verifica adversariale**

Half deliberate, half real. Deliberate and TESTED: collation participating in column equality (ComparisonEngine.cs:421-431, with the M13-PARITY.5 #32 comment) is locked in by tests/DbDelta.Core.UnitTests/ScriptGen/ColumnCollationTests.cs:28 `Engine_flags_collation_only_change_as_Different`, and always-explicit COLLATE emission (TableScriptEmitter.cs:488-498) is a Redgate-parity decision recorded in docs/BACKLOG.md:132-136 ('always-explicit COLLATE (reverted #31; removed targetDefaultCollation …)'), tested at ColumnCollationTests.cs:74-113. So 'every string table reports Different across two default collations' is by design, matching Redgate's default (Ignore collations off). What IS real: (a) ComparisonOptions.IgnoreCollations (Options/ComparisonOptions.cs:13) is never read anywhere in src — grep confirms — so there is no escape hatch, and Database.DefaultCollation (Database.cs:65, populated LiveDbSource.cs:32/99) is read but never used by the engine; (b) TableScriptEmitter.cs:244-249 emits a bare `ALTER TABLE … ALTER COLUMN [c] [nvarchar] (100) COLLATE … NOT NULL;` with no drop/recreate of dependent indexes or constraints, which SQL Server rejects (Msg 5074) for any indexed/PK/UQ/CHECK-referenced column → XACT_ABORT rolls the whole deployment back. Downgraded to medium: the mass-diff is intentional parity and the failure mode is a safe abort, not damage.

**Nota**: Split the finding: 'collation participates in equality' is deliberate + tested (don't change it silently). The genuine defects are the dead IgnoreCollations flag and ALTER COLUMN COLLATE emitted without handling dependent indexes.

---

## [medium] dependency-cycle-aborts-generation  ·  diff-engine-correctness

**A legal SQL Server schema (UDF used in a CHECK/computed column of the table the UDF reads) produces a Table↔Function cycle and DependencyResolver throws, killing script generation with a message that blames a "reader bug"**

- file: `src/DbDelta.Core/Dependency/DependencyResolver.cs` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

DependencyResolver.cs:92-95 `if (remaining.Any(n => !DeferredKinds.Contains(n.Kind))) { throw new DependencyCycleException(FindCycle(remaining, adj)); }` with `DeferredKinds = { "Procedure", "Trigger" }` (line 32). DependencyCycleException.cs message: "Dependency cycle among create-validated objects" and the doc comment says it "signals a reader bug rather than user error". DependencyReader.cs emits every sys.sql_expression_dependencies row as an edge, including the table→function edge SQL Server records for computed columns and CHECK constraints (its own doc comment says so). ScriptCommand.cs has no try/catch and Program.cs has no global handler.

**Scenario di fallimento**

`dbo.fnSaldoCliente` SELECTs from `dbo.Movimenti`, and `dbo.Movimenti` carries `CONSTRAINT CK_Saldo CHECK (dbo.fnSaldoCliente(IdCliente) >= 0)` — a legal and well-known T-SQL pattern (SQL Server records both directions in sys.sql_expression_dependencies). Both objects are in the change set. `dbdelta script --source dev --target prod` → the topological sort leaves both nodes with in-degree ≥ 1, neither Table nor Function is a DeferredKind → DependencyCycleException escapes the command handler → stack trace, non-zero exit, no script file. There is no option or flag to get past it; the user's only recourse is to deselect objects, which the CLI does not support.

**Fix proposto**

Degrade instead of throwing, exactly as the deferred-kind path already does: append the residual nodes in CompareNodes order and have ScriptGenerator write a `-- WARNING: dependency cycle …, verify order` comment into the script. Keep DependencyCycleException only behind an explicit strict flag.

**Verifica adversariale**

Code path verified: DependencyResolver.cs:92-95 throws DependencyCycleException whenever any residual node's Kind is outside DeferredKinds = {Procedure, Trigger} (line 28-29), and DependencyCycleException.cs's message/doc-comment does blame 'a reader bug rather than user error'. Reachability verified partially. The reviewer's CHECK-constraint route is doubtful: for a CHECK/DEFAULT constraint SQL Server records the CONSTRAINT object as referencing_id, whose sys.objects.type is 'C'/'D', and DependencyReader.MapKind (Readers/DependencyReader.cs:68-78) returns null for those → edge dropped (line 58). The computed-column route does work — referencing_id is the table (type 'U' → 'Table'), which is exactly the edge shape the golden test builds (DependencyOrderingGoldenTests.cs:25-30) — so a computed column calling a data-accessing scalar UDF that SELECTs from the same table yields Table→Function plus the ordinary Function→Table module edge = a genuine cycle among two non-deferred kinds → throw, uncaught (ScriptCommand.cs has no try/catch, Program.cs is 12 lines with no handler). Reachability is narrower than claimed: only `dbdelta script` can throw. The app cannot — DeployScriptBuilder passes NO dependencies, so the resolver never sees an edge (see finding app-script-loses-dependency-edges). Downgraded to medium: CLI-only, needs an unusual (if legal) schema shape, and the outcome is a hard failure with no script rather than a wrong deploy.

**Nota**: Re-word the scenario around a computed column (not a CHECK constraint) calling a UDF that reads the same table — CHECK/DEFAULT expression edges are dropped by MapKind. Also note only the CLI `script` verb can hit it; the app is accidentally immune.

---

## [medium] grant-on-database-invalid-syntax  ·  diff-engine-correctness

**Database-scoped permissions are emitted as `GRANT <perm> ON DATABASE TO [x]`, which is not valid T-SQL — and a unit test asserts the invalid form**

- file: `src/DbDelta.Core/ScriptGen/PermissionScriptEmitter.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: True

**Evidenza**

PermissionScriptEmitter.cs:57-66 `FormatTarget(Permission p) => p.ClassDesc switch { "DATABASE" => "DATABASE", ... }` composed at :33 `sb.Append(" ON ").Append(FormatTarget(p));`. T-SQL requires either no ON clause (`GRANT CONNECT TO [app];`) or `ON DATABASE::[dbname]`. The expectation is frozen in tests/DbDelta.Core.UnitTests/ObjectModel/M6KindsTests.cs:169 `.Should().Be("GRANT CONNECT ON DATABASE TO [app];")`, so the test suite protects the bug instead of catching it.

**Scenario di fallimento**

`dbdelta script --include-permissions` against DBs that differ by a database-level grant (`GRANT VIEW DEFINITION TO [reader]`, `GRANT CONNECT TO [app]`, `GRANT EXECUTE TO [app]` — all common). The script contains `GRANT VIEW DEFINITION ON DATABASE TO [reader];` → Msg 102/156 "Incorrect syntax near 'DATABASE'" when applied → with XACT_ABORT the entire deployment rolls back, so a permission typo blocks every other change in the batch. Same for the REVOKE path (:47-53).

**Fix proposto**

For ClassDesc == "DATABASE" omit the ON clause entirely (`GRANT <action> TO [grantee]`), which targets the current database — the only database the script runs in. Fix M6KindsTests.cs:169 to the corrected expectation.

**Verifica adversariale**

Verified. PermissionScriptEmitter.cs:33 always appends `" ON "` + FormatTarget, and FormatTarget maps ClassDesc 'DATABASE' → the bare string "DATABASE" (:59) — also the `_ =>` fallback (:64). T-SQL accepts `GRANT CONNECT TO [app];` or `GRANT … ON DATABASE::[db] TO …`; `ON DATABASE` with no `::name` is a syntax error. The REVOKE path has the identical defect (:52-53). The invalid expectation is indeed frozen in a test: tests/DbDelta.Core.UnitTests/ObjectModel/M6KindsTests.cs:163-170 asserts `"GRANT CONNECT ON DATABASE TO [app];"` under the name `Permission_database_level_grant_omits_target_object` — it does not omit it. The path is reachable: PermissionReader's WHERE includes `p.class_desc IN ('DATABASE','SCHEMA','OBJECT_OR_COLUMN')` (Readers/PermissionReader.cs) so DATABASE-class rows are read, and `dbdelta script --include-permissions` clears IgnorePermissions (ScriptCommand.cs:74-77). The SCHEMA:: form (:60) is correct, so only the DATABASE branch is broken.

**Nota**: Holds. Note the app can never hit it today because the app path never emits permissions at all (finding selected-permissions-silently-not-deployed) — CLI --include-permissions only.

---

## [medium] included-columns-order-sensitive  ·  diff-engine-correctness

**Index INCLUDE lists are compared order-sensitively (and read with an ambiguous ORDER BY) → semantically identical indexes are reported Different and rebuilt on production tables**

- file: `src/DbDelta.Core/Diff/ComparisonEngine.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ComparisonEngine.cs:547 `if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns)) { return false; }` (and the same in ScriptGenerator.IndexShapeEqual :639). The read order is not pinned: IndexReader.cs:37 `ORDER BY i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal` — key_ordinal is 0 for EVERY included column, so their relative order is whatever the engine happens to return (in practice index_column_id, i.e. creation order) and is never disambiguated.

**Scenario di fallimento**

Dev: `CREATE INDEX IX_Fatture_Cliente ON Fatture(IdCliente) INCLUDE (Data, Importo)`. Prod: the same index created years earlier as `INCLUDE (Importo, Data)`. SQL Server treats these as the same index (INCLUDE is an unordered payload). SequenceEqual fails → the table is Different and the script emits `DROP INDEX IX_Fatture_Cliente ...` + `CREATE INDEX ...` on a 50M-row production table, i.e. a long lock/IO storm inside a SERIALIZABLE transaction for a no-op change.

**Fix proposto**

Compare IncludedColumns as a case-insensitive set (`SetEquals` after ordering) in both ComparisonEngine.IndexesEqual and ScriptGenerator.IndexShapeEqual, and add `ic.index_column_id` to the IndexReader ORDER BY so the read order is at least deterministic.

**Verifica adversariale**

Verified. ComparisonEngine.cs:547 `if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns))` uses the default string comparer (ordinal, order-sensitive), and ScriptGenerator.IndexShapeEqual:639 repeats it with `StringComparer.Ordinal`. INCLUDE is an unordered payload in SQL Server, so `INCLUDE (Data, Importo)` vs `INCLUDE (Importo, Data)` is the same index but reports Different → EmitIndexDelta (:605-631) emits DROP INDEX + CREATE INDEX for a no-op, inside the SERIALIZABLE transaction the writer opens. The read-order half of the evidence is accurate but weaker: IndexReader.cs:37 orders by `i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal` and key_ordinal is 0 for every included column, so the tie is broken by whatever the engine returns (in practice index_column_id); both sides are read the same way, so this mostly makes the read non-deterministic in principle rather than in practice. No test covers the reorder case — IndexDiffTests.cs:66 `Different_included_columns_yield_Different` compares ["Email"] against an empty list, i.e. a genuinely different set.

**Nota**: Holds. Use SetEquals/OrdinalIgnoreCase in both places; the ORDER BY addition is a nice-to-have, not the bug.

---

## [medium] non-btree-indexes-invisible  ·  diff-engine-correctness

**IndexReader filters out every index that is not clustered/nonclustered B-tree → columnstore, XML and spatial indexes are invisible to the diff (false Identical)**

- file: `src/DbDelta.Providers.LiveDb/Readers/IndexReader.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

IndexReader IndexesQuery line 35: `AND i.type IN (1, 2)` (plus `is_primary_key = 0 AND is_unique_constraint = 0`). Types 3 (XML), 4 (spatial), 5 (clustered columnstore) and 6 (nonclustered columnstore) never reach the model, so IndexesEqual (ComparisonEngine.cs:498-554) compares two lists that are equally truncated on both sides and finds them equal. docs/00_overview.md:247 states "Columnstore indexes | Compared as part of table indexes" as the intended behaviour, and nothing warns the user that they were skipped.

**Scenario di fallimento**

Dev added `CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Fatture ON dbo.Fatture (Data, Importo)` to make the reporting queries usable; prod has no such index. The reader drops the row on the dev side, both index lists come back with the same B-tree indexes → the table is reported Identical, the index is never deployed, and the user is told the schemas match. The mirror case is worse: if the table needs a rebuild for another reason (TableScriptEmitter.EmitRebuild), the columnstore index on the target is dropped with the table and never recreated, because the model never knew it existed.

**Fix proposto**

Widen the filter to `i.type IN (1,2,5,6)` (and 3/4 if XML/spatial are in scope), add `Type` to TableIndex, include it in IndexesEqual/IndexShapeEqual, and have IndexScriptEmitter emit the columnstore form. Minimum viable alternative: keep the filter but surface a per-table warning row when a skipped index type exists on either side, so "Identical" is never asserted over unread metadata.

**Verifica adversariale**

Verified. IndexReader.IndexesQuery line 35 is literally `AND i.type IN (1, 2)` (plus is_primary_key = 0 / is_unique_constraint = 0 / name IS NOT NULL), so types 3 (XML), 4 (spatial), 5/6 (columnstore) — and 7 (hash, memory-optimized) — never enter the model. TableIndex has no Type field, so ComparisonEngine.IndexesEqual (:498-554) compares two equally-truncated lists: a table whose only difference is a columnstore index reports Identical. The mirror case is confirmed too: TableScriptEmitter.EmitRebuild (:312-386) does `DROP TABLE` + sp_rename and re-adds only named non-FK constraints (:377) — indexes are re-emitted from the model by ScriptGenerator (:183-215), which never knew the columnstore index existed, so it is silently lost. Not an accepted exclusion: docs/00_overview.md:247 lists 'Columnstore indexes | Compared as part of table indexes', BACKLOG.md:69 lists columnstore only as a missing parity SCENARIO, and section D's out-of-scope list does not mention it.

**Nota**: Holds. Add type 7 (hash) to the widened filter list, and the minimum-viable warning row is the honest interim fix since 'Identical' is currently asserted over unread metadata.

---

## [medium] normalizer-collapses-literals-and-comments  ·  diff-engine-correctness

**BodyNormalizer collapses whitespace inside string literals and across line-comment boundaries → two genuinely different module bodies (and CHECK/DEFAULT expressions) compare Identical**

- file: `src/DbDelta.Core/Diff/BodyNormalizer.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

BodyNormalizer.cs:33-37 `string lf = body.Replace("\r\n", "\n").Replace('\r','\n'); string collapsed = WhitespaceRun().Replace(lf, " ");` with `[GeneratedRegex(@"\s+")]` — a blind global collapse with no lexer, no literal masking, no comment handling. It is the sole equality test for module bodies (ComparisonEngine.cs:330-334) and, via ExpressionsEqual (BodyNormalizer.cs:51-55), for DEFAULT / computed / CHECK / index-filter expressions.

**Scenario di fallimento**

(a) Literals: dev proc contains `PRINT 'Totale:  ' + @tot` (two spaces, column alignment), prod has `PRINT 'Totale: ' + @tot`. Both normalize to `PRINT 'Totale: ' + @tot` → Identical → the difference is invisible and can never be deployed. (b) Comments, worse: dev has `SELECT * FROM Ordini -- filtro\nWHERE Annullato = 0` while prod has `SELECT * FROM Ordini -- filtro WHERE Annullato = 0` (someone joined the lines). Prod's WHERE clause is INSIDE the comment, so prod returns cancelled orders — a real behavioural difference. After the newline→space collapse both sides are the byte-identical string `SELECT * FROM Ordini -- filtro WHERE Annullato = 0` → the engine reports Identical and the broken prod view/proc is never fixed. Same class of bug for `CHECK ([Codice] <> 'A  B')` vs `'A B'`.

**Fix proposto**

Before collapsing, tokenize enough to protect content: replace each single-quoted literal (and bracket-quoted identifier) with a placeholder, strip/normalize `--` comments to an explicit `\n` boundary (never a space) and `/* */` blocks, collapse whitespace on the remainder, then restore the placeholders verbatim. ~40 lines in the one file that both the engine and the emitters already share.

**Verifica adversariale**

Verified. BodyNormalizer.cs:33-37 is a blind global collapse — CRLF/CR→LF then `WhitespaceRun()` (`[GeneratedRegex(@"\s+")]`, line 19) → single space, trim, strip trailing ';'. No lexer, no literal masking, no comment handling; the class doc even calls it 'v1 strategy'. It is the sole module-body equality test (ComparisonEngine.ClassifyModule:330-334 compares the two normalized strings with StringComparison.Ordinal) and, via ExpressionsEqual (:51-55), the equality for DEFAULT/computed/CHECK/index-filter expressions (ComparisonEngine.cs:406, 411, 489, 494, 525) and for the emitters' shape checks (TableScriptEmitter.cs:414-415, 431-434). Both scenarios reproduce on paper: `'Totale:  '` and `'Totale: '` normalize identically (false negative on a literal), and `… -- filtro\nWHERE Annullato = 0` vs `… -- filtro WHERE Annullato = 0` normalize to the same byte string, so a prod view whose WHERE clause is commented out reports Identical. Not covered by tests: BodyNormalizerTests only asserts benign whitespace/CRLF/trailing-semicolon cases (lines 13-58). Note it is unconditional — IgnoreWhitespace/IgnoreComments are never consulted, so a user cannot turn it off either.

**Nota**: Holds. Literal masking is the load-bearing part; the `--` comment case is the one that can actually hide a behavioural difference.

---

## [medium] pk-uq-descending-key-not-read  ·  diff-engine-correctness

**PRIMARY KEY / UNIQUE constraint key columns lose their ASC/DESC direction → a DESC key compares Identical to an ASC key and is recreated ASC**

- file: `src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ConstraintReader KeysQuery (lines 13-31) selects `ic.key_ordinal` and `c.name` but never `ic.is_descending_key`, and the model stores plain strings: PrimaryKey.cs `IReadOnlyList<string> Columns`. Equality is therefore direction-blind: ComparisonEngine.cs:476 `pk.IsClustered == other.IsClustered && pk.Columns.SequenceEqual(other.Columns)`. Emission is too: TableScriptEmitter.cs:440 `$"PRIMARY KEY {(pk.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({string.Join(", ", pk.Columns.Select(Bracket))})"`. Note TableIndex DOES carry IsDescending (IndexColumn) — only PK/UQ lost it.

**Scenario di fallimento**

Dev has `ALTER TABLE Movimenti ADD CONSTRAINT PK_Movimenti PRIMARY KEY CLUSTERED (Data DESC, Id)` (deliberate, so the newest-first paging query scans forward); prod has the same PK declared `(Data, Id)`. Both sides read as ["Data","Id"] → Identical → the tool asserts the two schemas match while prod's clustered index is physically reversed, and the paging query keeps doing a backward scan. If the table is created from scratch by DbDelta, the DESC is silently dropped.

**Fix proposto**

Add `ic.is_descending_key` to KeysQuery and change PrimaryKey/UniqueConstraint.Columns to IReadOnlyList<IndexColumn> (or add a parallel Descending list), then include the flag in ConstraintShapeEqual and in FormatStandaloneConstraintBody. Touches the two emitters and the golden scripts.

**Verifica adversariale**

Verified. ConstraintReader.KeysQuery (src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs:13-32) selects key_ordinal and c.name but never `ic.is_descending_key`; FlushKey (:142-157) builds `new PrimaryKey(name, [..columns], isClustered)` and PrimaryKey/UniqueConstraint declare `IReadOnlyList<string> Columns` (ObjectModel/PrimaryKey.cs, UniqueConstraint.cs) — no direction anywhere. Equality is therefore direction-blind in both places: ComparisonEngine.cs:475-478 (`Columns.SequenceEqual`) and TableScriptEmitter.ConstraintShapeEqual:426-429. Emission drops it too: TableScriptEmitter.cs:440-441 `PRIMARY KEY … ({string.Join(", ", pk.Columns.Select(Bracket))})`. The contrast the reviewer draws is accurate — TableIndex key columns DO carry direction (ObjectModel/IndexColumn.cs, read at IndexReader.cs:23/67/91 and compared at ComparisonEngine.cs:541-544). So a `PRIMARY KEY CLUSTERED (Data DESC, Id)` compares Identical to `(Data, Id)` and is recreated ASC. Genuine false negative; medium is right (silent but narrow, no data loss).

**Nota**: Holds unchanged.

---

## [medium] body-resolver-fire-and-forget-race  ·  livedb-readers

**Body resolution is fire-and-forget with CancellationToken.None — a late reply can paint another object's DDL under the selected object's name**

- file: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`partial void OnSelectedRowChanged(DifferenceRowViewModel? value) { … _ = DiffViewer.LoadAsync(value, CancellationToken.None); }` (AppStateViewModel.cs:122) — no CTS, no serialization, result discarded. `DiffViewerViewModel.LoadAsync` sets `ObjectQualifiedName` first and then awaits two round-trips that each open a fresh `SqlConnection` (`await using SqlConnection connection = await ConnectionFactory.OpenAsync(connectionString, ct)`, LiveDbObjectBodyResolver.cs:33) before assigning `SourceBody`/`TargetBody`/`Rows` (DiffViewerViewModel.cs:64-71). A table body costs up to 6 queries per side.

**Scenario di fallimento**

The user arrow-keys down a result grid of 300 Different rows. Row 12's resolution (a 400-column table) completes after row 40's, so the panes show table #12's DDL while the header and the grid selection say #40; the user reviews and approves the deploy on the strength of the wrong DDL. Same mechanism when a resolution throws (permission denied on sys.sql_modules): the task's exception is unobserved, `IsLoading` resets, and the previously selected object's body stays on screen with no error shown.

**Fix proposto**

Keep a `CancellationTokenSource` field in `AppStateViewModel`; cancel the previous one on each selection change and pass its token, then in `LoadAsync` bail out (`ct.ThrowIfCancellationRequested()` before each assignment) so only the newest selection can write the panes. Add a `catch (Exception ex) { SourceBody = TargetBody = null; ErrorText = ex.Message; }` so a failed resolution never leaves stale DDL visible.

**Verifica adversariale**

Verified. AppStateViewModel.cs:115-123: `partial void OnSelectedRowChanged` ends in `_ = DiffViewer.LoadAsync(value, CancellationToken.None);` — no CTS field, no serialization, task discarded, exception unobserved. `DiffViewerViewModel.LoadAsync` (DiffViewerViewModel.cs:56-82) assigns `ObjectQualifiedName = row.QualifiedName` FIRST (line 64), then awaits two resolver round-trips (65-68) before assigning SourceBody/TargetBody/Rows/Sections (65-71), with no token check between assignments. Each resolver call opens its own `SqlConnection` (LiveDbObjectBodyResolver.cs:33) and a table costs 7 queries per side (object_id + columns + 4 constraint queries + indexes, lines 229-243), so overlapping loads with out-of-order completion are realistic while arrow-keying a large grid. Nothing serializes them; the existing tests (tests/DbDelta.App.HeadlessTests/ViewModels/DiffViewerViewModelTests.cs, 4 facts) all await a single load. The exception half also holds: LoadAsync has only `try/finally` resetting IsLoading (61-81), no catch, so a throwing resolve leaves the previous object's body on screen with no error surfaced.

**Nota**: Confirmed. Given requirement 2, the header/pane mismatch is the dangerous half — the user can approve a deploy after reviewing the wrong object's DDL. Cancel-previous CTS plus a ct check before each pane assignment is the whole fix.

---

## [medium] included-column-order-nondeterministic  ·  livedb-readers

**INCLUDE column order is not deterministically ordered, and the comparer is order-sensitive → spurious index DROP/CREATE**

- file: `src/DbDelta.Providers.LiveDb/Readers/IndexReader.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`ORDER BY i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal;` — for included columns `sys.index_columns.key_ordinal` is 0 for every row, so the sort keys are identical and the relative order of the INCLUDE list is whatever the plan happens to produce (SQL Server sorts are not stable). The comparer then requires exact sequence equality: `if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns)) { return false; }` (ComparisonEngine.cs:547) and so does `IndexShapeEqual` used by `EmitIndexDelta` (ScriptGenerator.cs:633).

**Scenario di fallimento**

`CREATE INDEX IX_Order_Cust ON dbo.OrderHeader (CustomerId) INCLUDE (Total, Status, ShipDate)` exists identically on both servers. The two servers have different table sizes/statistics, so the source scan returns Total,Status,ShipDate while the target scan returns Status,Total,ShipDate. The table is flagged Different and the deploy emits `DROP INDEX IX_Order_Cust` + `CREATE INDEX …` — a full index rebuild on a large production table for a non-existent difference, and the difference reappears on the next comparison.

**Fix proposto**

Add `ic.index_column_id` to the SELECT and make the ORDER BY `i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id` (same fix in `LiveDbObjectBodyResolver.ReadIndexesForObjectAsync`, line 584). Belt and braces: compare included columns as a set (order is not semantically meaningful in INCLUDE).

**Verifica adversariale**

Both halves verified. IndexReader.cs:37 is exactly `ORDER BY i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal;` and `ic.index_column_id` is not even in the SELECT list (26-37); for included columns key_ordinal is 0 on every row, so the ORDER BY genuinely does not determine the INCLUDE sequence and SQL Server's sort is not stable. The comparer is strictly order-sensitive in both places: `if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns)) { return false; }` (ComparisonEngine.cs:547) and `a.IncludedColumns.SequenceEqual(b.IncludedColumns, StringComparer.Ordinal)` in `IndexShapeEqual` (ScriptGenerator.cs:639), which drives `EmitIndexDelta`'s DROP+CREATE (ScriptGenerator.cs:614-629). The same missing tiebreaker exists in the resolver copy (LiveDbObjectBodyResolver.cs:584). No test pins multi-column INCLUDE order — IndexReaderTests.cs:60 asserts a single included column.

**Nota**: Confirmed as a latent determinism bug: I found no evidence it has ever fired in the wild (it needs the two servers to pick different plans for the catalog query), so treat it as cheap insurance rather than an active incident. Fix both copies; comparing INCLUDE as a set is the more honest semantic since INCLUDE order is not meaningful to SQL Server.

---

## [medium] permission-scope-gaps  ·  livedb-readers

**Permissions granted to public and permissions at TYPE scope are never read**

- file: `src/DbDelta.Providers.LiveDb/Readers/PermissionReader.cs` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`WHERE grantee.is_fixed_role = 0 AND grantee.principal_id > 4 AND p.class_desc IN ('DATABASE','SCHEMA','OBJECT_OR_COLUMN')`. The `public` role is `principal_id = 0`, so `> 4` excludes it; `class_desc = 'TYPE'` (grants on table-type UDTs, a supported object kind) is excluded by the class list.

**Scenario di fallimento**

(a) Target has `GRANT SELECT ON dbo.Salary TO public` that the source does not — the difference is invisible, so the comparison asserts the databases match while every principal on the target can read salaries. (b) Source has `GRANT EXECUTE ON TYPE::dbo.OrderItemTvp TO app_user`; the target does not. No difference row, no GRANT in the deploy → the application fails at runtime with "The EXECUTE permission was denied on the object 'OrderItemTvp'" after a deploy DbDelta called complete.

**Fix proposto**

Drop `principal_id > 4` in favour of an explicit exclusion of dbo/guest/INFORMATION_SCHEMA/sys (`principal_id NOT IN (1,2,3,4)`), keeping `public` (0); add `'TYPE'` to the class list with a `sys.types`-based name resolution branch and teach `PermissionScriptEmitter` the `ON TYPE::` syntax.

**Verifica adversariale**

Query verified verbatim at PermissionReader.cs:37-39: `WHERE grantee.is_fixed_role = 0 AND grantee.principal_id > 4 AND p.class_desc IN ('DATABASE','SCHEMA','OBJECT_OR_COLUMN')`. `public` is principal_id 0 with is_fixed_role = 0, so `> 4` excludes it; `class_desc = 'TYPE'` is excluded by the IN list even though table-type UDTs are a supported kind. `ComparePermissions` pairs on DiffKey (ComparisonEngine.cs:106-134), so a row that was never read simply produces no pair — the difference is invisible in the grid and the report regardless of options. No test covers the reader's principal/class filters (no PermissionReader test exists in tests/DbDelta.Providers.LiveDb.IntegrationTests/).

**Nota**: Confirmed, with one correction to scenario (b): `ComparisonOptions.Default` includes IgnorePermissions (Core/Options/ComparisonOptions.cs), and ScriptGenerator only emits GRANT/REVOKE when that flag is cleared (ScriptGenerator.cs:278-281) — the CLI needs --include-permissions (ScriptCommand.cs:36-47). So 'no GRANT in the deploy' is the default behaviour for ALL permissions, not a consequence of this reader gap. The real, unconditional harm is the visibility half: a `GRANT … TO public` divergence can never be reported, so DbDelta asserts the databases match while every principal on the target reads the data.

---

## [medium] resolver-table-body-omits-collation  ·  livedb-readers

**The diff viewer's table body is rebuilt by a second, less complete reader — a collation-only difference renders as an empty diff**

- file: `src/DbDelta.Providers.LiveDb/ObjectBody/LiveDbObjectBodyResolver.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`ReadSingleTableAsync`'s columnsSql does not project `c.collation_name` and builds `new Column(… isPersistedComputed: isPersistedComputed)` with no `collation:` argument (lines 267-322), whereas `TableReader.ColumnsQuery` does read it (TableReader.cs:40, 116) and the engine compares it (`if (!string.Equals(col.Collation, other.Collation, StringComparison.OrdinalIgnoreCase)) return false;`, ComparisonEngine.cs:428) and the emitter prints it (`AppendCollation`, TableScriptEmitter.cs:494). The resolver is a hand-duplicated copy of every reader query, so the two paths can drift like this silently.

**Scenario di fallimento**

`dbo.Customer.Name varchar(100) COLLATE Latin1_General_CS_AS` on source vs `Latin1_General_CI_AS` on target. The grid correctly says Different; selecting the row shows two byte-identical CREATE TABLE bodies with zero highlighted lines, so the user cannot see what changed before approving a script that emits `ALTER TABLE … ALTER COLUMN [Name] varchar(100) COLLATE Latin1_General_CS_AS NOT NULL` (an index-invalidating change on a large table). This is exactly the class of bug the round-16 comment at TableScriptEmitter.cs:120-128 was written about.

**Fix proposto**

Add `c.collation_name` to the resolver's columnsSql and pass `collation:` to the `Column` ctor. Root fix (removes the whole drift class): make `TableReader`/`ConstraintReader`/`IndexReader` accept an optional `object_id` filter and have the resolver call them instead of maintaining a second copy of six queries.

**Verifica adversariale**

Verified line by line. `ReadSingleTableAsync`'s columnsSql (LiveDbObjectBodyResolver.cs:267-291) selects 13 columns and `c.collation_name` is not among them; the `new Column(...)` at 312-322 passes no `collation:` argument, so Collation is null for every column in the viewer's model. TableReader does read it (TableReader.cs:40, 93, 116), the engine compares it (`!string.Equals(col.Collation, other.Collation, OrdinalIgnoreCase)` → false, ComparisonEngine.cs:428), and the deploy emitter prints it (`AppendCollation`, TableScriptEmitter.cs:494-498, called from FormatColumn:479 and the ALTER COLUMN path:247). Since both panes come from the same resolver, a collation-only difference yields two byte-identical `GenerateFullTableBody` outputs → `LineDiffer.Compute` produces zero highlighted rows while the grid correctly says Different. The reviewer's characterisation of the drift class is also right: the resolver hand-duplicates six reader queries (constraints in four parts at 346-566, indexes at 568-634, and its own FormatDataType at 681-689). No test covers the resolver — tests/DbDelta.Core.UnitTests/ScriptGen/ColumnCollationTests.cs exercises the Core emitter only, and nothing in tests/ references LiveDbObjectBodyResolver.

**Nota**: Confirmed. Broader than the named scenario: the viewer body omits COLLATE for *every* string column, so the preview never matches the DDL the deploy will actually run. The one-line fix closes the scenario; the 'let the resolver call the real readers with an object_id filter' root fix is the only thing that stops this drift class recurring.

---

## [medium] sequence-cast-bigint-overflow  ·  livedb-readers

**SequenceReader hard-casts sequence bounds to bigint — a decimal/numeric sequence aborts the entire catalog scan**

- file: `src/DbDelta.Providers.LiveDb/Readers/SequenceReader.cs` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`CAST(seq.start_value AS bigint), CAST(seq.increment AS bigint), CAST(seq.minimum_value AS bigint), CAST(seq.maximum_value AS bigint)` (SequenceReader.cs:19-22, duplicated at LiveDbObjectBodyResolver.cs:57-58). `Sequence` stores them as `long?`. `CREATE SEQUENCE … AS decimal(38,0)` is legal and its implicit bounds are ±(10^38-1).

**Scenario di fallimento**

The database contains `CREATE SEQUENCE dbo.GlobalId AS numeric(38,0) START WITH 1` (the documented way to get a range wider than bigint). The catalog query fails with Msg 8115 "Arithmetic overflow error converting numeric to data type bigint"; `LoadAsync`'s generic `catch (SqlException ex)` turns it into `ErrorCode.CatalogQueryFailed` with just the SQL message, so the user cannot compare that database at all and gets no hint which object caused it.

**Fix proposto**

Select the raw sql_variant values (`seq.start_value` etc.), read them with `r.GetValue(i)` and store as `decimal`, or `CAST(... AS decimal(38,0))` and widen `Sequence` to `decimal?`. Minimum fix if the model must stay long: `TRY_CAST` + treat NULL as "out of range" and flag the sequence as unsupported rather than failing the scan.

**Verifica adversariale**

Verified: SequenceReader.cs:19-22 hard-casts start_value/increment/minimum_value/maximum_value to bigint and reads them with GetInt64 (lines 42-45); `Sequence` stores long/long? (ObjectModel/Sequence.cs). Duplicated in LiveDbObjectBodyResolver.cs:57-58. `CREATE SEQUENCE … AS numeric(38,0)` is legal and its implicit bounds are ±(10^38-1), so the SELECT raises Msg 8115 and `LoadAsync`'s generic `catch (SqlException ex)` (LiveDbSource.cs:117-122) turns it into ErrorCode.CatalogQueryFailed carrying only the SQL message — no object name, and the entire comparison is impossible. Crucially this contradicts the code's own documented intent rather than being an accepted limit: Sequence.cs's doc comment says the long storage is "sufficient for … tinyint / smallint / int / bigint / **decimal(p,0)**" and only excludes floating-point base types — decimal(38,0) is exactly the case it claims to support and cannot.

**Nota**: Confirmed. Worth noting the doc comment on ObjectModel/Sequence.cs is itself wrong and should be corrected along with the fix; TRY_CAST + 'unsupported sequence' flagging is the S-sized option that keeps the model as long.

---

## [medium] comparison-options-mostly-dead  ·  redgate-parity

**14 of the 20 ComparisonOptions flags are never read — including IgnoreComments, which ships ON in Default**

- file: `src/DbDelta.Core/Options/ComparisonOptions.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

Grepping every flag against `src/` (excluding `obj/` and the enum's own file) gives 0 usages for: IgnoreWhitespace, IgnoreComments, IgnoreCollations, IgnoreFillFactor, IgnoreConstraintNames, IgnoreUserSettings, CaseSensitiveObjectDefinition, IgnoreStatistics, IgnoreTriggers, IgnoreWithElementOrder, IgnoreFileGroups, IgnoreIdentitySeed, IgnoreUsersPermissionsAndRoleMemberships, ThrowOnFileParseFailed. Only IgnorePermissions (ScriptGenerator:278), IgnoreIndexes + IgnoreKeys + ForceColumnOrder (ComparisonEngine), NoTransactions + DoNotOutputCommentHeader (ScriptGen) are honoured. `Default = IgnoreWhitespace | IgnoreComments | IgnoreFillFactor | IgnorePermissions | IgnoreStatistics` — three of those five do nothing. `ClassifyModule(Module? a, Module? b)` takes **no** options parameter at all, and `BodyNormalizer.Normalize` only collapses whitespace and strips a trailing `;` — it never strips comments.

**Scenario di fallimento**

A stored procedure is identical on both sides except that the source body carries `-- rev 2026-07-01 SB` added during a review. `ComparisonOptions.Default` claims `IgnoreComments`, but `BodyNormalizer` leaves comments in, so `ClassifyModule` reports Different and the deploy script emits a `CREATE OR ALTER PROCEDURE` for a semantically unchanged proc. Worse for collation: `col.Collation` comes from `sys.columns.collation_name` (the *effective* collation, always populated for string columns), it is compared unconditionally at ComparisonEngine.cs:428, and `IgnoreCollations` is dead — so comparing a `Latin1_General_CI_AS` database against a `SQL_Latin1_General_CP1_CI_AS` one marks **every** string column of **every** table Different, with no way to suppress it, and generates a full-table rebuild for each.

**Fix proposto**

Either implement the flags or delete them — a flag that lies is worse than a missing one. Priority order for implementing: IgnoreCollations (unblocks cross-collation compares), IgnoreConstraintNames, IgnoreComments (strip `--`/`/* */` in BodyNormalizer, guarded by the flag), IgnoreIdentitySeed, IgnoreTriggers, CaseSensitiveObjectDefinition. Thread `ComparisonOptions` into `ClassifyModule`/`CompareModules`/`CompareTriggers` (they currently drop it) and into `ColumnsEqual` for the seed/collation guards.

**Verifica adversariale**

I re-ran the count per flag over src/ excluding obj/ and the enum's own file: 0 usages for IgnoreComments, IgnoreCollations, IgnoreConstraintNames, IgnoreUserSettings, CaseSensitiveObjectDefinition, IgnoreStatistics, IgnoreTriggers, IgnoreWithElementOrder, IgnoreFileGroups, IgnoreIdentitySeed, IgnoreUsersPermissionsAndRoleMemberships, ThrowOnFileParseFailed. IgnoreWhitespace and IgnoreFillFactor show 4 hits each but ALL of them are the unrelated ProjectOptions record (Abstractions/ProjectOptions.cs, XmlProjectStore.cs:122,124,235,237) — the ComparisonOptions flags themselves are dead too, so the reviewer's 14/20 tally is right. Honoured: IgnorePermissions (ScriptGenerator.cs:278), IgnoreKeys (:355), IgnoreIndexes (:357), ForceColumnOrder (:433), NoTransactions (:131), DoNotOutputCommentHeader (:132). ClassifyModule takes no options (ComparisonEngine.cs:305), CompareModules doesn't either (:286), and BodyNormalizer.Normalize (BodyNormalizer.cs:29-37) only collapses whitespace and trims one trailing `;` — comments survive, so Default's IgnoreComments genuinely lies. Collation is compared unconditionally at :428. Tests confirm nothing: the only flags ever exercised in tests/ are Default, None, IgnoreKeys, IgnoreIndexes.

**Nota**: One consequence is wrong: a collation-only divergence does NOT cause a full-table rebuild. RequiresFullRebuild (TableScriptEmitter.cs:289-304) triggers only on IDENTITY flag/seed/increment changes; a collation-only diff takes the ALTER path at TableScriptEmitter.cs:244-249 → `ALTER TABLE … ALTER COLUMN [c] <type> COLLATE <x> NOT NULL`. Also calibrate: IgnoreWhitespace/IgnoreStatistics/IgnoreFillFactor being dead is harmless (whitespace is always normalised; statistics and fill factor are not modelled at all). Only IgnoreComments and IgnoreCollations produce a user-visible wrong result today — that is why medium, not high. Deleting the 12 unimplemented flags is the S fix; implementing the two that matter is the M one.

---

## [medium] dynamic-data-masking-invisible  ·  redgate-parity

**Dynamic Data Masking on columns is not read — an unmasked PII column compares Identical to a masked one**

- file: `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs` · effort **M** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`TableReader.ColumnsQuery` projects 15 fields (type, length, nullability, identity, default, computed, ordinal, collation) and joins only `sys.identity_columns`, `sys.default_constraints`, `sys.computed_columns`. There is no join to `sys.masked_columns` and no `is_masked` / `masking_function` column; `ObjectModel/Column.cs` has no masking member (`grep -rni masked src/ --include=*.cs` only matches the Avalonia PasswordBox). `ColumnsEqual` therefore cannot see masking. Redgate models this as `IgnoreDynamicDataMasking` (`iddm`), i.e. compared by default.

**Scenario di fallimento**

Source (hardened) `dbo.Customer.Iban nvarchar(34) MASKED WITH (FUNCTION = 'default()')`; target (production, pre-hardening) has the same column unmasked. DbDelta reports `dbo.Customer` Identical, the deploy script is empty for it, and the operator concludes production is aligned — production keeps serving full IBANs to every non-`UNMASK` reader. Same class of miss applies to `is_sparse`, `is_rowguidcol`, FILESTREAM, and the XML schema-collection binding (`xml(dbo.MySchema)` vs untyped `xml` both come back as TYPE_NAME 'xml').

**Fix proposto**

Join `sys.masked_columns` (or select `c.is_masked` + `mc.masking_function`) in `ColumnsQuery`, add `MaskingFunction` to `Column`, compare it in `ColumnsEqual`, and emit `ADD MASKED WITH (FUNCTION='…')` / `DROP MASKED` in `TableScriptEmitter`. Add `xml_collection_id` in the same pass — the query already joins the tables it needs.

**Verifica adversariale**

TableReader.cs ColumnsQuery (lines 24-51) projects exactly the 15 fields listed and joins only sys.identity_columns, sys.default_constraints, sys.computed_columns — no sys.masked_columns, no c.is_masked. ObjectModel/Column.cs has 11 members (Name…Collation) and no masking member, so ColumnsEqual (ComparisonEngine.cs:365-440) cannot see it; `grep -rni masked src/ --include=*.cs` matches only Views/Controls/PasswordBox. The false negative is real: masked vs unmasked otherwise-identical column ⇒ ClassifyTable → Identical. Same holds for the secondary items named (xml_collection_id: TYPE_NAME(user_type_id) returns 'xml' for both typed and untyped; is_sparse/is_rowguidcol/FILESTREAM absent). Downgraded to medium for consistency and calibration: this is the same unmodeled-table-attribute false-negative class the same reviewer rated medium for temporal tables (which is a considerably more widely used feature), and DDM is a niche SQL Server feature that DbDelta never claims to script. The `sicuro` framing is a consequence of the wrong report, not a leak by DbDelta itself.

**Nota**: Keep the finding, but bundle it with temporal/sparse/rowguidcol/xml-collection as one "unread column & table attributes ⇒ false Identical" item: they share a single query and a single equality function, so one pass fixes all of them and one severity applies.

---

## [medium] temporal-versioning-invisible  ·  redgate-parity

**System-versioned (temporal) table settings are not read — versioning silently never deploys**

- file: `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs` · effort **L** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`TablesQuery` selects only `s.name, t.name, t.object_id, t.modify_date` from `sys.tables`. It never reads `temporal_type`, `history_table_id`, `is_memory_optimized`, `durability`, `lock_escalation`, `is_filetable`, or change-tracking state, and `sys.periods` is not queried at all. `ObjectModel/Table.cs` has no corresponding members, so `ClassifyTable` compares only columns, constraints and indexes.

**Scenario di fallimento**

Source `dbo.Employee` is `SYSTEM_VERSIONED = ON (HISTORY_TABLE = dbo.EmployeeHistory)` with `PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)`; the target has the same column list with `ValidFrom`/`ValidTo` as plain `datetime2(7) NOT NULL` and no period, no versioning. Column data types and nullability match → `ClassifyTable` returns Identical → `dbo.Employee` reports as aligned. The history table shows up separately as "Solo provenienza" and gets created as an ordinary table, leaving the target with a stray empty table, no period, no versioning — i.e. the compliance audit trail the source has is silently absent, and the tool reported success.

**Fix proposto**

Select `t.temporal_type`, `t.history_table_id` and join `sys.periods` + the two period column ids; add `TemporalType`/`HistoryTable`/`PeriodColumns` to `Table`; include them in `ClassifyTable`. Emitting the DDL is the larger half (ALTER TABLE SET (SYSTEM_VERSIONING = OFF/ON) ordering) — as an interim step, reporting Different is far better than reporting Identical.

**Verifica adversariale**

TableReader.cs:12-22 TablesQuery selects exactly `s.name, t.name, t.object_id, t.modify_date` — no temporal_type, no history_table_id, no is_memory_optimized/durability/lock_escalation, and sys.periods is queried nowhere (`grep -rni 'temporal|sys.periods|SYSTEM_VERSION' src/ tests/` → zero hits). ObjectModel/Table.cs carries only Schema/Name/Columns/Constraints/Indexes/ModifyDate, and ClassifyTable (ComparisonEngine.cs:354-362) compares only those three collections. Column.cs has no generated_always_type either, so GENERATED ALWAYS AS ROW START/END period columns are indistinguishable from plain datetime2(7) NOT NULL — both sides compare equal ⇒ Identical. The history-table half also holds: TablesQuery filters only on is_ms_shipped = 0, so the source's history table is read as an ordinary table and emitted as an ordinary CREATE TABLE. Medium is right: a genuine false negative, but on a feature the tool never claimed and one that is visible to the user (the stray history table shows up as source-only). L is right for full DDL support; reading the catalog and reporting Different would be M.

**Nota**: Ship the interim half only: read t.temporal_type + sys.periods and report Different with a note. Emitting SYSTEM_VERSIONING = OFF/ON around the alter is the part that deserves L, and getting the ordering wrong there is far more dangerous than reporting Different.

---

## [medium] alias-udt-column-type-not-schema-qualified  ·  scriptgen-correctness

**Columns typed with an alias UDT emit an unqualified type name, which resolves against the executing user's default schema**

- file: `src/DbDelta.Core/ScriptGen/SqlTypeFormatter.cs:18` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

SqlTypeFormatter: `string bracketedName = name.StartsWith('[') || name.Contains('.') ? name : $"[{name}]";` — an alias UDT arrives without a dot, so it is emitted bare-bracketed. The name is produced by TableReader.FormatDataType (src/DbDelta.Providers.LiveDb/Readers/TableReader.cs:135-149), whose fallback is `_ => typeName` where typeName is `TYPE_NAME(c.user_type_id)` — the unqualified type name; the type's schema is never captured. UserDefinedTypeScriptEmitter does emit the type itself as `CREATE TYPE [schema].[name]`, so the schema is known elsewhere in the model but not on the column.

**Scenario di fallimento**

Source has `CREATE TYPE sales.Money FROM decimal(19,4)` and dbo.Invoice.Amount of type sales.Money; the target lacks dbo.Invoice. The deploy is executed by a login whose default schema is dbo. DbDelta emits `CREATE TABLE [dbo].[Invoice] ( [Amount] [Money] NOT NULL, … )` → Msg 2715 "Column, parameter, or variable #1: Cannot find data type Money." → whole deploy rolls back, and the error text gives no hint that the missing piece is the schema qualifier.

**Fix proposto**

Select the type's schema in TableReader's ColumnsQuery (join sys.types + sys.schemas on user_type_id) and format user-defined types as `[schema].[type]`; SqlTypeFormatter already passes dotted names through untouched.

**Verifica adversariale**

SqlTypeFormatter.cs:18 is verbatim (`name.StartsWith('[') || name.Contains('.') ? name : $"[{name}]"`). TableReader.cs:28 selects `TYPE_NAME(c.user_type_id)` — unqualified — and FormatDataType's fallback at :147 is `_ => typeName`, so a column typed `sales.Money` arrives as the bare token `Money` and is emitted `[Money]`; the type's schema is never selected in ColumnsQuery (:24-51). SQL Server resolves an unqualified type name against the caller's default schema then dbo, so a UDT in a non-dbo schema gives Msg 2715 'Cannot find data type Money' → XACT_ABORT rollback with no hint about the qualifier. Confirmed the schema IS known elsewhere (UserDefinedTypeReader.cs:14 selects s.name; UserDefinedTypeScriptEmitter.cs:18-19 emits `CREATE TYPE [schema].[name]`), so the fix is a two-column join in ColumnsQuery — S. Medium is right: it needs the alias UDT to live outside dbo, which is uncommon but entirely realistic.

---

## [medium] check-constraint-disabled-and-nfr-not-emitted  ·  scriptgen-correctness

**CHECK constraint `IsDisabled` / `IsNotForReplication` are compared but never emitted → failed deploy or a permanent false-positive diff**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:442` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ComparisonEngine.cs:488-491 makes both flags part of equality:

    CheckConstraint ck when right is CheckConstraint other =>
        BodyNormalizer.ExpressionsEqual(ck.Expression, other.Expression)
        && ck.IsDisabled == other.IsDisabled
        && ck.IsNotForReplication == other.IsNotForReplication,

but both emit paths ignore them. FormatStandaloneConstraintBody: `CheckConstraint ck => $"CHECK {ck.Expression}"`, and the inline CREATE TABLE branch (line 61-65) is the same. There is no `WITH NOCHECK`, no `NOCHECK CONSTRAINT`, no `NOT FOR REPLICATION` — unlike ForeignKeyScriptEmitter.cs:17-47, which handles the equivalent FK flags correctly.

**Scenario di fallimento**

Production dbo.OrderLine has `CK_OrderLine_Qty CHECK ([Qty] > 0)` DISABLED because 4,000 legacy rows have Qty = 0; source (dev) has the same constraint, also disabled. If the target copy is enabled (or vice-versa), the pair is Different, and EmitAlter emits `ALTER TABLE [dbo].[OrderLine] DROP CONSTRAINT [CK_OrderLine_Qty];` + `ALTER TABLE [dbo].[OrderLine] ADD CONSTRAINT [CK_OrderLine_Qty] CHECK ([Qty] > 0);`. The ADD is validated → Msg 547 on the legacy rows → whole deploy rolls back. If the data happens to conform, the constraint is silently re-created ENABLED (and without NOT FOR REPLICATION), so the very next compare still reports Different — an unfixable diff loop.

**Fix proposto**

Mirror ForeignKeyScriptEmitter: append ` NOT FOR REPLICATION` when ck.IsNotForReplication, prefix the ALTER with `WITH NOCHECK ` and append `ALTER TABLE … NOCHECK CONSTRAINT [name];` when ck.IsDisabled. Same for the inline CREATE TABLE branch (inline can only carry NOT FOR REPLICATION; disabled checks must move to a trailing ALTER).

**Verifica adversariale**

The core claim holds: ComparisonEngine.cs:488-491 makes ck.IsDisabled and ck.IsNotForReplication part of equality, ConstraintReader.cs:256-260 reads both, and neither is ever emitted — FormatStandaloneConstraintBody (TableScriptEmitter.cs:442) is `CHECK {ck.Expression}` and the inline CREATE TABLE branch (:61-65) is the same; grep NOCHECK/'NOT FOR REPLICATION' over src/ hits only ForeignKeyScriptEmitter.cs:19,38,46. BUT THE STATED FAILURE MECHANISM IS WRONG: TableScriptEmitter's own ConstraintShapeEqual (:430-431) compares CheckConstraint by Expression ONLY, so a flags-only difference gives shapeChanged=false at :208 and :269 → NO DROP CONSTRAINT and NO ADD CONSTRAINT are emitted at all. The enabled-vs-disabled scenario therefore produces an EMPTY batch, not the Msg 547 rollback described. The real reachable paths are (a) a disabled CHECK present only on the source → step 5 emits a bare `ADD CONSTRAINT … CHECK (…)` which SQL Server validates against existing target rows → Msg 547 if the data is why it was disabled; (b) any disabled/NFR CHECK gets created enabled/without NFR, so the next compare still reports Different and no generated script can ever clear it. Downgraded to medium: the dominant outcome is an unclearable diff, not a rollback.

---

## [medium] index-and-table-roundtrip-incompleteness  ·  scriptgen-correctness

**A recreated index/table loses FILLFACTOR, DATA_COMPRESSION, filegroup and disabled state, and columnstore/XML/spatial indexes are never emitted at all**

- file: `src/DbDelta.Core/ScriptGen/IndexScriptEmitter.cs:12` · effort **L** · requisito **parity** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

IndexScriptEmitter.EmitCreate builds only `CREATE [UNIQUE] CLUSTERED|NONCLUSTERED INDEX [n] ON [s].[t] (keys) [INCLUDE (…)] [WHERE filter];` — no WITH clause at all. TableIndex (ObjectModel/TableIndex.cs) has exactly six members (Name, IsUnique, IsClustered, FilterExpression, KeyColumns, IncludedColumns), so FILLFACTOR / PAD_INDEX / DATA_COMPRESSION / IGNORE_DUP_KEY / ALLOW_*_LOCKS / filegroup / is_disabled are not modelled. IndexReader.cs restricts to rowstore: `AND i.type IN (1, 2)` — clustered columnstore (5), nonclustered columnstore (6), XML (3) and spatial (4) indexes are never read. On the table side, SPARSE, ROWGUIDCOL, MASKED WITH, XML schema collections, FILESTREAM, temporal (SYSTEM_VERSIONING) and memory-optimized options have no model representation either.

**Scenario di fallimento**

Production dbo.FactSales carries a clustered columnstore index CCI_FactSales and IX_FactSales_Date WITH (FILLFACTOR = 80, DATA_COMPRESSION = PAGE). The table is rebuilt on a new environment from the DbDelta script: the columnstore index is never mentioned (queries go from seconds to minutes) and IX_FactSales_Date is created uncompressed at fillfactor 0 (storage grows ~3x). A subsequent DbDelta compare of the two databases reports them Identical, so the divergence is invisible — a false negative against the AFFIDABILE requirement.

**Fix proposto**

Extend TableIndex with FillFactor, IsPadded, DataCompression, IsDisabled, FileGroup and IndexType; widen the IndexReader predicate to include types 3-6 with per-type emission (columnstore/XML/spatial have their own CREATE syntax); include the new members in ComparisonEngine.IndexesEqual and IndexShapeEqual so at minimum the divergence is *reported* even before it is emitted.

**Verifica adversariale**

All three legs check out. IndexScriptEmitter.cs:12-42 builds `CREATE [UNIQUE] CLUSTERED|NONCLUSTERED INDEX [n] ON [s].[t] (keys) [INCLUDE (…)] [WHERE …];` with no WITH clause and no filegroup. ObjectModel/TableIndex.cs is a 6-member record (Name, IsUnique, IsClustered, FilterExpression, KeyColumns, IncludedColumns) — FILLFACTOR / PAD_INDEX / DATA_COMPRESSION / IGNORE_DUP_KEY / ALLOW_*_LOCKS / is_disabled / filegroup are simply not modelled, so ComparisonEngine.IndexesEqual and ScriptGenerator.IndexShapeEqual (:633-639) cannot see them either — two databases differing only in a columnstore index or DATA_COMPRESSION compare Identical, which is the false negative claimed. IndexReader.cs restricts to `i.type IN (1, 2)` exactly as quoted, so columnstore (5/6), XML (3) and spatial (4) are never read. Minor correction: filtered indexes ARE supported (FilterExpression is read and emitted at :35-38), so only the columnstore half of the BACKLOG:69 'filtered/columnstore indexes' note is missing. Partially acknowledged in docs/BACKLOG.md:69 as a parity-scenario gap, which is why medium/L is right rather than high.

---

## [medium] table-type-udt-loses-keys-identity-defaults  ·  scriptgen-correctness

**CREATE TYPE … AS TABLE reproduces only name/type/null/collation — PK, UNIQUE, CHECK, DEFAULT and IDENTITY are dropped, and the compare cannot see the loss**

- file: `src/DbDelta.Core/ScriptGen/TableTypeUdtScriptEmitter.cs:13` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

    sb.Append("    [").Append(col.Name).Append("] ").Append(SqlTypeFormatter.FormatColumnType(col.DataType));
    AppendCollation(sb, col);
    sb.Append(col.IsNullable ? " NULL" : " NOT NULL");

That is the entire column rendering — Column.IsIdentity, Column.DefaultExpression, Column.ComputedExpression are ignored even though the model carries them, and TableTypeUdt (ObjectModel/TableTypeUdt.cs) has no constraint collection at all (documented as "out of scope"). ComparisonEngine.TableTypeUdtsEqual (line 253-268) compares only Name/DataType/IsNullable/Ordinal/Collation, so the missing pieces never surface as a difference either.

**Scenario di fallimento**

Source has `CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL PRIMARY KEY)`, used as a TVP by dbo.usp_BulkUpsert whose MERGE relies on Id being unique. Target lacks the type. DbDelta emits `CREATE TYPE [dbo].[IdList] AS TABLE ( [Id] [int] NOT NULL );` — no PK. usp_BulkUpsert then fails at runtime with "The MERGE statement attempted to UPDATE or DELETE the same row more than once" whenever the caller passes a duplicate, and a re-compare reports the type Identical, so the operator has no way to discover the divergence from DbDelta.

**Fix proposto**

Extend TableTypeUdt with a Constraints list (TableTypeUdtReader can read sys.key_constraints / sys.check_constraints for the type's underlying table object id), include it in TableTypeUdtsEqual, and emit inline PRIMARY KEY / UNIQUE / CHECK plus column DEFAULT and IDENTITY in the emitter.

**Verifica adversariale**

Emitter verified: TableTypeUdtScriptEmitter.cs:18-29 renders name + type + collation + nullability and nothing else; ObjectModel/TableTypeUdt.cs has only (Schema, Name, Columns) — no constraint collection; ComparisonEngine.cs:253-268 compares Name/DataType/IsNullable/Ordinal/Collation only, so a type that loses its PK re-compares as Identical (a genuine false negative against AFFIDABILE). One correction to the evidence: the emitter is not 'ignoring model data the reader captured' — TableTypeUdtReader.cs:28-42 selects only name/type/max_length/precision/scale/is_nullable/column_id/collation_name and constructs Column with just those five args, so IsIdentity/DefaultExpression/ComputedExpression are always the record defaults for table types; the gap is in the reader + model, not the emitter. Also note the scope decision is explicitly documented in ObjectModel/TableTypeUdt.cs:11-14 ('PK / unique / check constraints … are out of scope (rare in practice and easy to add later)'), so this is an acknowledged v1 limitation rather than an unnoticed bug — medium is the right severity.

---

## [medium] connection-strings-built-by-raw-interpolation  ·  sql-injection-security

**Connection strings are built by string interpolation with no value quoting - a password containing ';' bricks the app and leaks a fragment on screen**

- file: `src/DbDelta.App.Avalonia/ViewModels/ProjectSetupViewModel.cs:316` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

Four independent hand-rolled builders, none of which quotes values:
ProjectSetupViewModel.cs:311-318  $"Server={p.ServerName};Database={p.DatabaseName};" + ... + $";User Id={p.UserName};Password={p.Password}"
ProjectEndpointPanelViewModel.cs:455-459  same shape
ConnectionEditViewModel.cs:177  $"Server={ServerName};{db}User Id={UserName};Password={Password};TrustServerCertificate=True"
ConnectionStoreViewModel.cs:102  entry.ConnectionStringTemplate.Replace("{PASSWORD}", password, StringComparison.Ordinal)
SqlConnectionStringBuilder would quote a value containing ';' as Password="a;b"; none of these do. Then the value is fed to a strict parser, e.g. AppStateViewModel.cs:160 and ConnectionTester.cs:17.

**Scenario di fallimento**

SQL login password is Str0ng;P@ss (semicolons are legal in SQL Server passwords and common in generated ones). ProjectSetupViewModel produces "Server=PROD01;Database=App;Encrypt=False;TrustServerCertificate=True;User Id=sa;Password=Str0ng;P@ss". Every consumer then fails: SqlConnectionStringBuilder throws "Keyword not supported: 'p@ss'", so Connetti, the database list, the compare and the deploy are all impossible and the user cannot tell why (the app never says "your password needs quoting"). Worse, the main-window header binds through Converters.RedactConnectionString (MainWindow.axaml:456) which applies the [^;]+ regex and therefore renders "...;Password=***;P@ss" - i.e. the redaction control displays half of the password it exists to hide, on screen, for the rest of the session.

**Fix proposto**

Replace all four builders with one shared factory that populates a SqlConnectionStringBuilder (DataSource / InitialCatalog / UserID / Password / Encrypt / TrustServerCertificate / IntegratedSecurity) and returns .ConnectionString - it quotes and escapes values correctly and kills four copies of the same interpolation. For the {PASSWORD} template path, parse the template into a builder and set builder.Password instead of doing a textual Replace.

**Verifica adversariale**

All four hand-rolled builders read as quoted (ProjectSetupViewModel.cs:311-318, ProjectEndpointPanelViewModel.cs:447-461, ConnectionEditViewModel.cs:177, ConnectionStoreViewModel.cs:102) and none quotes values. The on-screen leak is reachable end to end, which is the part that matters: MainWindow.axaml:456/463 bind AppState.Source/TargetConnectionString through Converters.cs:60-61 -> ConnectionStringRedactor.Redact, whose `[^;]+` pattern (ConnectionStringRedactor.cs:13) stops at the injected `;`, so `Password=Str0ng;P@ss` renders as `Password=***;P@ss`. And the user CAN get there: the OK button is gated only on ProjectSetupViewModel.cs:175 IsValid = non-empty server/database/user/password, and DatabaseName is free-text (ProjectSetupDialog.axaml:177/191), so no successful connection is required to reach the main shell. One correction: the app is not silent — ProjectEndpointPanelViewModel.cs:357-358 surfaces `Errore: Keyword not supported: 'p@ss'.` (Redact cannot match that shape), so the user gets a cryptic message that is itself the same password fragment. Severity medium is right: no wrong deploy, but a visible plaintext fragment plus an unusable app for a legal password.

**Nota**: Correct, but the app does show an error (ProjectEndpointPanelViewModel.cs:358) — which is itself another copy of the leak, so the single SqlConnectionStringBuilder factory fixes both.

---

## [medium] sp-rename-single-quote-injection  ·  sql-injection-security

**sp_rename in the identity-rebuild path pastes object names into SQL string literals without doubling the single quote**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:372` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

EmitRebuild:
        sb.Append("EXEC sp_rename '").Append(qualifiedTmp).Append("', '")
          .Append(newT.Name).AppendLine("';");
where qualifiedTmp = $"[{newT.Schema}].[{newT.Name}_tmp]" (line 316). Neither value is escaped. The codebase already knows the correct pattern - DeploymentScriptWriter.cs:34 does
        sb.Append("PRINT N'").Append(label.Replace("'", "''", StringComparison.Ordinal))
for the PRINT phase label, so the escaping helper exists 300 lines away and is simply not used here. Reached whenever TableScriptEmitter.RequiresFullRebuild is true (identity flag / seed / increment change on an existing column) - ScriptGenerator.cs:74.

**Scenario di fallimento**

Source table dbo.[O'Brien] gains IDENTITY on its Id column (or its seed changes). The emitted rebuild block ends with:
  EXEC sp_rename '[dbo].[O'Brien_tmp]', 'O'Brien';
The literal terminates at the apostrophe: the batch is a syntax error AFTER "DROP TABLE [dbo].[O'Brien];" has already been emitted in the same script. With the app's own transaction wrapper this rolls back, but ApplyCommand runs with useOwnTransaction:false (ApplyCommand.cs:67) relying on the script's envelope - and the envelope's ROLLBACK is in the final verdict batch, which a mid-script parse failure of a later batch does still reach, so the practical result is a failed deploy that leaves the operator staring at a DROP TABLE they cannot explain. The crafted variant (a 128-char table name containing `', 'x', 'COLUMN'--`) turns the rename into an attacker-chosen sp_rename call on the production target.

**Fix proposto**

Escape both literals: .Append(qualifiedTmp.Replace("'", "''", StringComparison.Ordinal)) and the same for newT.Name. Since DeploymentScriptWriter needs the identical operation, promote it to a shared `SqlLiteral.Escape(string)` helper next to the identifier-quoting helper from the previous finding. Add a golden test for a table named `it's` with an identity change.

**Verifica adversariale**

Code is exactly as quoted: TableScriptEmitter.cs:372-373 `sb.Append("EXEC sp_rename '").Append(qualifiedTmp).Append("', '").Append(newT.Name)` with qualifiedTmp built at :316, neither escaped, while DeploymentScriptWriter.cs:34 does `label.Replace("'", "''", ...)` for the PRINT label — and DeploymentScriptWriterTests.cs:33 even tests it with `"Creating [dbo].[O'Brien]"`, so the project knows the operation. Reachability confirmed: RequiresFullRebuild is consulted at ScriptGenerator.cs:74 for Different table pairs. No test covers an apostrophe in a rebuilt table name. BUT the consequence claims are wrong and I am downgrading severity accordingly: (a) NO data loss. EmitRebuild returns ONE body -> ONE WriteBatch -> one GO-delimited batch, and SqlExecutor.cs:84-89 sends each batch as its own SqlCommand, so a parse error means nothing in that batch runs — the `DROP TABLE [dbo].[O'Brien];` at :371 is never executed. Earlier batches ran inside the script's own BEGIN TRANSACTION (ScriptGenerator.cs:131-135, useTransaction defaults on) and are rolled back when `await using SqlConnection` disposes. (b) The crafted-injection variant is much weaker than stated: the rebuild path only fires for Status=Different, i.e. the maliciously-named table must ALREADY exist on the target, which the source-only attacker cannot arrange — and if it existed only on the source it would take the EmitCreate path, i.e. the previous finding's vector. Net real impact: any table whose name contains an apostrophe and needs an identity rebuild produces a script that fails to compile with an opaque "Incorrect syntax near" — a reliability defect, not a data-loss or escalation one.

**Nota**: Bug is real and the one-line fix is right, but drop the data-loss framing: batch-level parse failure means the DROP TABLE never executes and the script's transaction is rolled back on connection dispose. Severity medium (broken deploy for names with apostrophes).

---

## [medium] unencrypted-untrusted-tds-by-default  ·  sql-injection-security

**Default connection is Encrypt=False + TrustServerCertificate=True - the DB link is unencrypted and never certificate-validated**

- file: `src/DbDelta.App.Avalonia/ViewModels/ProjectEndpointPanelViewModel.cs:49` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ProjectEndpointPanelViewModel.cs:49-50 (the panel that backs the startup ProjectSetupDialog, i.e. the only shipped connection UI):
    [ObservableProperty] private bool _encrypt;                        // defaults to false
    [ObservableProperty] private bool _trustServerCertificate = true;  // defaults to true
Both are pasted straight into every connection string:
ProjectEndpointPanelViewModel.cs:458  $"Server={ServerName};{db}User Id={UserName};Password={Password};" + $"Encrypt={Encrypt};TrustServerCertificate={TrustServerCertificate}"
ProjectSetupViewModel.cs:311-312, App.axaml.cs:130/138 - same shape.
The other three connection templates hard-code the unsafe value with no toggle at all:
ConnectionEditViewModel.cs:177/182, ConnectionPickerSlot.cs:123, ConnectionManagerDialog.axaml.cs:27 - all end with ";TrustServerCertificate=True" and never mention Encrypt.
Microsoft.Data.SqlClient 4.0+ defaults Encrypt to Mandatory; this code explicitly overrides that back to False.

**Scenario di fallimento**

Default flow: operator opens DbDelta, picks PROD01 in the picker, connects. The TDS session carries Encrypt=False, so the complete production schema (every table, every stored-procedure body, every index filter) and then the generated ALTER/DROP script cross the network in cleartext. An attacker on the path (same VLAN, compromised switch, rogue DHCP/ARP) can (a) read the whole schema passively, and (b) act as a TDS proxy: because the pre-login handshake certificate is never validated - and is not validated even when the user ticks Encrypt, since TrustServerCertificate stays True - the proxy can terminate the login, capture the sa credentials, and rewrite the DDL batches the operator believes they approved before forwarding them to the real server. The user has no indication: the header strip shows only the redacted connection string.

**Fix proposto**

Flip the defaults to _encrypt = true and _trustServerCertificate = false, and add the Encrypt/Trust pair to the three hard-coded templates. Keep the TrustServerCertificate checkbox but render an inline warning next to it (the ProjectSetupDialog already has the checkbox at ProjectSetupDialog.axaml:160/276) so opting into a self-signed on-prem cert is a deliberate, visible act rather than the silent default. Persist the choice per connection - ProjectAuthentication already round-trips both flags through XmlProjectStore.cs:193-194.

**Verifica adversariale**

Defaults verified: ProjectEndpointPanelViewModel.cs:49 `private bool _encrypt;` (false) and :50 `_trustServerCertificate = true`, pasted into every string at :456/:459, ProjectSetupViewModel.cs:312, App.axaml.cs:130/138. The three other templates hard-code `TrustServerCertificate=True` with no Encrypt at all: ConnectionEditViewModel.cs:177/182, ConnectionPickerSlot.cs:123, ConnectionManagerDialog.axaml.cs:27. Directory.Packages.props:6 pins Microsoft.Data.SqlClient 6.0.1, whose default is Encrypt=Mandatory, so the code does explicitly override a secure default. Two corrections that lower the severity: (1) the credential-capture half of the scenario is caused by TrustServerCertificate=True, not by Encrypt=False — TDS always encrypts the login packet even when session encryption is off, so it is the missing certificate validation that lets a proxy read `sa`; the schema/DDL-in-cleartext half is correct as written. (2) The toggles are not hidden — ProjectSetupDialog.axaml:158/160 and :274/276 bind Encrypt and TrustServerCertificate as checkboxes in the only shipped connection dialog, and XmlProjectStore.cs:193-194/327-328 round-trips both, so this is a bad default with a visible opt-out rather than an unfixable posture, and it needs an active on-path attacker. Medium.

**Nota**: Flip the defaults; the checkboxes already exist at ProjectSetupDialog.axaml:158/160+274/276. Note the sa-capture depends on TrustServerCertificate=True, not Encrypt=False.

---

## [medium] apply-has-no-transaction  ·  tests-cicd-arch

**`dbdelta apply` executes with useOwnTransaction:false — an arbitrary script gets no transaction, and the test claiming otherwise does not test it**

- file: `src/DbDelta.Cli/Commands/ApplyCommand.cs` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ApplyCommand's XML doc says 'execute it against the target server inside a single GO-split transaction via SqlExecutor', but the call is `await SqlExecutor.ExecuteAsync(tgtConn, script, ct, useOwnTransaction: false)`. In SqlExecutor that sets `tx = null`, so each GO batch runs in autocommit (`SqlExecutor.cs:78-91`) with no rollback path — the `catch` block's `if (tx is not null)` rollback is dead. The guarding test is `tests/DbDelta.Cli.AcceptanceTests/ApplyCommandTests.cs:33` `Applies_script_inside_a_transaction_and_target_picks_up_the_change`, whose script is literally `"CREATE TABLE dbo.AppliedByCli (Id int NOT NULL);\nGO\n"` — no BEGIN TRANSACTION, no failure injected, and the only assertion is `ObjectExistsAsync(...).Should().BeTrue()`. The test name asserts atomicity that the test never exercises.

**Scenario di fallimento**

An operator hand-edits a generated script (or writes one), stripping/never having the `SET XACT_ABORT ON … BEGIN TRANSACTION` envelope — e.g. five DDL batches split by GO. `dbdelta apply --target prod --script deploy.sql`: batches 1-2 (a DROP COLUMN and an ALTER TABLE) commit, batch 3 fails on a duplicate index name. Output is `{"success":false,"batchesExecuted":2}`, exit 40, and the target is left half-migrated with the dropped column's data gone and no rollback and no reverse script. Directly defeats requirement 3 (undo).

**Fix proposto**

Either (a) default `apply` to `useOwnTransaction: true` and add `--no-transaction` for scripts that manage their own (the generated envelope), or (b) detect the envelope — `script.Contains("BEGIN TRANSACTION", Ordinal)` — and own the transaction when absent. Then fix the test: rename it, and add a case with a 3-batch no-envelope script whose middle batch fails, asserting the first batch's object does NOT exist afterwards.

**Verifica adversariale**

src/DbDelta.Cli/Commands/ApplyCommand.cs:67 is `SqlExecutor.ExecuteAsync(tgtConn, script, ct, useOwnTransaction: false)` while its own XML doc at :8-11 says 'execute it against the target server inside a single GO-split transaction via SqlExecutor' — a direct contradiction. In src/DbDelta.Persistence/Sql/SqlExecutor.cs:78-80, useOwnTransaction:false sets `tx = null`, so :86-88 builds every SqlCommand without a transaction (autocommit per GO batch) and the rollback at :98-101 is guarded by `if (tx is not null)` — dead on this path. The guarding test is exactly as described: tests/DbDelta.Cli.AcceptanceTests/ApplyCommandTests.cs:30-47, named `Applies_script_inside_a_transaction_and_target_picks_up_the_change`, feeds `"CREATE TABLE dbo.AppliedByCli (Id int NOT NULL);\nGO\n"` — a single batch, no envelope, no injected failure — and asserts only ObjectExistsAsync(...).Should().BeTrue(). The test name asserts atomicity the test never exercises.

**Nota**: Severity lowered from high to medium because the default paths are safe and the reviewer did not check them: every DbDelta-generated script carries its own envelope (DeploymentScriptWriter.cs:20-26 emits `SET XACT_ABORT ON` + `BEGIN TRANSACTION`, suppressed only by ComparisonOptions.NoTransactions, which grep shows is set by NOTHING in src/ and is not exposed by any CLI flag or UI control), and the UI's execute path always builds through DeployScriptBuilder (MainWindowViewModel.cs:630-647), so useOwnTransaction:false there is correct and deliberate — documented at SqlExecutor.cs:36-43. Exposure is limited to `apply` on a hand-written or hand-edited script. Even then, a mid-script failure disposes the connection and SQL Server implicitly rolls back any open transaction, so an enveloped script is still atomic; only an envelope-less one half-commits. The fix and the test correction stand, and the lying XML doc + lying test name are the confirmed core.

---

## [medium] ci-skips-two-test-projects  ·  tests-cicd-arch

**CI never runs DbDelta.Property.Tests or DbDelta.Shared.UnitTests — the false-positive safety net is not a gate**

- file: `.github/workflows/ci.yml` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

windows-build enumerates test projects by name: `dotnet test tests/DbDelta.Core.UnitTests` / `.Architecture.Tests` / `.ScriptGen.GoldenTests` / `.Persistence.UnitTests` / `.Persistence.IntegrationTests` / `.App.HeadlessTests`. linux-integration-tests runs only `tests/DbDelta.Providers.LiveDb.IntegrationTests` and `tests/DbDelta.Cli.AcceptanceTests`. DbDelta.sln contains 11 test projects (grep 'Project(' DbDelta.sln) — `DbDelta.Property.Tests` (12 facts) and `DbDelta.Shared.UnitTests` (4 facts) appear in NO job. They are built (`dotnet build --configuration Release` at root) but never executed.

**Scenario di fallimento**

A contributor edits `ComparisonEngine.ClassifyModule` and breaks the reflexive invariant. `ComparisonEngineProperties.Compare_With_Itself_Is_Identical` (100 sampled schemas) and `Side_Flip_Is_Antisymmetric` would fail locally under `dotnet test DbDelta.sln`, but CI is green because those tests are never invoked → a false-positive-diff regression merges to main. Same for `ScriptGeneratorProperties.Script_Has_Transaction_Wrapper`: a change dropping `BEGIN TRANSACTION` from the envelope (the only thing making a deploy atomic) passes the PR gate.

**Fix proposto**

Replace the enumerated list with `dotnet test DbDelta.sln --no-build -c Release --filter "Category!=Compat"` and tag the Docker-requiring classes `[Trait("Category","Compat")]`/`RequiresDocker`. That makes any newly added test project automatically part of the gate — the current list is a maintenance trap, not a policy.

**Verifica adversariale**

Verified .github/workflows/ci.yml:46-53 — windows-build enumerates exactly six projects (Core.UnitTests, Architecture.Tests, ScriptGen.GoldenTests, Persistence.UnitTests, Persistence.IntegrationTests, App.HeadlessTests) and linux-integration-tests (ci.yml:88-91) runs only Providers.LiveDb.IntegrationTests + Cli.AcceptanceTests; nightly-compat-matrix (ci.yml:100-129) is schedule-gated and runs only Compat.Tests. DbDelta.sln lines 44 and 38 declare DbDelta.Property.Tests and DbDelta.Shared.UnitTests — neither name appears anywhere in ci.yml, so they are compiled by `dotnet build` (ci.yml:44) and never executed. Counted 12 [Fact] in tests/DbDelta.Property.Tests and 4 in tests/DbDelta.Shared.UnitTests, matching the claim. Confirmed also that the reflexive-identity invariant exists ONLY there: tests/DbDelta.Property.Tests/Properties/ComparisonEngineProperties.cs:29 (Compare_With_Itself_Is_Identical) and :83 (Side_Flip_Is_Antisymmetric), and a grep for reflexive/self-compare over Core.UnitTests + GoldenTests + HeadlessTests returns nothing.

**Nota**: Half the failure scenario is REFUTED: 'a change dropping BEGIN TRANSACTION from the envelope passes the PR gate' is false. tests/DbDelta.Core.UnitTests/ScriptGen/DeploymentScriptWriterTests.cs:45,47 assert `SET XACT_ABORT ON` and `BEGIN TRANSACTION`, DeployScriptBuilderTests.cs:77,121 assert the same, and Cli.AcceptanceTests/ScriptCommandTests.cs:29,50 assert `BEGIN TRANSACTION` in the emitted script — all three projects DO run in CI. Only the FsCheck sampled-schema reflexive/antisymmetric invariants and the 4 JsonReportGenerator facts are genuinely ungated. Severity lowered to medium: real gate hole, but no defect ships from it today and the highest-value invariant it protects (envelope) is redundantly covered. The proposed fix (`dotnet test DbDelta.sln --filter Category!=Compat`) is right, and Compat.Tests already carries [Trait("Category","Compat")] (CompatMatrixTests.cs:32), so most of the plumbing exists.

---

## [medium] error-taxonomy-mostly-dead  ·  tests-cicd-arch

**3 of 17 ErrorCodes are ever produced; exit codes 11/30/31 are unreachable and the UI discards Code + Remediation and the failing endpoint**

- file: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`grep -rn 'ErrorCode\.' src/ | grep -v CliErrorMapper` yields exactly three producers, all in LiveDbSource: `AuthFailed` (SQL 4060/18456), `CannotConnect` (53/-2) and `CatalogQueryFailed` (everything else). `InsufficientPermissions`, `UnsupportedSqlServerVersion`, `EncryptedObjectUnreadable`, `NoComparableObjects`, `UnresolvableDependencyCycle`, `DataPreservationImpossible`, `UnsupportedSchemaChange`, `BatchExecutionFailed`, `TransactionAborted`, `CancelledByUser`, `ProjectFileCorrupt`, `ProjectFileVersionUnsupported`, `InternalError` are never constructed, so `ExitCodes.InsufficientPermissions` (11), `ScriptGenerationFailure` (30) and `UnresolvableDependencyCycle` (31) can never be returned. In the UI, `AppStateViewModel.CompareAsync` does `LastError = srcRes.Error!.Message` (lines 187 and 194) — `Error.Code` and the carefully written `Error.Remediation` string are both thrown away, and because both branches assign the same property the message never says whether source or target failed.

**Scenario di fallimento**

A pipeline runs `dbdelta compare` with a service login that has CONNECT but not VIEW DEFINITION / SELECT on the catalog views. SQL Server raises 229 ('The SELECT permission was denied on the object …') → the generic `catch (SqlException)` → `CatalogQueryFailed` → exit **20**, the same code returned for 'unsupported SQL Server version' and 'no comparable objects'. The pipeline's `if exit == 11: notify DBA to grant permissions` never fires; the operator sees a schema-read failure and starts debugging the tool. In the desktop app the same user sees only the raw SQL text with no remediation hint and no indication which of the two endpoints was refused.

**Fix proposto**

Add one more filtered catch in LiveDbSource — `catch (SqlException ex) when (ex.Number is 229 or 230 or 262 or 300) => InsufficientPermissions` — and prefix the message with the source's `DisplayName` (already 'source'/'target' at both call sites) so the error names the endpoint. In `AppStateViewModel`, set `LastError = $"[{side}] {err.Code}: {err.Message}" + (err.Remediation is null ? "" : "\n" + err.Remediation)`. Delete or implement the remaining dead ErrorCodes so the documented §4.3 exit-code table stops being partly fiction.

**Verifica adversariale**

Every count checks out. `grep -rn 'ErrorCode\.' src/ | grep -v CliErrorMapper` returns exactly three hits, all in src/DbDelta.Providers.LiveDb/LiveDbSource.cs:106 (AuthFailed, filtered on ex.Number 4060/18456), :113 (CannotConnect, 53/-2) and :120 (CatalogQueryFailed, unfiltered fallback); `grep -rn 'new Error(' src/` returns only :105/:112/:119 — the entire product constructs three Errors. src/DbDelta.Core/Abstractions/Result.cs:6-25 declares 17 ErrorCodes, so the 13 listed as never produced are correct, and via CliErrorMapper.MapErrorToExitCode (CliErrorMapper.cs:19-46) ExitCodes.InsufficientPermissions (11), ScriptGenerationFailure (30) and UnresolvableDependencyCycle (31) are unreachable. The UI claim is exact, including line numbers: AppStateViewModel.cs:187 `LastError = srcRes.Error!.Message;` and :194 `LastError = tgtRes.Error!.Message;` — Code and Remediation discarded, both branches writing the same property so the message never names the endpoint, even though the sources are constructed with DisplayName 'source'/'target' at :181-182.

**Nota**: Confirmed as stated. Worth noting the CatalogQueryFailed construction at LiveDbSource.cs:119-121 is the only Error built with no Remediation argument at all, so even a UI that rendered Remediation would show nothing for the most common failure — the InsufficientPermissions catch filter should carry one. Also relevant to the sequence-overflow finding: error 8115 lands in the same unfiltered catch, which is why that failure surfaces as a bare arithmetic-overflow string naming no object.

---

## [medium] hand-rolled-error-json-invalid  ·  tests-cicd-arch

**CLI error output is hand-concatenated JSON that only escapes quotes — real SQL Server messages and Windows paths produce unparseable JSON**

- file: `src/DbDelta.Cli/CliErrorMapper.cs` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```
string msg = error.Message.Replace("\"", "\\\"");
string rem = (error.Remediation ?? string.Empty).Replace("\"", "\\\"");
Console.Error.WriteLine($"{{\"code\":\"{error.Code}\",\"message\":\"{msg}\",\"remediation\":\"{rem}\"}}");
```
Only `"` is escaped — newlines, backslashes and control chars pass through raw. The same pattern is duplicated in `src/DbDelta.Cli/Commands/ApplyCommand.cs:46` (`"Script file not found: {path.Replace("\"", "\\\"")}"`). `Error.Message` is always a raw `SqlException.Message`, which SqlClient builds by joining every row of `SqlException.Errors` with newlines. No test asserts stderr is valid JSON — the acceptance tests only assert exit codes.

**Scenario di fallimento**

`dbdelta compare --source "Server=HOST\\SQLEXPRESS;Database=Missing;..." --target ... --format json` against a database the login cannot open: SqlException.Message is two lines ('Cannot open database "Missing" requested by the login. The login failed.' + 'Login failed for user \'svc\'.'). The emitted stderr line contains a raw newline inside a JSON string → `JSON.parse` throws → the CI wrapper that reads `.code` to decide whether to page the DBA crashes instead. `dbdelta apply --script C:\temp\missing.sql` emits `"message":"Script file not found: C:\temp\missing.sql"` — `\t` silently becomes a TAB, so even a lenient parser reports the wrong path.

**Fix proposto**

One line each: `Console.Error.WriteLine(JsonSerializer.Serialize(new { code = error.Code.ToString(), message = error.Message, remediation = error.Remediation ?? "" }));` — System.Text.Json is already in the CLI (ApplyCommand uses it two methods down). Add one acceptance assertion that `JsonDocument.Parse(stderr)` succeeds on a forced auth failure.

**Verifica adversariale**

src/DbDelta.Cli/CliErrorMapper.cs:14-16 is verbatim as quoted — `error.Message.Replace("\"", "\\\"")` then string-interpolated into a hand-built JSON object. Only the double quote is escaped; raw newlines (invalid inside a JSON string per RFC 8259), backslashes and control characters pass through untouched. Duplicated at src/DbDelta.Cli/Commands/ApplyCommand.cs:47-48 with the same single-character escape. The message is always a raw SqlException.Message: all three Error constructions in src/ are LiveDbSource.cs:105/112/119 and each passes `ex.Message` directly, and SqlClient joins multi-row SqlException.Errors with newlines — so the multi-line 'Cannot open database…' + 'Login failed for user…' case is exactly what reaches this formatter. Confirmed no test guards it: tests/DbDelta.Cli.AcceptanceTests/CliRunner.cs:44 sets RedirectStandardError = true but the only JsonDocument.Parse in that project is ReportCommandTests.cs:53, against the report file on stdout, not stderr. System.Text.Json is already imported in ApplyCommand.cs:2, so the one-line fix is available.

**Nota**: Severity lowered to medium: this is an error-path wire-contract bug — it corrupts machine-readable diagnostics, but no credential leaks (ConnectionStringRedactor is not in this path, though note SqlException messages here are emitted unredacted — separate issue) and no wrong DDL. Note the backslash case is worse than described: `C:\temp\missing.sql` yields `\t` (silently becomes TAB for lenient parsers) AND `\m`, which is an *invalid* escape that makes strict parsers throw, so the path is both wrong and unparseable.

---

## [medium] project-comparison-options-ignored  ·  tests-cicd-arch

**A project file's comparison configuration (ProjectOptions, Options, Owner/Table mappings) is persisted and unit-tested but never reaches ComparisonEngine**

- file: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

Every Compare call site hardcodes the default: `engine.Compare(srcRes.Value!, tgtRes.Value!, ComparisonOptions.Default)` (AppStateViewModel:199), `CompareCommand.cs:66`, `ReportCommand.cs:79`, `ScriptCommand.cs:80`. Meanwhile `XmlProjectStore` writes and parses `ProjectOptions` (5 flags), the legacy `Options` bitmap (`legacyOptions = (ComparisonOptions)optInt`, XmlProjectStore.cs:268), `OwnerMappings` and `TableMappings`, and `tests/DbDelta.Persistence.UnitTests/Xml/XmlProjectStoreTests.cs` asserts `back.Options.Should().Be(project.Options)` and `back.ProjectOptions.Should().Be(project.ProjectOptions)`. `grep -rn 'OwnerMapping|TableMapping' src/` shows the only consumers are the setup view-model and the XML store — nothing in Core reads them. Separately, only 6 of the 20 `ComparisonOptions` flags are ever read (`grep -rn HasFlag src/`): IgnoreKeys, IgnoreIndexes, ForceColumnOrder, NoTransactions, DoNotOutputCommentHeader, IgnorePermissions. IgnoreWhitespace, IgnoreComments, IgnoreCollations, CaseSensitiveObjectDefinition, IgnoreTriggers, IgnoreConstraintNames, IgnoreIdentitySeed and 7 more are silent no-ops.

**Scenario di fallimento**

A user opens a `.dbd` whose `<Options>` element carries `IgnoreIndexes` (round-tripped and asserted by the persistence tests, and reachable from the v1-legacy import path `ParseV1Legacy`). The compare still evaluates indexes because `Compare` was handed `ComparisonOptions.Default`, so every table with a different fill-factor-only index shows as Different, and the generated script contains index DDL the project explicitly said to skip. The green persistence round-trip tests are what makes this dangerous: they signal the feature works.

**Fix proposto**

Thread the project's options through: `engine.Compare(src, tgt, AppState.CurrentProject?.Options ?? ComparisonOptions.Default)` in the app, and add `--ignore-indexes/--ignore-keys` to the CLI verbs. Then either honour the remaining 14 flags in ComparisonEngine or delete them from the enum — a flag that exists and does nothing is worse than a missing flag. Add one engine test per honoured flag (currently only IgnoreKeys/IgnoreIndexes/ForceColumnOrder have any).

**Verifica adversariale**

All four Compare call sites verified — `grep -rn '\.Compare(' src/` returns exactly AppStateViewModel.cs:199, CompareCommand.cs:66, ReportCommand.cs:79, ScriptCommand.cs:80, and every one passes the literal `ComparisonOptions.Default`. Nothing in src/ ever passes a project-derived options value. `grep -rn HasFlag src/` returns 6 hits total: IgnoreKeys and IgnoreIndexes (ComparisonEngine.cs:355,357), ForceColumnOrder (:433), NoTransactions and DoNotOutputCommentHeader (ScriptGenerator.cs:131,132), IgnorePermissions (:278) — so 14 of the 20 flags in src/DbDelta.Core/Options/ComparisonOptions.cs are silent no-ops, exactly as claimed. XmlProjectStore round-trips OwnerMappings/TableMappings/ProjectOptions/legacy bitmap (Xml/XmlProjectStore.cs:95-119 write, :210-268 parse including `legacyOptions = (ComparisonOptions)optInt` at :268), the persistence tests assert the round-trip, and grep confirms OwnerMapping/TableMapping consumers are only ProjectSetupViewModel and the XML store — nothing in Core reads them.

**Nota**: The named scenario is inaccurate but the finding is STRONGER than written. In ParseV2 the `<Options>` element maps to ProjectOptions attributes (XmlProjectStore.cs:229-240), not to the ComparisonOptions bitmap — that comes from `<LegacyRefs options="…">` (:254-268). So the '.dbd whose <Options> carries IgnoreIndexes' path does not exist. The real and worse case is ProjectOptions (src/DbDelta.Core/Abstractions/ProjectOptions.cs:7-12: IgnoreFillFactor, IgnoreCollation, IgnoreWhitespace, IgnoreCommentBlocks, TreatExtendedPropertiesAsObjects): it is user-editable in the setup dialog, persisted, reloaded (ProjectSetupViewModel.cs:224 Build, :235 LoadFrom), stored on AppState.CurrentProject (App.axaml.cs:97) — and never translated into a single ComparisonOptions bit. Note the direction: ProjectOptions.Default has IgnoreFillFactor=false while ComparisonOptions.Default *includes* IgnoreFillFactor, so a user who leaves fill-factor comparison ON still gets it ignored — that is a false NEGATIVE, not just a false positive. Severity medium and effort M stand; the flag-thinning half of the fix is the larger piece.

---

## [medium] splitongo-not-string-or-comment-aware  ·  tests-cicd-arch

**SqlExecutor.SplitOnGo splits on a bare `GO` line even inside a block comment or a multi-line string literal**

- file: `src/DbDelta.Persistence/Sql/SqlExecutor.cs` · effort **M** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`[GeneratedRegex(@"^\s*GO\s*$", IgnoreCase | CultureInvariant)]` applied line-by-line with no lexer state (`SqlExecutor.cs:131-146`). The 7 SplitOnGo unit tests in `tests/DbDelta.Persistence.UnitTests/Sql/SqlExecutorTests.cs` cover case-insensitivity, mid-line GO, empty/whitespace and trailing GO — none covers a GO inside `/* … */` or inside a string literal. `Microsoft.SqlServer.TransactSql.ScriptDom` is already a PackageReference on DbDelta.Core (and therefore on Persistence) but is referenced **nowhere** in src (`grep -rn ScriptDom src/` → 0 hits), so the correct splitter is already paid for and unused.

**Scenario di fallimento**

Source has a stored procedure whose body carries a commented-out deployment note:
```
CREATE PROCEDURE dbo.Nightly AS
/* legacy step, re-enable with:
GO
EXEC dbo.Old
*/
SELECT 1;
```
The generated script embeds that body verbatim. `apply` (or the UI's direct execute) splits it at the commented `GO`, producing batch A = `CREATE PROCEDURE … /* legacy step, re-enable with:` (unterminated comment → error 105/102) and batch B = `EXEC dbo.Old */ SELECT 1;`. The deploy fails on a syntax error that points at a comment, and under `useOwnTransaction:false` on a non-enveloped script the batches before it have already committed.

**Fix proposto**

Replace the regex loop with ScriptDom, already referenced: `new TSql160Parser(true).Parse(reader, out errors)` → iterate `TSqlScript.Batches` and re-render each via `Sql160ScriptGenerator`, or use ScriptDom's lexer to find `TSqlTokenType.Go` tokens (comment/string aware) and cut on token offsets. Add the block-comment and multi-line-literal cases to SqlExecutorTests.

**Verifica adversariale**

src/DbDelta.Persistence/Sql/SqlExecutor.cs:158 is `[GeneratedRegex(@"^\s*GO\s*$", IgnoreCase | CultureInvariant)]`, applied per-line at :131-146 with zero lexer state — no tracking of /* */ nesting or quote parity, so a line that is exactly `GO` inside a block comment or a multi-line string literal is treated as a batch separator. The 7 SplitOnGo tests in tests/DbDelta.Persistence.UnitTests/Sql/SqlExecutorTests.cs:16-90 are exactly the ones listed (case-insensitive, mid-line GO, empty, whitespace-only, no-GO, trailing GO) — none covers a commented or quoted GO. The ScriptDom claim checks out: Microsoft.SqlServer.TransactSql.ScriptDom is a PackageReference in src/DbDelta.Core/DbDelta.Core.csproj:3 (version 180.6.0 in Directory.Packages.props:7) and grep over all *.cs in src/ returns zero references — the correct comment/string-aware splitter is already paid for and unused.

**Nota**: Two mitigations the reviewer should have named. (1) SSMS and sqlcmd split on GO the same line-based, non-comment-aware way, so a procedure whose body contains a bare `GO` line inside a comment could never have been deployed via those tools — it requires a client that submits CREATE PROCEDURE without GO-splitting (ORM migration, sp_executesql, an app calling ExecuteNonQuery). Narrower than presented, but still a real hole: once such a proc exists in the source, DbDelta can never deploy it. (2) The 'batches before it have already committed' tail applies only to envelope-less scripts. With the generated envelope, the failing batch throws out of the foreach at SqlExecutor.cs:89, the connection is disposed, and SQL Server implicitly rolls the open transaction back — so the normal path fails safe with a confusing syntax error, not a half-migrated target. Severity medium and effort M both stand.

---

## [medium] app-apply-cannot-be-cancelled  ·  undo-rollback

**The desktop apply passes CancellationToken.None and the dialog refuses to close while running — the only escape from a wedged deploy is killing the process**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:646` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```csharp
executeAsync: () => SqlExecutor.ExecuteAsync(
    AppState.TargetConnectionString!, script, CancellationToken.None, useOwnTransaction: false));
```
and `ConfirmExecuteDialog.axaml.cs`:
```csharp
private void OnClosing(object? sender, WindowClosingEventArgs e)
{
    if (DataContext is ConfirmExecuteViewModel { IsRunning: true })
        e.Cancel = true; // the script owns a transaction — no mid-flight close
}
```
The action bar has no cancel affordance while running: `Annulla` is bound `IsVisible="{Binding IsIdle}"`, `Chiudi` to `IsDone`, and the crimson button is the Execute command itself. `ConfirmExecuteViewModel.ExecuteAsync` takes no token.

**Scenario di fallimento**

A 40-batch deploy runs under `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE`. Batch 12's `ALTER TABLE` blocks behind a long-running reporting transaction; it burns the full 60 s timeout, and the accumulated rollback of the earlier batches then takes minutes while holding schema locks that block the whole application tier. The user, watching production alerts fire, has no cancel button, cannot close the dialog, and their only option is Task Manager — which is also the only thing that ends the server-side transaction, and does so without any record of what happened. Multiply by 40 batches for the worst case.

**Fix proposto**

Give `ConfirmExecuteViewModel` a `CancellationTokenSource`, pass its token into `SqlExecutor.ExecuteAsync`, and show a neutral `Interrompi` button bound to `IsRunning` that cancels it; keep the close-blocked behaviour but let the token do the aborting. Note the token must not then be reused for the rollback — see `rollback-uses-the-cancelled-token`.

**Verifica adversariale**

All three code claims verified. MainWindowViewModel.cs:646 passes CancellationToken.None; ConfirmExecuteViewModel.ExecuteAsync (:98-116) takes no token and its only knobs are IsRunning/IsDone; ConfirmExecuteDialog.axaml.cs:22-28 cancels Closing while IsRunning. The action bar (ConfirmExecuteDialog.axaml:132-149) is exactly as described: Annulla IsVisible={Binding IsIdle}, Chiudi IsVisible={Binding IsDone}, and the crimson button is IsVisible={Binding !IsDone} bound to ExecuteCommand whose CanExecute is IsIdle — so during a run it is visible but disabled and there is no other control. Nothing in tests/DbDelta.App.HeadlessTests/ViewModels/ConfirmExecuteViewModelTests.cs touches cancellation (it only asserts message/state transitions), so the gap is untested as well as unimplemented.

**Nota**: Confirmed and the proposed fix is sound: with useOwnTransaction:false the token would abort the in-flight SqlCommand and the script's own XACT_ABORT/connection teardown undoes the batch, so cancelling is genuinely safe here — no need to keep the token away from a rollback path, because on this path SqlExecutor owns no transaction (tx is null, SqlExecutor.cs:78-80). See my REFUTED verdict on rollback-uses-the-cancelled-token: that coupling does not exist.

---

## [medium] cli-apply-runs-arbitrary-script-with-no-transaction  ·  undo-rollback

**`dbdelta apply` hardcodes useOwnTransaction:false for an arbitrary user-supplied file — a script without BEGIN TRANSACTION leaves the DB half-migrated**

- file: `src/DbDelta.Cli/Commands/ApplyCommand.cs:67` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```csharp
string script = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
string[] batches = SqlExecutor.SplitOnGo(script);
…
SqlBatchResult result = await SqlExecutor.ExecuteAsync(tgtConn, script, ct, useOwnTransaction: false);
```
The file is read from `--script <path>` with no validation beyond `File.Exists`. `useOwnTransaction: false` means `SqlExecutor` starts no transaction of its own (`SqlTransaction? tx = useOwnTransaction ? … : null;`) and each GO batch is executed in autocommit. The command's own XML doc even claims the opposite: *"execute it against the target server inside a single GO-split transaction via SqlExecutor"*. Nothing checks the script for `BEGIN TRAN`/`SET XACT_ABORT`.

**Scenario di fallimento**

`dbdelta apply --target "…PROD…" --script migration.sql`, where migration.sql is hand-written (or a DbDelta script edited to remove the envelope, or produced by any other tool) and has 20 GO batches. Batch 7 fails on a missing referenced table. Batches 1-6 are already committed — `DROP TABLE`s and `ALTER TABLE`s among them — so PROD is now in a shape that exists in neither DEV nor PROD's previous state. The command prints `{"success":false,"batchesExecuted":6}` and exits DeploymentFailure. There is no transaction to roll back, no record of what batches 1-6 did, and re-running the file re-fails on batch 1 (`CREATE TABLE` already exists) so the operator must hand-repair production.

**Fix proposto**

Either default `--transaction on` (wrap in `SqlExecutor`'s own transaction unless the script already contains a `BEGIN TRAN` at line start), or refuse to run and exit with a remediation message when the script contains no `BEGIN TRANSACTION` and `--no-transaction` was not passed explicitly. One regex + one flag.

**Verifica adversariale**

Code verified verbatim: ApplyCommand.cs:52-53 reads the file and splits it, :45-50 validates only File.Exists, :67 hardcodes useOwnTransaction: false, and SqlExecutor.cs:78-80 then leaves tx null so every GO batch runs in autocommit (SqlExecutor.cs:86-91). The class doc-comment at ApplyCommand.cs:7-12 does claim 'inside a single GO-split transaction', which is false, and the acceptance test is even named Applies_script_inside_a_transaction_and_target_picks_up_the_change (tests/DbDelta.Cli.AcceptanceTests/ApplyCommandTests.cs:31) while only asserting that the object exists afterwards — so nothing in tests/ refutes the claim. No regex or BEGIN TRAN check exists anywhere.

**Nota**: Downgraded high→medium because the reachable blast radius is narrower than stated. Every DbDelta-generated script self-wraps: ScriptGenerator.cs:131 sets useTransaction = !options.HasFlag(NoTransactions), ScriptCommand.cs exposes no flag that could set NoTransactions (its only options are --source/--target/--out/--include-permissions) and always passes ComparisonOptions.Default, so `dbdelta script` output always carries SET XACT_ABORT ON + BEGIN TRANSACTION + the rollback verdict (DeploymentScriptWriter.cs:20-26, :42-59) — which is precisely why useOwnTransaction:false is the right choice for the documented workflow, and the command's own remediation string even says 'Run `dbdelta script --out <path>` first.' Half-application therefore requires a foreign or hand-edited script. Still worth the one-regex guard, and the wrong doc-comment plus the misleading test name should be fixed with it.

---

## [medium] command-timeout-hardcoded-60s  ·  undo-rollback

**CommandTimeout is a non-configurable 60 s, so any batch that legitimately needs longer (table rebuild, large index) can never be deployed**

- file: `src/DbDelta.Persistence/Sql/SqlExecutor.cs:23` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```csharp
private const int CommandTimeoutSeconds = 60;
private const int ConnectTimeoutSeconds = 10;
…
await using SqlCommand cmd = tx is null
    ? new(batch, cn) { CommandTimeout = CommandTimeoutSeconds }
    : new(batch, cn, tx) { CommandTimeout = CommandTimeoutSeconds };
```
There is no parameter, option, or connection-string escape hatch (the builder is rewritten only for `ConnectTimeout`). Neither `ApplyCommand` nor `MainWindowViewModel` exposes a timeout. The two `SqlExecutorTests` files contain no timeout test.

**Scenario di fallimento**

An identity seed change on `dbo.Orders` (30M rows) makes `RequiresFullRebuild` true, so `EmitRebuild` emits one batch containing `CREATE TABLE [dbo].[Orders_tmp] …; SET IDENTITY_INSERT … ON; INSERT INTO [dbo].[Orders_tmp] (…) SELECT (…) FROM [dbo].[Orders]; DROP TABLE [dbo].[Orders]; EXEC sp_rename …`. The INSERT needs ~4 minutes. At 60 s SqlClient raises a timeout, `SqlExecutor` returns failure, and the whole transaction rolls back (itself several more minutes of undo work while SERIALIZABLE locks are held on Orders). The user retries and gets the identical failure forever: there is no knob to raise the timeout, so this deploy is simply impossible through DbDelta, and the same applies to any large `CREATE INDEX`.

**Fix proposto**

Add `int commandTimeoutSeconds = 60` (0 = infinite) to `ExecuteAsync`, thread it from a `--command-timeout` CLI option and from a spin box in the confirm dialog. Default stays 60 so nothing changes silently.

**Verifica adversariale**

Verified. SqlExecutor.cs:23 private const int CommandTimeoutSeconds = 60, applied unconditionally at :86-88 on every SqlCommand, both the tx and non-tx branch. The builder at :62-65 rewrites only ConnectTimeout, and because CommandTimeout is assigned explicitly on the command it also overrides any 'Command Timeout' keyword a user might put in the connection string — so there really is no escape hatch. Neither ApplyCommand (no timeout option among --target/--script/--dry-run) nor MainWindowViewModel exposes one. I read both SqlExecutorTests files (tests/DbDelta.Persistence.UnitTests/Sql/SqlExecutorTests.cs, tests/DbDelta.Persistence.IntegrationTests/Sql/SqlExecutorTests.cs) — SplitOnGo cases, an empty-script case, a redaction case, three live cases; no timeout test, no cancellation test. The rebuild really is one batch: DeploymentScriptWriter.WriteBatch emits the whole EmitRebuild body between two GOs, so the 30M-row INSERT gets a single 60 s budget.

**Nota**: Downgraded high→medium: 'this deploy is simply impossible through DbDelta' is overstated — it is impossible through *direct execute*. The product's primary artefact is the script (Salva script / `dbdelta script --out`), and that script is self-contained (its own SET XACT_ABORT + BEGIN TRANSACTION + verdict), so the documented escape is running it in SSMS/sqlcmd where no client timeout applies. Also note no data-loss risk on timeout: SqlClient aborts the batch, the executor returns failure, and the script's transaction is undone on connection teardown. It remains a genuine, cheap-to-fix capability ceiling (any CREATE INDEX on a large table hits it).

---

## [medium] drop-paths-not-idempotent-cannot-converge  ·  undo-rollback

**Table/index/constraint/sequence/synonym/UDT drops have no IF EXISTS while module drops do, so a re-apply against a drifted target fails hard instead of converging**

- file: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:171` · effort **M** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: True

**Evidenza**

Modules are guarded — `DROP VIEW IF EXISTS [{v.Schema}].[{v.Name}];` (`ViewScriptEmitter.cs:39`), same for `DROP PROCEDURE IF EXISTS` (`ProcedureScriptEmitter.cs:35`), `DROP FUNCTION IF EXISTS` (`FunctionScriptEmitter.cs:34`), `DROP TRIGGER IF EXISTS` (`TriggerScriptEmitter.cs:38`) and creates use `CREATE OR ALTER`. Everything else is unguarded:
```csharp
private static string EmitDrop(Table table) => $"DROP TABLE [{table.Schema}].[{table.Name}];";
```
plus `sb.Append(" DROP CONSTRAINT [")…` (lines 212, 343, `ScriptGenerator.cs:164`, `:662`), the index drops via `_indexEmitter.EmitDrop`, and the sequence/synonym/UDT drop-and-recreate paths in `ScriptGenerator.BuildOne*`. `grep 'IF NOT EXISTS|OBJECT_ID' src/DbDelta.Core/ScriptGen` finds nothing.

**Scenario di fallimento**

A deploy fails at batch 30 of 40 and correctly rolls back. While the user investigates, a colleague manually drops the obsolete table `dbo.Archive_2019` (which the script also intended to drop) and manually creates `dbo.NewLookup` (which the script also intended to create). The user re-runs the exact same script: the DROP pass hits `DROP TABLE [dbo].[Archive_2019]` → error 3701, XACT_ABORT rolls the whole thing back, nothing is applied. Fixing that manually then trips `CREATE TABLE [dbo].[NewLookup]` → error 2714. The script can never converge; the user must go back and re-compare, which the app does not tell them (and if they had saved the script and lost the connection profile, the saved artifact is now useless).

**Fix proposto**

Mirror the module emitters: `DROP TABLE IF EXISTS`, `DROP INDEX IF EXISTS`, `ALTER TABLE … DROP CONSTRAINT IF EXISTS`, `DROP SEQUENCE/SYNONYM/TYPE IF EXISTS` (all supported from SQL Server 2016, which the LiveDb readers already target). Golden-script tests will need re-baselining; that is the bulk of the effort.

**Verifica adversariale**

Every cited line is accurate. Guarded: ViewScriptEmitter.cs:39, ProcedureScriptEmitter.cs:35, FunctionScriptEmitter.cs:34, TriggerScriptEmitter.cs:38 all emit DROP … IF EXISTS, and their creates use CREATE OR ALTER. Unguarded: TableScriptEmitter.cs:171 (DROP TABLE), :212 and :343 (ALTER TABLE … DROP CONSTRAINT), ScriptGenerator.cs:164 and :662 (DROP CONSTRAINT), IndexScriptEmitter.cs:47 (DROP INDEX … ON …), SequenceScriptEmitter.cs:58 (DROP SEQUENCE), SynonymScriptEmitter.cs:20 (DROP SYNONYM), UserDefinedTypeScriptEmitter.cs:31 and TableTypeUdtScriptEmitter.cs:37 (DROP TYPE), plus RoleScriptEmitter.cs:35 / UserScriptEmitter.cs:60. My own grep for IF NOT EXISTS / OBJECT_ID across src/DbDelta.Core/ScriptGen returns nothing, and CREATE TABLE (TableScriptEmitter.cs:31) is likewise unguarded, so the 2714 half of the scenario holds as well as the 3701 half. Idempotency is an explicit owner requirement (requirement 2), so this is in scope, not a nice-to-have.

**Nota**: Two corrections to the framing. (1) The asymmetry is not an oversight: docs/00_overview.md:294 / docs/02_data_models.md:1923 / docs/04_api_endpoints.md:1210 document IF EXISTS guards as Redgate's ObjectExistenceChecks (oec) option, default OFF, and DbDelta mirrors that default; docs/BACKLOG.md records CREATE OR ALTER as a deliberate, owner-reviewed divergence 'kept for idempotency' (the owner DESELECTED aligning it away). So the module guards are the intentional exception, and unconditional IF EXISTS on tables would break the Redgate byte-parity the backlog treats as the north star — implement it as the oec flag, default off, rather than always-on. (2) The proposed fix has a safety cost the finding does not mention: DROP TABLE IF EXISTS makes the script silently tolerate drift, which cuts against requirement 1/2 — the drifted object is exactly the thing the operator should be told about. Pair the guard with the pre-execute drift check from stale-comparison-silently-clobbers-target, or the cure is worse than the disease. Effort M is right and is dominated by golden re-baselining.

---

## [medium] execute-leaves-no-audit-trail  ·  undo-rollback

**The direct-execute path never persists the script it ran — the Avalonia app has no logging at all**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:630` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`string script = DeployScriptBuilder.Build(...)` is a local variable in `ExecuteOnTargetAsync`; it is handed to the closure, executed, and garbage-collected. Nothing writes it to disk. Saving a script only happens on the *separate* `DeployAsync` path (line 595, `SaveFilePickerAsync`), which the user does not go through when they press Allinea/Esegui.

`grep -rn 'AppendAllText|WriteAllText|ILogger|Serilog' src/` shows logging exists **only** in `src/DbDelta.Cli/Logging/SerilogBootstrap.cs` and file writes only in the CLI's `ScriptCommand`/`ReportCommand` and the connection/project JSON stores. `src/DbDelta.App.Avalonia` has no logger and no deploy log.

**Scenario di fallimento**

Friday 17:40 the user aligns 23 objects against PROD from the desktop app, sees "Esecuzione completata — 41 batch in 6.2 s", closes the app. Monday a reporting procedure returns wrong numbers. There is no record of which 23 objects were touched, which DDL ran, against which server/database, or at what time — the only trace on the machine is a status-bar string that died with the process. Even a manual reconstruction is impossible because the source database has moved on since Friday, so re-running the comparison no longer yields the script that was applied.

**Fix proposto**

In `ExecuteOnTargetAsync`, before showing the dialog, `Directory.CreateDirectory` a per-run folder under the existing app-data root used by `ProjectsFolder` (`%LOCALAPPDATA%/DbDelta/deploys/{yyyyMMdd-HHmmss}-{server}-{db}/`) and `File.WriteAllTextAsync` the script as `up.sql` plus a `meta.json` (redacted endpoints, object list, UTC + server clock, DbDelta version). After the run, append the `SqlBatchResult`. ~15 lines, no new dependency; it is also the natural home for `down.sql`.

**Verifica adversariale**

Verified. `script` at MainWindowViewModel.cs:630 is a local captured only by the executeAsync closure at :643-647; nothing persists it. I grepped the entire Avalonia project for File./Directory./StreamWriter/WriteAllText/ILogger/Serilog/Log.: the only writes are MainWindowViewModel.cs:604-605, inside DeployAsync (the separate Salva-script picker at :589-608), and there is no logger of any kind — Serilog lives only in src/DbDelta.Cli/Logging/SerilogBootstrap.cs. So after an Esegui run the only artefact is StatusText, an in-memory string. Confirmed there is no deploy log, no run metadata, nothing under LocalApplicationData/DbDelta beyond connections.json / recent-projects / projects (JsonConnectionStore.cs:41, JsonRecentProjectsStore.cs:45, ProjectsFolder.cs:20).

**Nota**: Downgraded high→medium: real resilience gap, but no wrong output and a workaround exists that the reviewer under-weighted — DeployAsync and ExecuteOnTargetAsync call DeployScriptBuilder.Build with identical arguments, so a user who presses Salva script before Esegui gets a byte-identical artefact (only the header timestamp differs). The defect is that nothing makes that automatic or mandatory. The proposed fix is correctly sized at S and ProjectsFolder.GetOrCreate() already exists as the app-data root to hang it off.

---

## [medium] no-recompare-after-execute-stale-grid-stays-armed  ·  undo-rollback

**After executing, the grid is not refreshed and Esegui stays enabled on the now-stale selection, so the user cannot tell what state the target is in**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:652` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```csharp
await dlg.ShowDialog(owner).ConfigureAwait(true);
if (vm.Result is not null)
{
    StatusText = vm.ResultMessage; // mirror the dialog outcome in the status bar
}
```
That is the entire post-execute handling — no `AppState.CompareAsync`, no clearing of `Rows`/selection. `CanExecuteOnTarget()` is `Rows.Any(r => r.IsSelected) && !string.IsNullOrWhiteSpace(AppState.TargetConnectionString)`, so it is still true, and each invocation rebuilds a fresh `ConfirmExecuteViewModel`.

**Scenario di fallimento**

Two ways this bites. (a) A successful deploy: the grid still shows all 23 rows as Different/OnlyInSource with checkboxes ticked, so the user, unsure whether it worked, presses Esegui again. The same script re-runs; `CREATE TABLE [dbo].[NewTbl]` now fails with "There is already an object named …", the dialog turns crimson, and the user reasonably concludes the *first* deploy failed. (b) A transport-level error during the final COMMIT batch: the outcome is genuinely indeterminate — the transaction may have committed server-side — DbDelta reports "Esecuzione fallita", and there is no automatic re-compare to establish the real target state, so the operator is left guessing while holding no down script and no journal.

**Fix proposto**

On dialog close, always run `AppState.CompareAsync` (or at minimum clear the selection and disable Esegui until a fresh compare) and label the result as post-deploy verification — an empty diff is the only trustworthy confirmation that the deploy landed, and it doubles as the answer for the indeterminate-COMMIT case.

**Verifica adversariale**

Verified verbatim. MainWindowViewModel.cs:650-655 is the entire post-execute handling — ShowDialog, then `if (vm.Result is not null) { StatusText = vm.ResultMessage; }`. No AppState.CompareAsync call, no Rows/selection reset (contrast :548 and :565 where the project-edit and Refresh paths DO re-compare, so the omission here is asymmetric, not a house style). CanExecuteOnTarget (:658-660) is Rows.Any(r => r.IsSelected) && target non-blank, both still true after a successful run, and each invocation builds a fresh ConfirmExecuteViewModel from the same stale LastComparisonRaw. Scenario (a) is mechanically right: with a table in the selection the re-run hits the unguarded CREATE TABLE at TableScriptEmitter.cs:31 → error 2714 → XACT_ABORT rolls the whole script back → crimson 'Esecuzione fallita', which reads as 'the first deploy failed'. Scenario (b) is right too — nothing establishes post-COMMIT ground truth.

**Nota**: Confirmed at the claimed severity/effort. Narrowing worth noting: a selection containing only modules re-runs harmlessly (CREATE OR ALTER + DROP … IF EXISTS), so scenario (a) needs at least one table/index/constraint/sequence/synonym/UDT in the selection — which is the common case. The lazy version of the fix is the one-liner in the proposal (clear selection + disable Esegui until a fresh compare); the automatic re-compare is the version that also answers the indeterminate-COMMIT case.

---

## [medium] stale-comparison-silently-clobbers-target  ·  undo-rollback

**The deploy script is built from an arbitrarily old comparison and CREATE OR ALTER overwrites concurrent target changes with no drift check and no undo**

- file: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs:200` · effort **M** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`AppStateViewModel` stores `LastComparisonRaw = result;` (line 200) and keeps it indefinitely — `grep 'LastCompared|DateTime' AppStateViewModel.cs` finds no timestamp field, and nothing in `MainWindow.axaml` shows the comparison's age. `ExecuteOnTargetAsync` builds the script straight off that stored result (`if (AppState.LastComparisonRaw is null) { return; }`, then `DeployScriptBuilder.Build(selected, …)`) and never re-reads the target. Module emitters use unconditional overwrite: `ViewScriptEmitter`/`ProcedureScriptEmitter` doc-comment themselves as *"the side-A body rewritten as CREATE OR ALTER"* (`ViewScriptEmitter.cs:9-11`, `ProcedureScriptEmitter.cs:8`), i.e. no existence or content guard.

**Scenario di fallimento**

09:00 the user compares DEV→PROD and leaves DbDelta open. 11:00 a colleague hotfixes `dbo.usp_CalcInvoice` directly on PROD to stop a live billing bug. 14:00 the original user, whose grid still shows the 09:00 diff, selects `usp_CalcInvoice` and clicks Esegui. The script emits `CREATE OR ALTER PROCEDURE [dbo].[usp_CalcInvoice]` with the 09:00 DEV body, silently reverting the hotfix. The dialog reports success; the pre-overwrite body was never captured anywhere (see `execute-leaves-no-audit-trail`), so the hotfix text is unrecoverable from DbDelta and the billing bug is live again.

**Fix proposto**

Two S-sized guards: (1) store `ComparedAtUtc` next to `LastComparisonRaw` and show it in the confirm dialog, refusing/warning past a threshold; (2) before executing, re-read only the target objects in the selection via the existing LiveDb readers and abort if any target-side body/shape no longer matches the `SideB` captured at compare time. The captured `SideB` is exactly the pre-deploy state, so it also becomes the `down.sql` input.

**Verifica adversariale**

Facts verified. AppStateViewModel.cs:200 stores LastComparisonRaw = result with no companion timestamp — I grepped the file for ComparedAt/DateTime/LastCompar and the only hits are :96-105 (FilteredDifferences recompute) and :200-201. MainWindow.axaml never renders a comparison age (its only LastComparison bindings are IsNull/IsNotNull visibility gates at :299/:310/:336). ExecuteOnTargetAsync (:618-647) builds the script straight off the stored result and never re-reads the target. Module overwrite is unconditional: ProcedureScriptEmitter.cs:19-21 routes both OnlyInA and Different to EmitCreateOrAlter → ModuleHeader.ToCreateOrAlterScript(p.Body, …) with no existence or content check; ViewScriptEmitter.cs:23-25 identical. So a 5-hour-old diff will emit CREATE OR ALTER PROCEDURE with the compare-time source body and silently replace whatever is on the target now.

**Nota**: Downgraded high→medium: it needs a concurrent third-party writer, and two partial mitigations exist that the reviewer did not mention — the Refresh command (MainWindowViewModel.cs:556, 'Riesegui il confronto' at MainWindow.axaml:212) and, more importantly, the diff pane resolves bodies LIVE at row-selection time (AppStateViewModel wires LiveDbObjectBodyResolver(srcCs, tgtCs)), so a user who clicks the row before executing would actually see the hotfixed target body. The genuine gap is that nothing forces or even hints at that: no compare timestamp anywhere in the UI and no drift check at execute time. Guard (2) in the proposed fix is the valuable half and shares its plumbing with the down-script input.

---

## [low] confirmexecute-missing-catch  ·  app-ui-robustness

**ConfirmExecuteViewModel.ExecuteAsync has a finally but no catch — a throwing delegate escapes into the command with Result left null**

- file: `src/DbDelta.App.Avalonia/ViewModels/ConfirmExecuteViewModel.cs:99` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`IsRunning = true; try { SqlBatchResult res = await _executeAsync().ConfigureAwait(true); … } finally { IsRunning = false; IsDone = true; }` — no catch. Today SqlExecutor.ExecuteAsync swallows everything into a failed SqlBatchResult, except its own guards (`ArgumentException.ThrowIfNullOrWhiteSpace(connectionString)`), so this is latent rather than live; with no global handler (see no-global-unhandled-exception-handler) an escape becomes a process kill because AsyncRelayCommand rethrows onto the UI context.

**Scenario di fallimento**

Any future/alternate executeAsync delegate (or a SqlExecutor change that lets an exception through, e.g. an OperationCanceledException once cancellation is added per the execute finding) throws: the dialog flips to IsDoneFailure with an EMPTY ResultMessage — the crimson panel shows nothing — Result stays null so the caller's status update is skipped, and the exception continues out of the command into an unhandled crash.

**Fix proposto**

Add `catch (Exception ex) { Succeeded = false; ResultMessage = $"Esecuzione fallita: {ex.Message}"; }` before the finally (Result stays null, which already means "did not complete"). One headless test with a throwing delegate.

**Verifica adversariale**

ConfirmExecuteViewModel.cs:99-116 has `try { … } finally { IsRunning = false; IsDone = true; }` with no catch, so a throwing delegate leaves Result null, Succeeded false → IsDoneFailure true with ResultMessage still `string.Empty` (:79), i.e. an empty crimson panel, and the exception escapes into the AsyncRelayCommand (which by default rethrows onto the UI context) with no global handler. ConfirmExecuteViewModelTests.cs has no throwing-delegate case, so it is untested. I also confirmed there is no live trigger today: SqlExecutor.ExecuteAsync funnels everything into a failed SqlBatchResult (SqlExecutor.cs:67-70, 96-104, 110-114), and its only escaping guards — ArgumentException.ThrowIfNullOrWhiteSpace(connectionString) and ThrowIfNull(script) at :50-51 — are both pre-satisfied by the caller (MainWindowViewModel.cs:622 rejects a blank target; script is non-null; ct is None so no OperationCanceledException).

**Nota**: Correctly scoped as latent: keep it as cheap hardening (it becomes live the moment cancellation is wired per the execute finding), not as a bug with a present-day failure path.

---

## [low] new-project-button-is-a-noop  ·  app-ui-robustness

**The "Nuovo progetto" toolbar button is wired to a stub that does nothing**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:514` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`[RelayCommand] public void NewProject() { // Wave 2C stub — no-op until ProjectSetupDialog routing is wired. }` — and MainWindow.axaml:92 binds a fully styled, always-enabled toolbar button to `{Binding NewProjectCommand}` with tooltip "Crea un nuovo progetto DbDelta". EditProjectCommand/OpenProjectCommand next to it are real.

**Scenario di fallimento**

In shipped v1.0.0-rc4 a user who wants to compare a different pair of databases clicks "Nuovo progetto": nothing happens at all — no dialog, no error, no busy state. The only way to start a new project is to restart the app (the setup dialog is shown once from MainWindow.Opened) or to abuse "Modifica", which requires an existing project.

**Fix proposto**

Point the command at the same flow EditProjectAsync uses with a blank VM: `ProjectSetupViewModel vm = new(_credentials); … dialog.ShowDialog<DbDeltaProject?>(owner)` then assign the connection strings + CurrentProject and compare. Take the Window as the CommandParameter as the neighbouring buttons already do.

**Verifica adversariale**

MainWindowViewModel.cs:514-518 is a literal empty body with the comment '// Wave 2C stub — no-op until ProjectSetupDialog routing is wired.', and MainWindow.axaml:92-105 binds an always-enabled toolbar button (label 'Nuovo', tooltip 'Crea un nuovo progetto DbDelta') to NewProjectCommand, next to real OpenProjectCommand/EditProjectCommand buttons. Nothing in docs/BACKLOG.md tracks it as deliberate.

**Nota**: Severity dropped medium→low: the claim that the only workaround is restarting the app is wrong. App.axaml.cs shuts the app down if the startup setup dialog is cancelled, so CurrentProject is always non-null and 'Modifica' (EditProjectAsync, which reopens the same dialog and lets both endpoints be re-pointed) is always available — and 'Carica' opens the prefilled dialog too. It is a dead button, not a blocked workflow.

---

## [low] rebuildrows-quadratic-ui-freeze  ·  app-ui-robustness

**RebuildRows fires 4 full-collection scans per added row — O(n²) UI freeze on a large schema**

- file: `src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:56` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

Constructor: `Rows.CollectionChanged += (_, _) => { DeployCommand.NotifyCanExecuteChanged(); ExecuteOnTargetCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(SelectionSummary)); };` and RebuildRows adds rows one at a time inside the loop (`Rows.Add(row);`). Each notification re-evaluates `CanDeploy() => Rows.Any(r => r.IsSelected)` and `CanExecuteOnTarget()` (full scan when nothing is selected — the documented default state) and re-reads SelectionSummary, which itself computes `TotalDiffsCount` (`Rows.Count(r => !r.IsIdentical)`) and `SelectedCount` (`Rows.Count(r => r.IsSelected)`). Identical objects are included in Rows (IdenticalsCount / the "Identici" group), so n is the whole object count, not the diff count.

**Scenario di fallimento**

Comparing two copies of a 4000-object ERP schema produces ~4000 rows; the Add loop performs ~4 × Σn ≈ 32M predicate invocations plus a DataGridCollectionView re-sort per insert. The window is frozen (busy overlay showing, no input) for many seconds after every compare and every "Aggiorna"; on a 10k-object catalog it looks like a hang and users kill the process mid-run.

**Fix proposto**

Detach the CollectionChanged reaction while rebuilding (or build a local List and swap it in), then raise the command/summary notifications once at the end of RebuildRows — the method already re-raises all the count properties explicitly at lines 499-508.

**Verifica adversariale**

Mechanism verified exactly as quoted. MainWindowViewModel.cs:56-61 raises DeployCommand/ExecuteOnTargetCommand NotifyCanExecuteChanged + OnPropertyChanged(SelectionSummary) on EVERY CollectionChanged, and RebuildRows adds one row at a time (:494) inside the loop. CanDeploy (:611) and CanExecuteOnTarget (:658) are full `Rows.Any(IsSelected)` scans in the documented default state (rows start unselected — MainWindowViewModelTests:107-117), and SelectionSummary (:120-132) recomputes TotalDiffsCount + SelectedCount (+IdenticalsCount when total==0). It is genuinely bound, so the getter really runs: MainWindow.axaml:346 `Text="{Binding SelectionSummary}"`. Identical rows are included in Rows (IdenticalsCount at :107, bound at MainWindow.axaml:382-384), so n is the whole object count. RebuildRows already re-raises everything once at :499-508, making the per-add notifications pure waste.

**Nota**: Severity dropped medium→low and the scenario is overstated: ~4-5 O(n) scans per add at n=4000 is ~36M cheap predicate iterations, on the order of a second behind the busy overlay — not 'frozen for many seconds', and it is not a correctness issue. It does grow quadratically, so it matters at 10k+. The fix (build a local list / suppress the handler, then notify once) is right and nearly free.

---

## [low] comparison-options-are-decorative  ·  diff-engine-correctness

**15 of the 20 ComparisonOptions flags are never read by the engine, and the CLI even computes an options value then passes Default to Compare**

- file: `src/DbDelta.Cli/Commands/ScriptCommand.cs` · effort **M** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ScriptCommand.cs:73-80 `ComparisonOptions opts = ComparisonOptions.Default; if (emitPerms) { opts &= ~ComparisonOptions.IgnorePermissions; } ... .Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default);` — the computed `opts` is passed to Generate but NOT to Compare. Grep across src shows the engine consults only IgnoreKeys (ComparisonEngine.cs:355), IgnoreIndexes (:357) and ForceColumnOrder (:433); IgnoreCollations, IgnoreConstraintNames, IgnoreComments, IgnoreWhitespace, IgnoreTriggers, IgnoreIdentitySeed, CaseSensitiveObjectDefinition, IgnoreWithElementOrder, IgnoreFileGroups, IgnoreUserSettings, IgnoreStatistics, IgnoreUsersPermissionsAndRoleMemberships and IgnoreExtendedProperties-equivalents are dead. Every production call site hardcodes `ComparisonOptions.Default`, and ProjectOptions (persisted per project by XmlProjectStore) is never translated into ComparisonOptions at all.

**Scenario di fallimento**

A user saves a .dbd project with IgnoreCollation = true (XmlProjectStore.cs:235 round-trips it), reopens it and compares: the setting has no effect whatsoever because ProjectOptions is never mapped into ComparisonOptions and the engine would ignore the flag anyway. Same for anyone who reasons about the documented option list — the tool advertises Redgate-parity toggles that silently do nothing, which is how findings collation-mass-diff and system-named-constraints become unworkable in the field.

**Fix proposto**

Either implement the flags the UI/project file exposes (start with IgnoreCollations, IgnoreConstraintNames, IgnoreComments, IgnoreTriggers, IgnoreIdentitySeed) or delete the unimplemented enum members and the ProjectOptions fields so the surface cannot lie. Immediately: pass `opts` (not Default) to Compare in ScriptCommand.

**Verifica adversariale**

Verified with two corrections. Confirmed: a grep for every non-Default flag across src shows IgnoreCollations, IgnoreConstraintNames, IgnoreUserSettings, CaseSensitiveObjectDefinition, IgnoreStatistics, IgnoreTriggers, IgnoreWithElementOrder, IgnoreFileGroups, IgnoreIdentitySeed, IgnoreUsersPermissionsAndRoleMemberships, ThrowOnFileParseFailed, IgnoreWhitespace and IgnoreComments appear ONLY in Options/ComparisonOptions.cs — the engine reads only IgnoreKeys (:355), IgnoreIndexes (:357) and ForceColumnOrder (:433); the generator reads only IgnorePermissions (:278), NoTransactions (:131) and DoNotOutputCommentHeader (:132). ScriptCommand.cs:73-80 does compute `opts` and then pass `ComparisonOptions.Default` to Compare. ProjectOptions (Abstractions/ProjectOptions.cs) is round-tripped by XmlProjectStore (:119-124, :230-237) and never translated to ComparisonOptions. Corrections: (1) the ScriptCommand mix-up is INERT — the engine never reads IgnorePermissions (ComparePermissions runs unconditionally at ComparisonEngine.cs:29), so Default vs opts changes nothing there; (2) the stated failure scenario overstates exposure — ProjectSetupDialog.axaml has no binding for any ProjectOptions field (grep finds none), so 'a user saves a project with IgnoreCollation = true' requires hand-editing the .dbd XML.

**Nota**: Dead-flag inventory is accurate. Drop the 'pass opts to Compare' urgency (inert) and the project-file scenario (no UI exposes those toggles); the real cost is that the enum + docs advertise toggles that do nothing.

---

## [low] fk-ck-not-trusted-not-modelled  ·  diff-engine-correctness

**is_not_trusted (WITH NOCHECK) is not read for FKs or CHECK constraints → an untrusted constraint compares Identical to a validated one**

- file: `src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs` · effort **S** · requisito **affidabile** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ForeignKeysQuery (lines 33-58) selects delete/update actions, is_disabled and is_not_for_replication but not `fk.is_not_trusted`; ChecksQuery (60-70) likewise omits `cc.is_not_trusted`. The models (ForeignKey.cs, CheckConstraint.cs) have no such property, so ConstraintShapeEqual (ComparisonEngine.cs:479-491) cannot see it. docs/01_architecture.md:310 lists `fk.is_not_trusted` as a field the reader is supposed to project.

**Scenario di fallimento**

A prod hotfix added the FK with `ALTER TABLE ... WITH NOCHECK ADD CONSTRAINT FK_Movimenti_Cliente ...` to avoid validating 40M existing rows, so prod's FK is untrusted (integrity NOT guaranteed and the optimizer cannot use it for join elimination). Dev's identical FK is trusted. DbDelta reports Identical, so the discrepancy is never surfaced and prod keeps silently violating rows.

**Fix proposto**

Add is_not_trusted to both queries and to the two records, include it in ConstraintShapeEqual/ForeignKeyShapeEqual, and emit `WITH CHECK CHECK CONSTRAINT` when only the trust state differs.

**Verifica adversariale**

Verified. ConstraintReader.ForeignKeysQuery (Readers/ConstraintReader.cs:34-58) projects delete/update actions, is_disabled and is_not_for_replication but not `fk.is_not_trusted`; ChecksQuery (:60-71) projects name/definition/is_disabled/is_not_for_replication and not `cc.is_not_trusted`. ForeignKey.cs and CheckConstraint.cs have no such property, so ComparisonEngine.ConstraintShapeEqual (:479-491) and ScriptGenerator.ForeignKeyShapeEqual (:676-684) cannot see it — a WITH NOCHECK (untrusted) FK compares Identical to a validated one. docs/01_architecture.md:310 does list `fk.is_not_trusted` among the fields the reader is supposed to project, so this is an implementation gap. Low is right: metadata/optimizer semantics, no data loss and no invalid DDL.

**Nota**: Holds unchanged.

---

## [low] linediffer-quadratic-memory  ·  diff-engine-correctness

**LineDiffer allocates a full O(N·M) int matrix → clicking a large ERP procedure in the diff viewer allocates hundreds of MB and can OOM the app**

- file: `src/DbDelta.Core/Diff/LineDiffer.cs` · effort **S** · requisito **resiliente** · verdetto **PLAUSIBLE** · coperto da test: False

**Evidenza**

LineDiffer.cs:82-86 `int m = src.Length; int n = tgt.Length; int[,] table = new int[m + 1, n + 1];` with the comment "Build LCS via O(N*M) DP — well within budget for SQL bodies". Called unguarded from the UI thread: DiffViewerViewModel.cs:69 `Rows = LineDiffer.Compute(SourceBody, TargetBody);` — no size check, no try/catch, and the resolver hands it whole sys.sql_modules definitions.

**Scenario di fallimento**

An Italian ERP schema with a 15,000-line generated stored procedure that differs slightly between dev and prod. The user clicks that row in the results grid: `new int[15001, 15001]` = 900 MB of contiguous LOH allocation on the UI thread → multi-second freeze then OutOfMemoryException, which crashes the window (nothing catches it). A 20k-line proc is 1.6 GB and fails outright. The user cannot inspect the very object they most need to review before deploying.

**Fix proposto**

Guard the entry point: if `(long)m * n` exceeds ~4M cells, either run a cheap line-hash prefix/suffix trim first (usually collapses ERP procs to a few hundred differing lines) or return a single "body too large for line diff" row plus the section summary. Rolling two int[] rows instead of the full matrix also cuts memory to O(min(N,M)) if the back-walk is restructured.

**Verifica adversariale**

The allocation math is real, the consequence is misstated. Confirmed: LineDiffer.cs:82-86 allocates `new int[m + 1, n + 1]` with the comment 'well within budget for SQL bodies', and DiffViewerViewModel.cs:69 calls `LineDiffer.Compute(SourceBody, TargetBody)` with no size check — 15k lines really is ~900 MB of zeroed LOH plus 225M string compares. But two evidence claims are wrong: (1) it does NOT run on the UI thread — LoadAsync awaits both resolver calls with `.ConfigureAwait(false)` (DiffViewerViewModel.cs:65-68), so Compute executes on a thread-pool continuation; (2) it cannot 'crash the window' — the only caller is fire-and-forget, `_ = DiffViewer.LoadAsync(value, CancellationToken.None);` at AppStateViewModel.cs:122, so an OutOfMemoryException is captured in an unobserved Task and swallowed (no ThrowUnobservedTaskExceptions anywhere in the props/csproj/runtimeconfig — grep confirms), while the `finally` still clears IsLoading. Observable symptom: memory spike and a stall, then a silently empty diff pane. Marked PLAUSIBLE because I could not measure the real threshold, and severity dropped to low: no wrong output, no damage, and it needs a 15k+ line body.

**Nota**: Keep the guard (cheap prefix/suffix trim or a 'body too large' row) but drop the crash claim: the exception is swallowed by the fire-and-forget call at AppStateViewModel.cs:122, and Compute is not on the UI thread.

---

## [low] catalog-scan-timeout-reported-as-connectivity  ·  livedb-readers

**Catalog reads use the default 30 s CommandTimeout and a timeout is reported as a connectivity failure**

- file: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

No reader sets `CommandTimeout` — every command is `new SqlCommand(sql, connection)` (e.g. TableReader.cs:56, PermissionReader.cs:46); the only `CommandTimeout` in the whole repo is `SqlExecutor`'s 60 s (`grep -rn CommandTimeout src/`). `LoadAsync` then maps the timeout error number to the wrong bucket: `catch (SqlException ex) when (ex.Number is 53 or -2) { return … new Error(ErrorCode.CannotConnect, ex.Message, "Verify server name, network connectivity, and firewall rules."); }` — -2 is the client query timeout, not a connectivity error.

**Scenario di fallimento**

A 6000-object production database on a busy server: the columns query or the `sys.database_permissions` query exceeds 30 s → SqlException -2 → the GUI shows "Verify server name, network connectivity, and firewall rules" for a database it just connected to successfully. The user chases a firewall problem; there is no way to raise the timeout from the UI, project file, or CLI, so the comparison is simply impossible.

**Fix proposto**

Set `CommandTimeout` on catalog commands from a single constant (e.g. 300 s) or from the connection string's `Command Timeout` keyword, and split the catch: `ex.Number == -2` → a distinct `ErrorCode` with the hint "catalog scan timed out; increase Command Timeout", keeping 53 for connectivity.

**Verifica adversariale**

Partially refuted on impact, confirmed on the defect. Confirmed: no reader sets CommandTimeout — `grep -rn CommandTimeout src/` returns only SqlExecutor.cs:23/87/88 (60 s); every catalog command is a bare `new SqlCommand(sql, connection)` (e.g. TableReader.cs:56,74; PermissionReader.cs:46; IndexReader.cs:55), and `ConnectionFactory.OpenAsync` adds nothing. And the misclassification is exactly as quoted: `catch (SqlException ex) when (ex.Number is 53 or -2)` → ErrorCode.CannotConnect with the hint "Verify server name, network connectivity, and firewall rules" (LiveDbSource.cs:110-116) — -2 is the client-side query timeout, not connectivity. REFUTED sub-claim: "there is no way to raise the timeout from the UI, project file, or CLI" is wrong. The repo uses Microsoft.Data.SqlClient 6.0.1 (Directory.Packages.props:6), which honours the `Command Timeout` connection-string keyword and applies it as the default CommandTimeout for commands on that connection — and both the GUI (AppStateViewModel.SourceConnectionString) and CLI (--source/--target) take raw connection strings, so a user can already set `Command Timeout=300`. The remaining harm is diagnostic misdirection only, and even then ex.Message for -2 reads "Execution Timeout Expired…", so the user does get a timeout signal alongside the wrong hint.

**Nota**: Downgraded critical path: this is a wrong-remediation-hint bug, not a can't-compare bug — `Command Timeout=300` in the connection string already works today. Still worth the S fix: split the catch so -2 gets its own ErrorCode + 'catalog scan timed out; raise Command Timeout' hint, and keep 53 for connectivity. Setting an explicit default on catalog commands is optional polish.

---

## [low] typename-null-unhandled  ·  livedb-readers

**TYPE_NAME can return NULL for a type the login cannot reference — GetString throws a non-SqlException that LoadAsync does not catch**

- file: `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs` · effort **S** · requisito **resiliente** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`string typeName = columnsReader.GetString(2);` where column 2 is `TYPE_NAME(c.user_type_id)`; TYPE_NAME returns NULL when the caller lacks permission to reference the type. Same unguarded read at TableTypeUdtReader.cs:74, UserDefinedTypeReader.cs:39, SequenceReader.cs:40 and LiveDbObjectBodyResolver.cs:300. `LoadAsync` only catches `SqlException` (LiveDbSource.cs:103-122), so a `SqlNullValueException` escapes the Result<> contract.

**Scenario di fallimento**

A least-privilege comparison login has SELECT on `dbo.Invoice` but no permission on the alias type `dbo.Money` used by `Invoice.Amount`. TYPE_NAME returns NULL → `SqlNullValueException: Data is Null. This method or property cannot be called on Null values.` The GUI's blanket `catch (Exception ex) { LastError = ex.Message; }` shows that message with no object name; the CLI (`root.Parse(args).InvokeAsync()`, no handler) dumps a stack trace and returns an unmapped exit code.

**Fix proposto**

`r.IsDBNull(2) ? null : r.GetString(2)` and, when null, either skip the column with a recorded warning or fail with `ErrorCode.CatalogQueryFailed` naming the table+column. Also broaden `LoadAsync` to catch `Exception` (excluding OperationCanceledException) so the reader can never break the `Result<Database>` contract.

**Verifica adversariale**

Confirmed with one correction. `TYPE_NAME` is documented to return NULL when the caller lacks permission to reference the type, and the unguarded `GetString` on a `TYPE_NAME(...user_type_id)` projection is real at TableReader.cs:81 (col 2 = `TYPE_NAME(c.user_type_id)`, TableReader.cs:28), TableTypeUdtReader.cs:74 (`r.GetString(2)` over `TYPE_NAME(c.user_type_id)`, line 32), LiveDbObjectBodyResolver.cs:300 (over line 270) and SequenceReader.cs:41 (over `TYPE_NAME(seq.user_type_id)`, line 18). `LoadAsync` catches only SqlException (LiveDbSource.cs:103-122), so a SqlNullValueException escapes the Result<> contract; the GUI's blanket `catch (Exception ex) { LastError = ex.Message; }` (AppStateViewModel.cs:214-217) shows a message with no object name. CORRECTION: two of the reviewer's five sites are not reachable — UserDefinedTypeReader.cs:39 and LiveDbObjectBodyResolver.cs:185 read `TYPE_NAME(t.system_type_id)`, and system types are visible to public, so NULL cannot arise there for permission reasons.

**Nota**: Confirmed for the four user_type_id sites; UserDefinedTypeReader.cs:39 (and the resolver's UDT branch) use system_type_id and are safe — drop them from the fix list. The CLI consequence is different from and arguably worse than described: Program.cs has no handler (`root.Parse(args).InvokeAsync()`), and System.CommandLine 2.0.0-beta5's default exception handler returns exit code 1, which DbDelta defines as SuccessDifferencesFound (ExitCodes.cs:9) — a crashed comparison would be indistinguishable from 'differences found' in a script. I did not execute this to confirm the beta5 handler behaviour, so treat that detail as unverified; broadening LoadAsync's catch (excluding OperationCanceledException) removes the question either way.

---

## [low] linediffer-quadratic-memory  ·  redgate-parity

**LineDiffer allocates an int[m+1,n+1] LCS table — a large module body OOMs the diff pane**

- file: `src/DbDelta.Core/Diff/LineDiffer.cs` · effort **S** · requisito **quality** · verdetto **PLAUSIBLE** · coperto da test: False

**Evidenza**

```
int m = src.Length;
int n = tgt.Length;
int[,] table = new int[m + 1, n + 1];
```
with the comment "well within budget for SQL bodies" and no size guard. `LineDiffer.Compute` is called from `DiffViewerViewModel.cs:69` on every row selection, on the body text resolved live from the server (`nvarchar(max)` definitions, unbounded). The existing tests (`LineDifferTests.cs`) all use 1-3 line inputs.

**Scenario di fallimento**

The user clicks a row for a generated 12,000-line MERGE/ETL stored procedure (routine in data-warehouse codebases) whose target counterpart is also ~12,000 lines. `new int[12001, 12001]` requests 12001*12001*4 ≈ 576 MB in one contiguous allocation → `OutOfMemoryException` on a 32-bit-ish LOH-fragmented process, or a multi-second freeze plus GC pressure on a healthy one. The exception surfaces from a UI command, so the diff pane dies on a legitimate object.

**Fix proposto**

Trim the common prefix/suffix before building the table (kills most real cases in a few lines), then cap: if `m*n` exceeds ~4M cells, skip the LCS and return a single Modified block ("corpo troppo grande per il diff riga-per-riga"). Both are small and keep the pane responsive; a Myers/O(min(m,n)) diff is the upgrade path if the cap ever bites.

**Verifica adversariale**

The code is exactly as quoted — LineDiffer.cs:82-99, `int[,] table = new int[m + 1, n + 1]` with the "well within budget for SQL bodies" comment at line 15 and no size guard, no prefix/suffix trim — and it is driven by unbounded server data: DiffViewerViewModel.cs:65-69 resolves both bodies live and calls Compute on every row selection. The arithmetic is right (12001² × 4 ≈ 576 MB). What I could not confirm is the stated failure: the shipped target is win-x64 (Release/net10.0/win-x64), where a 576 MB single array normally succeeds and .NET Core permits >2 GB arrays, so OOM needs roughly 20k+ lines on BOTH sides; and the claimed symptom is wrong in two ways — Compute runs after `.ConfigureAwait(false)` (DiffViewerViewModel.cs:65-68), i.e. off the UI thread, so no freeze; and the call is fire-and-forget (`_ = DiffViewer.LoadAsync(...)`, AppStateViewModel.cs:122), so a throw is an unobserved task exception, not a crash — the pane would silently keep the previous object's rows under the new object's name. Real robustness gap (unbounded allocation from catalog data), but the concrete damage is a fat transient LOH allocation plus O(n·m) work, hence low.

**Nota**: Trim the prefix/suffix and cap at ~4M cells as proposed — that is genuinely S and removes the unbounded allocation. Drop the OOM/32-bit framing (the app ships x64) and the "diff pane dies" claim (LoadAsync is fire-and-forget at AppStateViewModel.cs:122, so it degrades to a stale pane, silently).

---

## [low] owner-table-mappings-never-applied  ·  redgate-parity

**OwnerMappings / TableMappings are persisted in the project but never applied — a mapped table is reported for DROP**

- file: `src/DbDelta.Persistence/Xml/XmlProjectStore.cs` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`XmlProjectStore` always writes `<OwnerMappings>` / `<TableMappings>` elements (lines 95-113) and parses them back (212-223); `ProjectSetupViewModel` holds them as observable collections (166-169, 222-223, 237-246, 270-279). But `grep -rn "OwnerMapping|TableMapping" src/` shows **no** consumer in `DbDelta.Core.Diff` or `DbDelta.Core.ScriptGen` — the comparison engine pairs objects strictly on `ObjectIdentity` (`aByIdentity = a.Tables.ToDictionary(t => t.Identity)`), with no remapping hook. There is no UI for them either (no `OwnerMappings` binding in `ProjectSetupDialog.axaml`), so they are reachable only by editing the .dbd XML — which the persisted empty elements invite.

**Scenario di fallimento**

A user opens `MyProject.dbd`, sees the `<OwnerMappings>` element DbDelta wrote, and adds a map `dbo`→`sales` to compare a dev DB (objects in `dbo`) against a prod DB (same objects in `sales`). The mapping is parsed and stored, then ignored: `dbo.Customer` reports "Solo provenienza" and `sales.Customer` reports "Solo destinazione". Deploying the default selection CREATEs `dbo.Customer` and **DROPs the populated `sales.Customer`** — real data loss under a feature the project format advertises.

**Fix proposto**

Either implement it (apply the mapping when building the identity dictionaries in `ComparisonEngine`, and un-map when emitting target-side DDL), or — the lazy correct move for v1 — stop persisting what nothing honours: drop the two elements from `XmlProjectStore` and the two collections from the view-model, and reject a .dbd that carries non-empty mappings with a clear "not supported in this version" error rather than silently ignoring them.

**Verifica adversariale**

The mechanical claim holds exactly. XmlProjectStore.cs:95-117 unconditionally writes <OwnerMappings>/<TableMappings> on every save, lines 212-223 parse them back, ProjectSetupViewModel.cs:166-169/222-223/237-246/270-279 holds them — and `grep -rn 'OwnerMapping|TableMapping' src/` shows no consumer anywhere in DbDelta.Core.Diff or ScriptGen. Pairing is strictly by identity (ComparisonEngine.cs:272-273 `a.Tables.ToDictionary(t => t.Identity)`), no remap hook. No UI: I enumerated all 31 Bindings in ProjectSetupDialog.axaml — no mappings, no options. Downgraded to low on two counts: (1) the path requires the user to discover and hand-edit an undocumented XML element (only the generated DocFX API page mentions "owner/table mappings"; no user doc does); (2) the stated trigger is wrong — DifferenceRowViewModel.cs:32 `private bool _isSelected;` defaults to FALSE, so there is no "default selection" that deploys anything. The user must additionally tick the OnlyInB row to reach the DROP. Real, but a compound-improbability path, not a live data-loss route.

**Nota**: Fix the "deploying the default selection" wording — rows start unselected (DifferenceRowViewModel.cs:32); the DROP requires an explicit tick. The lazy correct move you propose is right and is S: stop writing the two elements and hard-fail a .dbd that carries non-empty mappings.

---

## [low] project-options-never-applied  ·  redgate-parity

**ProjectOptions persisted in the .dbd project file are silently ignored by the comparison**

- file: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs` · effort **S** · requisito **parity** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`ProjectOptions` (IgnoreFillFactor, IgnoreCollation, IgnoreWhitespace, IgnoreCommentBlocks, TreatExtendedPropertiesAsObjects) is written and re-read by `XmlProjectStore` (lines 119-128 / 230-239), round-trips through `ProjectSetupViewModel` (lines 163, 224, 235, 268) and is covered by persistence tests (`XmlProjectStoreTests.cs:172`, `ProjectSetupViewModelTests.cs:167-191`). But `grep -rn "ProjectOptions" src/` shows **no** conversion to `ComparisonOptions` anywhere, and the only compare call in the app is:
```
ComparisonResult result = engine.Compare(srcRes.Value!, tgtRes.Value!, ComparisonOptions.Default);
```
The CLI is the same — `CompareCommand.cs:66`, `ReportCommand.cs:79`, `ScriptCommand.cs:80` all hardcode `ComparisonOptions.Default`. There is also no binding to `Options` in `ProjectSetupDialog.axaml` (all `Binding` targets enumerated: no `Options.*`), so the values are only reachable by hand-editing the XML.

**Scenario di fallimento**

A user (or a future UI) sets `ignoreCollation="true"` in `MyProject.dbd`, loads the project, and compares two databases with different default collations. The saved option is parsed, held in the view-model, re-saved — and has zero effect: every nvarchar column still reports Different. The user trusts a setting that does nothing, and the round-trip tests give false confidence that the feature works.

**Fix proposto**

Add `ProjectOptions.ToComparisonOptions()` (5 lines) and pass it at the single app call site plus a new `--options` CLI switch. Then either drop `ProjectOptions` in favour of persisting the `ComparisonOptions` flag names directly (one model, not two), or keep it as the on-disk shape and make the mapping the only bridge.

**Verifica adversariale**

Factually exact. ProjectOptions appears in src/ only at Abstractions/ProjectOptions.cs, DbDeltaProject.cs:21,40, XmlProjectStore.cs:119-128/230-239/288/354, ProjectSetupViewModel.cs:163,224,235,268 — never converted to ComparisonOptions, and every compare call site passes Default (AppStateViewModel.cs:199, CompareCommand.cs:66, ReportCommand.cs:79, ScriptCommand.cs:80). Also worth noting the reviewer missed a second dead path: DbDeltaProject.Options (a real ComparisonOptions) is parsed from legacy XML at XmlProjectStore.cs:256-268 and is equally ignored. I enumerated every Binding in Views/ProjectSetupDialog.axaml: 31 targets, none touching Options or the mappings — so the reviewer is right that hand-editing the .dbd is the only way in. Round-trip tests exist and only assert persistence (XmlProjectStoreTests.cs:172,207; ProjectSetupViewModelTests.cs:167-168,190-191). Downgraded to low: with no UI, no CLI switch, and ProjectOptions.Default/ the VM default (ProjectSetupViewModel.cs:163 `new(false,false,true,false,false)`) setting only IgnoreWhitespace — which is unconditionally applied by BodyNormalizer anyway — no realistic user reaches a state where the ignored option would have changed the result. This is dead weight / a maintainer trap, not a wrong result on a realistic path.

**Nota**: Mention DbDeltaProject.Options (XmlProjectStore.cs:256-268) alongside ProjectOptions — there are TWO persisted option models, both ignored. Lazy correct move: delete one and wire the other in five lines at AppStateViewModel.cs:199.

---

## [low] script-cmd-discards-computed-options  ·  redgate-parity

**`dbdelta script` computes comparison options then passes Default to the engine anyway**

- file: `src/DbDelta.Cli/Commands/ScriptCommand.cs` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```
ComparisonOptions opts = ComparisonOptions.Default;
if (emitPerms) { opts &= ~ComparisonOptions.IgnorePermissions; }

ComparisonResult comparison = new ComparisonEngine()
    .Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default);   // <-- opts dropped
string script = new ScriptGenerator().Generate(comparison, selection: null, options: opts, ...);
```
`opts` reaches the generator but not the engine.

**Scenario di fallimento**

Inert today only because `ComparePermissions` (ComparisonEngine.cs:106) takes no options parameter, so `IgnorePermissions` has no engine-side effect. It becomes live the moment any Ignore* flag is honoured in the engine (the fix for `comparison-options-mostly-dead`): `--include-permissions`, and every future `--options` value, would then silently fail to influence the comparison while still influencing emission — producing a script whose contents disagree with the diff it was generated from.

**Fix proposto**

Pass `opts` instead of `ComparisonOptions.Default` on line 80. One-word change; fix it together with the options work so the two never diverge again.

**Verifica adversariale**

Verbatim in the file: ScriptCommand.cs:73-85 computes `opts` (clearing IgnorePermissions when --include-permissions is set), then calls `.Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default)` on line 80 and passes `opts` only to Generate on line 84. The reviewer's own inertness analysis is correct: ComparePermissions (ComparisonEngine.cs:106-134) takes no options parameter, so nothing in the engine reads IgnorePermissions today — no user-visible bug now, a latent divergence the moment any Ignore* flag becomes engine-side live. Low/S confirmed; it is a one-token change on line 80.

**Nota**: Land it in the same commit as any options work so the script command's diff and its emitted script can never disagree.

---

## [low] force-column-order-silent-noop  ·  scriptgen-correctness

**ForceColumnOrder makes tables Different on ordinal alone, but the emitter can express no reordering and silently emits nothing**

- file: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:399` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

ComparisonEngine.cs:433 `if (options.HasFlag(ComparisonOptions.ForceColumnOrder) && col.Ordinal != other.Ordinal) { return false; }` classifies the table Different. EmitAlter has no reorder path, so with identical column sets/shapes it appends nothing and returns ""; BuildOneTable then converts that to null (`return string.IsNullOrWhiteSpace(ddl) ? null : ddl;`) and ScriptGenerator.cs:175 skips the batch entirely — no statement, no comment, no exception. Mitigating: grep shows ForceColumnOrder is set nowhere in src/DbDelta.Cli or src/DbDelta.App.Avalonia, so the flag is currently unreachable from both front ends.

**Scenario di fallimento**

A consumer of DbDelta.Core (or a future UI checkbox) enables ComparisonOptions.ForceColumnOrder. dbo.Customer has the same 8 columns on both sides but Email and Phone are swapped. The grid shows the table as Different; the generated script contains no statement for it at all; after "successful" deployment a re-compare still reports Different. The user is stuck in a diff that no deploy can clear and gets no explanation.

**Fix proposto**

When EmitAlter yields an empty body for a Different pair, emit `-- WARNING: [s].[t] differs only in column order; a table rebuild is required` (or route the pair through EmitRebuild, which already copies data by name). Either way the script must never be silently empty for a Different pair.

**Verifica adversariale**

Accurate about the code: ComparisonEngine.cs:433 returns false on an ordinal mismatch under the flag; EmitAlter (TableScriptEmitter.cs:184-278) has no reorder path so with identical column sets/shapes it returns ""; BuildOneTable (ScriptGenerator.cs:399-403) maps that to null and the emit loop at :174-175 skips the batch with no statement, comment, or exception. Unreachability also confirmed — a repo-wide grep for ForceColumnOrder hits only Options/ComparisonOptions.cs:28 (the enum member) and ComparisonEngine.cs:433; neither src/DbDelta.Cli nor src/DbDelta.App.Avalonia ever sets it, so no shipped front end can trigger the scenario and real-world impact today is nil. Keep at low. Worth noting for the owner: the 'Different pair → empty batch' symptom IS reachable today under default options through the CHECK IsDisabled/IsNotForReplication path (finding check-constraint-disabled-and-nfr-not-emitted), because TableScriptEmitter.ConstraintShapeEqual:430-431 ignores those flags while ComparisonEngine.cs:488-491 counts them — so a generic 'never emit an empty batch for a Different pair' guard would pay for itself there, not here.

**Nota**: Fix the guard generically (assert/comment when a Different pair yields an empty body) — it catches the reachable CHECK-flags case as well as this unreachable one.

---

## [low] unredacted-exception-text-in-error-surface  ·  sql-injection-security

**Connection-string parse errors are surfaced with the raw exception message, which can contain a password fragment**

- file: `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs:164` · effort **S** · requisito **sicuro** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

                LastError = $"Source connection string parse failed: {parseEx.Message}\n"
                    + $"len={srcCs.Length}\n"
                    + $"sanitised='{System.Text.RegularExpressions.Regex.Replace(srcCs, @"(?i)(password|pwd)\s*=\s*[^;]+", "$1=***")}'";
Only the echo of the string is sanitised; parseEx.Message is interpolated raw. For a password containing ';' the SqlConnectionStringBuilder message is literally "Keyword not supported: 'p@ss'." - a fragment of the password. Lines 187, 194 and 216 also assign LastError = <exception/Error>.Message with no redaction. Mitigating: the only view bound to LastError is ConnectionPickerView.axaml:142, and ConnectionPickerView is never instantiated anywhere in the shell (grep across src/ finds only its own .axaml/.axaml.cs), so today the string is built and thrown away.

**Scenario di fallimento**

Latent. The moment LastError is bound into MainWindow (or written to a log, or included in a bug-report/"copia dettagli" affordance), a password containing ';' or '=' appears in the error banner as "Keyword not supported: 'p@ss'" - unredacted, because the redaction is applied only to the neighbouring 'sanitised=' echo. The same applies to any SqlException surfaced verbatim at lines 187/194/216.

**Fix proposto**

Route every assignment to LastError through ConnectionStringRedactor.Redact(...) - including the exception messages, not just the echoed string - and delete the two inlined regex copies at lines 166 and 177 in favour of the shared helper. Then either delete the dead ConnectionPickerView/ConnectionPickerSlot pair or bind LastError somewhere visible, so the error path is exercised rather than silently dead.

**Verifica adversariale**

Accurate including its own mitigation. AppStateViewModel.cs:164-167 and :175-178 interpolate `parseEx.Message` raw and sanitise only the neighbouring echo; :187/:194/:216 assign Error/exception Message with no redaction. And LastError really is dead: repo-wide grep shows the only binding is ConnectionPickerView.axaml:133/142, and ConnectionPickerView is referenced by nothing but its own code-behind and stale plan docs, so the string is built and discarded. Worth adding: the same defect is already LIVE elsewhere, which is where the fix should land — ProjectEndpointPanelViewModel.cs:357-358 and ConnectionEditViewModel.cs:158 display `Redact(ex.Message)` (Redact cannot match "Keyword not supported: 'p@ss'"), and ConnectionTester.cs:21 returns `ex.Message` on the parse-failure path with no Redact at all. Low/latent as filed is the correct call for the cited lines.

**Nota**: Correct and correctly self-mitigated. The same unredacted-parser-message leak is already user-visible at ProjectEndpointPanelViewModel.cs:358, ConnectionEditViewModel.cs:158 and ConnectionTester.cs:21 (that one skips Redact entirely) — fix those with it.

---

## [low] culture-dependent-cli-and-header-output  ·  tests-cicd-arch

**CLI text output ordering and the deploy-script header timestamp are culture-dependent**

- file: `src/DbDelta.Cli/Output/TextFormatter.cs` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`g.OrderBy(p => p.Identity.SchemaName).ThenBy(p => p.Identity.ObjectName)` — no `StringComparer`, so it uses the current culture's linguistic comparison, unlike every ordering in Core/`HtmlReportGenerator` which passes `StringComparer.Ordinal`/`OrdinalIgnoreCase` explicitly. Separately `DeployScriptBuilder.cs:37` does `header.AppendLine($"-- Generated : {nowUtc:yyyy-MM-dd HH:mm:ss} UTC")` with no `CultureInfo.InvariantCulture`; in a custom date/time format string `:` is the *TimeSeparator* placeholder, replaced by the culture's value.

**Scenario di fallimento**

A CI job diffs `dbdelta compare --format text` output against a checked-in baseline. The baseline was produced on an en-US runner; a self-hosted tr-TR or fi-FI agent orders `dbo.Item` vs `dbo.Ítem` (or `I`/`ı`) differently and the job fails with no schema change. On a fi-FI desktop the saved alignment script's header reads `-- Generated : 2026-07-30 14.05.22 UTC` instead of `14:05:22` — cosmetic, but it is inside the artefact users archive as a deploy record.

**Fix proposto**

`OrderBy(p => p.Identity.SchemaName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Identity.ObjectName, StringComparer.OrdinalIgnoreCase)` and `nowUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)`. Optionally add `<InvariantGlobalization>` or a culture-sweep xunit fixture so the whole suite runs under a non-en culture once.

**Verifica adversariale**

src/DbDelta.Cli/Output/TextFormatter.cs:21 is `g.OrderBy(p => p.Identity.SchemaName).ThenBy(p => p.Identity.ObjectName)` with no StringComparer, so it uses CurrentCulture linguistic comparison. The contrast the reviewer draws is real: ComparisonEngine.cs:44,72,93,94,120, HtmlReportGenerator.cs:142,166,167, RoleScriptEmitter.cs:24 and ScriptGenerator.cs:478 all pass StringComparer.Ordinal/OrdinalIgnoreCase explicitly — TextFormatter is the outlier. src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs:35 is `header.AppendLine($"-- Generated : {nowUtc:yyyy-MM-dd HH:mm:ss} UTC")`, an interpolated custom format string with no IFormatProvider, so it resolves against CurrentCulture and `:` is substituted with DateTimeFormat.TimeSeparator. No mitigating global setting exists: Directory.Build.props has no InvariantGlobalization, and the SDK's default analyzer level does not turn CA1304/CA1305/CA1310 into the errors that TreatWarningsAsErrors would catch.

**Nota**: Accurate, including the subtlety that only the time part is at risk — `-` in `yyyy-MM-dd` is a literal, whereas the date separator placeholder is `/`. low/S correct.

---

## [low] docker-fixtures-start-outside-guard  ·  tests-cicd-arch

**Docker-gated test fixtures call StartAsync() outside the skip guard, so 'Docker present but wrong container mode' fails the job instead of skipping**

- file: `tests/DbDelta.Persistence.IntegrationTests/Sql/SqlExecutorTests.cs` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

`InitializeAsync` probes with `IsDockerAvailableAsync()` → TCP localhost:2375, falling back to `File.Exists(@"\\.\pipe\docker_engine")` — a check for *a daemon*, not for a daemon that can run a **Linux** mssql image — then calls `await _container.StartAsync()` with no try/catch. The class comment promises 'if Docker is not available, each test is skipped via Assert.Skip'. CompatMatrixTests has the same shape (`SkipUnlessEnabledAsync` then unguarded `StartAsync`, CompatMatrixTests.cs:56-59), and `LiveDbFixture.InitializeAsync() => new(Container.StartAsync())` has no probe at all. Note this project IS in the windows-latest CI job (`ci.yml:49`), where Docker runs in Windows-container mode.

**Scenario di fallimento**

On a Windows box (dev machine or the `windows-latest` runner) with dockerd running in Windows-container mode, the named pipe exists → the probe reports 'available' → `MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").StartAsync()` throws DockerApiException/DockerImageNotFoundException from `InitializeAsync`. xUnit reports all three SqlExecutor tests as **errored**, so the required `build + non-DB tests (windows)` job goes red for an environment reason with a Docker stack trace — the exact outcome the skip logic was written to prevent. Locally, `dotnet test DbDelta.sln` on a Docker-less machine fails LiveDb.IntegrationTests and Cli.AcceptanceTests outright rather than skipping.

**Fix proposto**

Wrap the start: `try { await _container.StartAsync(); _connectionString = …; } catch (Exception ex) { _skipReason = ex.Message; }` and have each test `Assert.Skip(_skipReason)` when the connection string is null — the per-test skip plumbing already exists. Apply the same three-line pattern to `LiveDbFixture` and `CompatMatrixTests`. Better still, stop probing for a daemon and let the failed start *be* the signal.

**Verifica adversariale**

Code shape is exactly as described in all three places. tests/DbDelta.Persistence.IntegrationTests/Sql/SqlExecutorTests.cs:18-36 — IsDockerAvailableAsync (:135-148) probes TCP localhost:2375 then falls back to `File.Exists(@"\\.\pipe\docker_engine")`, i.e. it detects *a daemon*, not one that can run a Linux mssql image, and :34 `await _container.StartAsync();` has no try/catch, so any start failure throws out of InitializeAsync and xUnit errors all three tests instead of skipping — directly contradicting the class comment at :11 ('if Docker is not available, each test is skipped via Assert.Skip'). CompatMatrixTests.cs:49 SkipUnlessEnabledAsync then :56 unguarded StartAsync — same shape. LiveDbFixture.cs:18 `public ValueTask InitializeAsync() => new(Container.StartAsync());` — no probe at all, as claimed. And the project IS in the windows job (ci.yml:52). The per-test skip plumbing the fix reuses already exists (SqlExecutorTests.cs:49-52,74-77,110).

**Nota**: Severity lowered to low and one claim downgraded to unverified. The specific 'windows-latest goes red' scenario I could not reproduce — the job is evidently green on today's HEAD, which implies the probe returns false on the runner (2375 is not exposed and File.Exists on \\.\pipe\ is unreliable), so the described CI failure is speculative. What IS confirmed is the local developer case and the code-level contradiction. Note the comment at SqlExecutorTests.cs:26-29 shows the authors already hit this class of problem ('Assert.Skip cannot intercept DockerImageNotFoundException') and worked around it by pinning the image rather than wrapping the start — so the reviewer's 'let the failed start be the signal' is the fix they should have made. Zero product impact: test infrastructure only.

---

## [low] script-command-dead-options-variable  ·  tests-cicd-arch

**`dbdelta script` computes comparison options from --include-permissions then passes ComparisonOptions.Default to Compare anyway**

- file: `src/DbDelta.Cli/Commands/ScriptCommand.cs` · effort **S** · requisito **quality** · verdetto **CONFIRMED** · coperto da test: False

**Evidenza**

```
ComparisonOptions opts = ComparisonOptions.Default;
if (emitPerms) { opts &= ~ComparisonOptions.IgnorePermissions; }
ComparisonResult comparison = new ComparisonEngine()
    .Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default);   // <- opts ignored
string script = new ScriptGenerator().Generate(comparison, selection: null, options: opts, …);
```
`opts` reaches Generate but not Compare. It happens to be harmless today only because `ComparisonEngine` never reads `IgnorePermissions` (`ComparePermissions` runs unconditionally). `grep -rn 'include-permissions' tests/` → zero hits: the flag has no test at all.

**Scenario di fallimento**

The moment `ComparisonEngine` starts honouring `IgnorePermissions` (the natural fix for the 14 dead flags), `--include-permissions` silently stops working: the engine, given the hardcoded Default, skips producing Permission pairs, so `Generate` has nothing to emit and the script contains no GRANT/REVOKE while the CLI reports success and exit 0. The same latent trap applies to any future `--ignore-indexes`.

**Fix proposto**

Pass `opts` to `Compare`. Add one acceptance test: seed a GRANT on the source only, run `script --include-permissions`, assert the output contains `GRANT SELECT ON` and that without the flag it does not.

**Verifica adversariale**

src/DbDelta.Cli/Commands/ScriptCommand.cs:73-85 is verbatim as quoted: `opts` is computed at :73-77 (`ComparisonOptions.Default`, then `opts &= ~IgnorePermissions` when --include-permissions is set), but :80 passes the literal `ComparisonOptions.Default` to Compare while :84 passes `opts` to Generate. The 'harmless today' reasoning also checks out: ComparisonEngine.cs:29 calls `ComparePermissions(a.Permissions, b.Permissions)` unconditionally — IgnorePermissions is read only at ScriptGenerator.cs:278 — so the Permission pairs always exist and the generator's gate is what --include-permissions actually opens. Zero test coverage confirmed: `grep -rn 'include-permissions' tests/ src/` returns a single hit, ScriptCommand.cs:36 (the option declaration itself).

**Nota**: Confirmed exactly. This one and project-comparison-options-ignored are the same root cause at two call sites — fixing the latter by threading options through Compare must not miss this line, or --include-permissions inverts from 'dead variable' to 'silently broken flag'.

---

# Annex — 62 migliorie proposte

## [fondamentale/L] reverse-script-generation  ·  app-ui-robustness

**Generate a reverse (rollback) script alongside every deploy** — requisito **resiliente**

The owner's stated top concern is undo, and today nothing in the app can undo a committed deploy: the in-script transaction only protects against a failed run, and there is no backup step. The information needed for a reverse script is already in hand — every DifferencePair carries SideA and SideB, so inverting the pair set (swap sides, OnlyInA↔OnlyInB) and running it through the existing ScriptGenerator yields the script that returns the target to its pre-deploy shape for every non-data-bearing object.

**Sketch**

Add `DeployScriptBuilder.BuildReverse(pairs, …)` that maps each pair to its inverse and reuses ScriptGenerator; in the execute flow, write the reverse script to %LOCALAPPDATA%\DbDelta\rollback\{timestamp}.sql BEFORE executing and surface its path in the outcome panel. Be explicit in the UI that a reverse script cannot restore rows lost to a DROP/narrowing — that still needs a backup, so pair it with a "BACKUP DATABASE … TO DISK" pre-step offered when OnlyInTargetCount > 0.

---

## [fondamentale/L] no-undo-path-at-all  ·  tests-cicd-arch

**There is no undo: no pre-deploy backup, no reverse (rollback) script, no post-commit recovery** — requisito **resiliente**

The owner's stated top concern. Assessed honestly, what exists today is only *pre*-commit atomicity: `DeploymentScriptWriter` wraps the generated script in SET XACT_ABORT ON + BEGIN TRANSACTION + per-batch `IF @@ERROR <> 0 SET NOEXEC ON` + a verdict block that ROLLBACKs (covered by DeployErrorHandlingTests, which is genuinely good). Once COMMIT succeeds there is nothing — no snapshot, no generated inverse script, no history of what was applied. A DROP COLUMN or a table rebuild that committed is unrecoverable from inside the tool. And `dbdelta apply` does not even give the atomicity when the supplied script lacks the envelope (see apply-has-no-transaction).

**Sketch**

Cheapest meaningful step first: the diff already holds BOTH sides of every pair, so a reverse script is the same ScriptGenerator run with A and B swapped — emit `<name>.rollback.sql` beside every generated deploy script and show it in the confirm-execute dialog. Second: before executing, `BACKUP DATABASE … TO DISK` (or `CREATE DATABASE … AS SNAPSHOT OF` on Enterprise) with the path surfaced in the UI, gated by a checkbox defaulted ON. Third: an applied-script journal (timestamp, endpoints, script hash, the script text) under %LOCALAPPDATA% so the user can see what was deployed and re-generate the inverse later. Test each with the round-trip harness: apply forward, apply reverse, assert the target is byte-identical to its pre-deploy snapshot.

---

## [fondamentale/M] mvp-inverse-down-script  ·  undo-rollback

**Minimum viable undo: generate down.sql by swapping the two sides of the selected pairs, and write up/down/meta to a per-run deploy folder before executing** — requisito **resiliente**

This is the cheapest real undo the codebase can have, because the generator is already symmetric. A `DifferencePair` is `(Identity, Status, SideA, SideB)` and `SideB` *is* the captured pre-deploy target object. Swap the sides and flip OnlyInA/OnlyInB, feed the result to the existing `DeployScriptBuilder.Build`, and `ScriptGenerator` emits the DDL that turns the target back into its pre-deploy shape — transaction envelope, dependency ordering, XACT_ABORT and all. No new subsystem, no new emitter, no data model change. It also fixes `execute-leaves-no-audit-trail` in the same edit, since writing the folder is where the down script lands.

**Sketch**

1. `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs` — add ~15 lines:
```csharp
public static string BuildInverse(IReadOnlyList<DifferencePair> selectedPairs, string src, string tgt, DateTime nowUtc)
    => Build([.. selectedPairs.Select(Invert)], tgt, src, nowUtc);

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
2. `MainWindowViewModel.ExecuteOnTargetAsync` — before `new ConfirmExecuteViewModel(...)`: create `%LOCALAPPDATA%/DbDelta/deploys/{yyyyMMdd-HHmmss}-{server}-{db}/` (reuse the app-data root helper in `src/DbDelta.Persistence/Json/ProjectsFolder.cs`) and write `up.sql`, `down.sql` (= `BuildInverse(...)`), `meta.json` (redacted endpoints via `ConnectionStringRedactor`, object list, DbDelta version, UTC timestamp). Append the `SqlBatchResult` to meta.json after the dialog closes.
3. `ConfirmExecuteViewModel` — add `string DeployFolder` and `string? RollbackScriptPath`; `ConfirmExecuteDialog.axaml` idle panel gets one line "Script di rollback: down.sql" and the outcome panel a neutral (raised-grey filled, 32 px) button "Carica script di rollback" that opens the folder / loads down.sql into the script view.
4. User flow: Esegui → dialog already shows that a down script was written → deploy commits → user realises it was wrong → Chiudi, click "Carica script di rollback", read it, then run it through the same execute path (or `dbdelta apply --script down.sql`). One extra click versus nothing today.
5. `tests/` — one golden test asserting round-trip: `Build(pairs)` then `BuildInverse(pairs)` and, on a Testcontainers DB, apply-then-undo leaves the target schema identical to the start (`DependencyRoundTripTests` already has the harness for this).

Honest scope: inverse DDL restores **schema, never row data**. It fully undoes: objects created by the deploy (inverse = DROP), module bodies overwritten by CREATE OR ALTER (inverse re-applies the captured `SideB` body — this alone fixes the hotfix-clobber scenario), added columns / indexes / constraints / FKs, sequence-synonym-UDT changes, users/roles/permissions. It restores structure but **not rows** for: `DROP TABLE` of a target-only table (the inverse re-creates it empty), `DROP COLUMN` (the column returns all-NULL, and returns as NOT NULL-impossible if it was NOT NULL without a default), a narrowed `ALTER COLUMN` (truncated text does not come back), and the rebuild path when the temp copy dropped a column. Say this in the dialog in one sentence — the user must not read "rollback script" as "backup".

---

## [fondamentale/M] copy-only-backup-gate  ·  undo-rollback

**The only real undo for row data: offer a COPY_ONLY backup of the target immediately before a deploy classified as data-destroying** — requisito **resiliente**

Generated inverse DDL cannot resurrect deleted rows — that is a physics problem, not a design gap. Since `SqlExecutor` already runs arbitrary batches against the target, a pre-deploy backup is a handful of lines and is the only mechanism that makes the owner's requirement ("undo every change") literally true for data. Gate it on the risk list rather than always, so ordinary module-only deploys stay one click.

**Sketch**

`ConfirmExecuteViewModel` gains `bool BackupFirst` (checkbox, pre-checked and non-clearable when the risk list contains a data-destroying entry) and `string BackupPath`. On Esegui, run a *separate* `SqlExecutor.ExecuteAsync(target, "BACKUP DATABASE [db] TO DISK = N'…' WITH COPY_ONLY, INIT, CHECKSUM", ct, useOwnTransaction: false)` before the deploy script, abort the deploy if it fails, and record the path in `meta.json`. `COPY_ONLY` keeps the customer's log chain intact. Choose the path with a server-side default (`SELECT SERVERPROPERTY('InstanceDefaultBackupPath')`) since the file is written by the SQL Server service account, not the client — and surface a clear "backup non possibile su questa istanza" when it fails (Azure SQL DB, no filesystem, insufficient rights), in which case require an explicit typed acknowledgement to proceed. Do **not** reach for database snapshots (`AS SNAPSHOT OF`) as the primary mechanism: Enterprise-edition only, and they pin the source database's files. Restore is deliberately out of scope — the meta.json path plus the down.sql is enough for a DBA to act; DbDelta should never issue `RESTORE`.

---

## [fondamentale/M] shared-sql-quoting-helpers-plus-arch-test  ·  sql-injection-security

**One SqlIdentifier.Quote / SqlLiteral.Escape pair, enforced by an architecture test** — requisito **sicuro**

The two injection findings above are the same absent abstraction seen from two angles: 40+ sites concatenate `[` + catalog string + `]` and 1 site concatenates `'` + catalog string + `'`, and the correct escape exists exactly once in the repo (DeploymentScriptWriter.cs:34) where it happened to be needed. Fixing the call sites without adding the guard means the 41st emitter reintroduces the bug. The repo already has tests/DbDelta.Architecture.Tests, which is the cheapest place to make the rule permanent.

**Sketch**

Add DbDelta.Core.ScriptGen.SqlText with Quote(id) => "[" + id.Replace("]","]]") + "]" and Literal(s) => "'" + s.Replace("'","''") + "'". Convert every emitter. Then an Architecture.Tests rule scanning the ScriptGen source files for the literal sequences "[\"" / "'[' " adjacent to an identifier expression, failing the build on new occurrences.

---

## [fondamentale/M] rollback-script-generation  ·  scriptgen-correctness

**Generate a reverse (rollback) script alongside the forward script** — requisito **resiliente**

The owner's stated top concern (RESILIENTE) has no implementation today. The transaction wrapper in DeploymentScriptWriter only protects against failure *during* the deploy; once COMMIT runs there is no undo. The generator already has both sides of every pair in hand, so producing the inverse script is mostly a matter of calling Generate with SideA and SideB swapped, and it is the single highest-value resilience feature: the operator gets a file that restores the target's schema shape.

**Sketch**

Add `ScriptGenerator.GenerateRollback(result, selection, options)` that inverts each pair (OnlyInA↔OnlyInB, Different with sides swapped) and reuses the whole pipeline. Save it next to the forward script in the GUI (DbDelta-<ts>-rollback.sql) and print its path in the ConfirmExecuteDialog outcome panel. Document clearly that it restores schema, not data — data loss from DROP COLUMN / narrowing is still one-way, which is exactly why the destructive-operation warnings above matter.

---

## [fondamentale/M] rollback-script-from-swapped-sides  ·  diff-engine-correctness

**Generate the reverse (undo) script for free by re-running the emitter with SideA/SideB swapped** — requisito **resiliente**

Every DifferencePair already carries BOTH sides of the object (DifferencePair.cs: SideA + SideB), and ScriptGenerator is a pure function of a ComparisonResult. A rollback script for exactly the selected objects is therefore obtainable by mapping each selected pair to `pair with { Status = inverted, SideA = pair.SideB, SideB = pair.SideA }` and calling Generate again — no new catalog reads, no snapshot subsystem. That directly serves the owner's biggest stated concern (resilienza / undo) at a fraction of the cost of a backup-based approach. Caveats to state honestly in the UI: it restores SCHEMA, not data — a dropped table/column cannot be un-dropped, so the reverse script must refuse (or loudly warn) for OnlyInB drops and for the identity-rebuild path.

**Sketch**

Add `ScriptGenerator.GenerateReverse(ComparisonResult, selection, options, dependencies)` that inverts each pair (OnlyInA↔OnlyInB, Different keeps both sides swapped), reuses the same emitter pipeline, and prefixes a `-- WARNING: schema-only rollback; data removed by DROP TABLE/COLUMN cannot be restored` banner listing the destructive statements it could not invert. Save it next to the deploy script in DeployAsync and offer it in the ConfirmExecute dialog.

---

## [fondamentale/M] roundtrip-idempotence-test  ·  diff-engine-correctness

**Add an apply-then-recompare round-trip test to the LiveDb integration suite** — requisito **affidabile**

Every high finding above (missing schema creation, app dependency order, system-named constraints, collation churn, GRANT ON DATABASE) shares one signature: the generated script either fails to execute or does not actually flatten the diff. A single Testcontainers test — build two DBs, compare, apply the script, re-compare and assert zero non-Identical pairs — would have caught all of them, and it is the only test shape that validates the engine and the emitters together. The suite currently has 82 test files and none of them close this loop.

**Sketch**

tests/DbDelta.Providers.LiveDb.IntegrationTests: per scenario, spin one SQL Server container with two databases, run ScriptGenerator (with Dependencies), execute via SqlExecutor, then re-run ComparisonEngine and assert `result.Differences.All(d => d.Status == Identical)`. Seed the scenario list from the parity cases already documented in docs/ (new schema, view-over-function, unnamed default, differing default collations, permissions).

---

## [fondamentale/M] reader-coverage-manifest  ·  livedb-readers

**Make silent incompleteness loud: have every reader report what it skipped** — requisito **affidabile**

Every high-severity finding in this dimension is the same failure mode — a WHERE clause or an unread column turns real drift into "Identical", and the user has no way to know. The readers already know exactly which rows they discarded (index types not in (1,2), triggers whose parent is not a table, modules with NULL definitions, sequences that overflow, tables with temporal_type<>0). Surfacing a `SkippedObjects`/`Warnings` collection on `Database` and rendering it as a banner in the GUI + a section in the HTML/JSON report converts every current silent lie into a visible caveat, without needing to implement support for any of the features. This is the single cheapest thing that raises trust in the diff.

**Sketch**

`Database` gains `IReadOnlyList<SourceWarning> Warnings`; each reader runs a second cheap COUNT query (or counts filtered rows in the same pass) and appends e.g. `new SourceWarning("Index", "dbo.FactSales", "clustered columnstore index CCI_Fact not compared")`. GUI: a persistent amber strip "Confronto parziale: 7 oggetti non analizzati" with a details popup. CLI: print to stderr and add them to the JSON report.

---

## [fondamentale/M] rollback-script-generation  ·  redgate-parity

**Generate the inverse (rollback) script alongside every deploy script** — requisito **resiliente**

THE owner's #1 stated concern, and today the honest answer is: nothing exists. `grep -rni backup src/` → **zero hits**. `grep -rni rollback src/` → only the in-script `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION` (DeploymentScriptWriter.cs:56) and `SqlExecutor`'s catch block. That covers exactly one case: the deploy *fails*. It covers none of the case the owner actually worries about — the deploy **succeeds** and was the wrong thing to do. Redgate has no undo button either; its documented rollback path is snapshot-before → compare snapshot-as-source → deploy back (docs/00_overview.md §4.4 'Rollback Strategy'). DbDelta has neither half of that path. Note the material advantage available here: `ComparisonEngine.Compare(a, b)` is symmetric and pure, so `Compare(target, source)` already yields the exact inverse difference set — the rollback script is `ScriptGenerator.Generate` over the swapped comparison. This is close to free and would put DbDelta *ahead* of Redgate on the requirement the owner cares most about.

**Sketch**

In the same run that produces the deploy script, also run the comparison with the endpoints swapped and emit `<name>.rollback.sql`. Honest caveats to print in the rollback script header: a DROP of a source-only object cannot restore its rows (the rollback re-CREATEs an empty table), and a table rebuild is not reversible data-wise. Pair it with a `dbdelta rollback --script <file>` verb. Gate the GUI's 'Esegui' button on having written the rollback script first.

---

## [fondamentale/M] schema-snapshot-source  ·  redgate-parity

**Schema snapshot files (.snp equivalent) as both a comparison source and a target** — requisito **resiliente**

Snapshots are the load-bearing primitive behind three separate Redgate workflows documented in docs/00_overview.md §4.5: pre-deploy baseline (→ rollback), scheduled drift detection against a baseline, and offline/historical audit. DbDelta has none of them — `ISchemaSource` has exactly one implementation (`LiveDbSource`) and the BACKLOG parks this in the v2 pile (§D). The cost here is unusually low: `Database` is a plain immutable record graph of records, so `System.Text.Json` round-trips it with no new format work. Highest value-per-line item in the entire parity list, and it is the missing half of the rollback story.

**Sketch**

`JsonSchemaSource : ISchemaSource` reading/writing a versioned `{schemaVersion, capturedUtc, server, database, Database}` envelope. `dbdelta snapshot --source <conn> --out prod-2026-07-30.dbds`; then `--source`/`--target` accept either a connection string or a `.dbds` path. Add a schemaVersion gate so an old snapshot fails loudly rather than deserializing into a half-populated model (which would read as false-negative 'Identical').

---

## [fondamentale/M] deployment-warnings-taxonomy  ·  redgate-parity

**Deployment warnings taxonomy + --abort-on-warnings** — requisito **sicuro**

Redgate raises ~40 named warnings and gates the wizard on them (docs/00_overview.md §4.4, plus `/AbortOnWarnings:High|Medium|Low`). DbDelta raises **zero**: `grep -rni warning src/` hits only design-system brush names, credential-store comments, and the `WITH ENCRYPTION` emitter notes. `ConfirmExecuteViewModel` shows four counts (ObjectCount / DifferentCount / OnlyInTargetCount / OnlyInSourceCount) and the reassurance text 'in caso di errore viene eseguito il rollback completo' — nothing tells the user that row 7 of the plan is a full table rebuild with a data copy, or that row 12 drops a populated table. The information is already computed and thrown away: `ScriptGenerator` line 74 calls `TableScriptEmitter.RequiresFullRebuild(src, tgt)` and populates `rebuildTargets` — that single set is a data-loss-risk warning nobody surfaces.

**Sketch**

A `DeploymentWarning(Severity, Code, ObjectIdentity, Message)` list returned alongside the script. Start with the eight that matter and are all derivable from data already in hand: TableWillBeRebuilt (reuse `rebuildTargets`), ColumnDropped, ColumnTypeNarrowed (compare precision/length/scale), NotNullAddedWithoutDefault, TableDropped, ForeignKeyDropped, IdentityReseeded, ObjectDependsOnDroppedObject (the DependencyResolver already has the edges). Render them as a red list in ConfirmExecuteDialog above the Esegui button, add `--abort-on-warnings <high|medium|low>` to the CLI, and add `SELECT COUNT(*)` row counts so 'drops a populated table' can be stated with a number.

---

## [fondamentale/M] comparison-options-surface  ·  redgate-parity

**A real comparison-options surface: UI page, --options CLI switch, project persistence** — requisito **parity**

Redgate's ~60 options are the difference between a demo and a tool people can run against their own databases. DbDelta currently has **no user-facing options at all**: `ComparisonOptions.Default` is hardcoded at all five call sites (CompareCommand:66, ReportCommand:79, ScriptCommand:80, AppStateViewModel:199 — plus ScriptGenerator's parameter default), there is no `--options` switch, and `ProjectSetupDialog.axaml` binds no options at all. Ranked by what actually blocks real-world use, from the code as it stands: (1) IgnoreConstraintAndIndexNames + IgnoreSystemNamedConstraintAndIndexNames — without them two builds of the same schema never compare clean (see the system-named finding); (2) IgnoreCollations — without it a cross-collation compare marks every string column different; (3) IgnoreComments — advertised in Default, not implemented; (4) IgnoreIdentitySeed — an identity whose current seed drifted with usage triggers a full table rebuild; (5) IgnoreFillFactor / IgnoreDataCompression / IgnoreFileGroups — needed the moment index physical properties are read; (6) IgnoreQuotedIdentifiersAndAnsiNullSettings + IgnoreSquareBrackets — cosmetic module-text drift; (7) IgnoreTriggers / IgnoreIndexes / IgnoreKeys (last two already honoured); (8) IgnorePermissions / IgnoreUsersPermissionsAndRoleMemberships. Below the line for v1: DecryptEncryptedObjects, treat-views-as-tables, IgnoretSQLt, IgnoreMigrationScripts.

**Sketch**

One options panel (a scrollable list of the 32px checkboxes the design system already mandates) bound to a single `ComparisonOptions` value that lives on the project, is passed to every `Compare`/`Generate` call, and is settable from the CLI as `--options IgnoreComments,IgnoreCollations` / `--options Default,IgnoreForeignKeys` / `--options None`, matching Redgate's syntax. Ship only the flags that are actually implemented — a checkbox that does nothing is worse than a missing checkbox.

---

## [fondamentale/M] per-kind-roundtrip-matrix  ·  tests-cicd-arch

**One parameterised read→diff→script→apply→recompare test per object kind, with no kind filter** — requisito **affidabile**

This is the single highest-value missing test for a schema-diff tool and the only thing that can prove a kind actually deploys. Today an untested kind is a kind that may silently not deploy, and the two existing round-trip tests hard-filter their convergence assertion to 3 kinds each, so adding a 14th kind requires no proof of anything. Making the assertion unfiltered turns the test into a ratchet: a new kind cannot be declared supported until it converges.

**Sketch**

In DbDelta.Providers.LiveDb.IntegrationTests (already per-PR in the linux CI job): `[Theory][MemberData(nameof(KindSeeds))]` where each row is (kindName, seedSql). Body: seed source, LoadAsync both sides, Generate with dependencies, SqlExecutor.ExecuteAsync, reload target, `re.Differences.Should().OnlyContain(d => d.Status == Identical)`. Then run the same theory a second time against the already-converged target to prove idempotency (the second script must be envelope-only).

---

## [fondamentale/S] scriptdom-parse-gate-on-every-emitted-script  ·  scriptgen-correctness

**Parse every generated script with ScriptDom in the test suite** — requisito **affidabile**

Microsoft.SqlServer.TransactSql.ScriptDom is already a DbDelta.Core PackageReference and its DLL already ships into the golden-test output folder, yet nothing parses the emitted DDL. The consequence is visible: the CREATE TABLE named-DEFAULT syntax error is *pinned* by an approved golden file, so the suite actively defends invalid T-SQL. A single shared assertion (`TSql160Parser.Parse` → assert zero errors) applied to every golden output and every ScriptGenerator unit-test result would have caught that finding, and would catch the whole class cheaply and forever.

**Sketch**

Add a test helper `AssertParses(string tsql)` that runs TSql160Parser (initialQuotedIdentifiers: true) over the batch-split script and fails on any ParseError. Call it from a Verify converter used by all golden tests plus a [Theory] over the ScriptGenerator unit-test fixtures.

---

## [alto/L] scripts-folder-provider  ·  redgate-parity

**Scripts folder as a comparison source and target** — requisito **parity**

This is Redgate's most-used non-live source (docs/00_overview.md §4.6, workflows 4 and 5) and the entire basis of database-as-code: one .sql file per object in a Git working copy, compared both ways. Without it DbDelta cannot participate in a source-controlled schema workflow at all, which rules out the CI/CD and code-review use cases that make up half of SQL Compare's stated user base (§1 target-user table). Parked as v2 in BACKLOG §D. Writing the folder is much easier than reading it — the emitters already produce per-object DDL, whereas reading requires a T-SQL parser (the real cost, and the reason `ThrowOnFileParseFailed`/`IgnoreParserErrors` exist on the Redgate side).

**Sketch**

Split it: ship `--scripts-out <dir>` first (write-only, `<dir>/<Kind>/<Schema>.<Name>.sql`, reusing the existing emitters) — that alone unlocks 'export schema to Git' and PR-based schema review. Defer the read side (`ScriptsFolderSource`) until there is a parser decision; `Microsoft.SqlServer.TransactSql.ScriptDom` is the boring choice and handles the whole dialect.

---

## [alto/M] deploy-risk-classifier  ·  undo-rollback

**One risk classifier over the selected pairs, reused by the confirm dialog, the CLI --abort-on-warnings gate, and the backup gate** — requisito **sicuro**

Three separate features (`destructive-ddl-emitted-unflagged`, the backup gate, the spec'd `/AbortOnWarnings` at docs/01_architecture.md:1154) all need the same answer: which selected pairs will destroy data or need a rebuild. Compute it once from the pair list — no new source of truth, no re-parsing of the emitted SQL.

**Sketch**

New `src/DbDelta.Core/ScriptGen/DeployRisk.cs`: `record DeployRisk(ObjectIdentity Target, RiskKind Kind, string Detail)` with `RiskKind { DropObject, DropColumn, NarrowColumn, TableRebuild, ComputedFlip }`. A static `Classify(IReadOnlyList<DifferencePair>)` walks the same branches `TableScriptEmitter.EmitAlter`/`EmitDrop` take — reuse the existing `TableScriptEmitter.RequiresFullRebuild` for the rebuild case and `SqlTypeFormatter` for a length/precision comparison on the narrowing case. Consumers: `ConfirmExecuteViewModel.Risks` (Warning-brush band + `AcknowledgedRisk` checkbox gating `CanExecute`), `ApplyCommand`/`ScriptCommand` `--abort-on-warnings`, and `copy-only-backup-gate`. Keep the classifier dumb and over-inclusive: a false "this may lose data" costs a checkbox, a false negative costs a table.

---

## [alto/M] pre-execute-drift-recheck  ·  undo-rollback

**Re-read the selected target objects immediately before executing and abort on drift** — requisito **affidabile**

Turns the lost-update scenario from silent into a hard stop, and gives the post-COMMIT indeterminate case a verification step. It is also the cheapest way to make a saved script safe to re-run days later.

**Sketch**

In `ExecuteOnTargetAsync`, before showing the dialog, re-read only the selected objects through the existing `DbDelta.Providers.LiveDb` readers and compare each against the pair's captured `SideB` using the existing comparison predicates (`BodyNormalizer.ExpressionsEqual`, the `*ShapeEqual` helpers in `ScriptGenerator`/`TableScriptEmitter`). Any mismatch → block with "la destinazione è cambiata dopo il confronto: rifai il confronto". Pair with `ComparedAtUtc` on `AppStateViewModel` so the dialog can also just show the comparison's age.

---

## [alto/M] hostile-identifier-property-tests  ·  sql-injection-security

**Property test that generates hostile identifiers and asserts every emitter's output is still one well-formed statement** — requisito **affidabile**

The golden-script suite only ever uses names like dbo/T1/Orders, so no test in the repo contains a ']' in an identifier (grep for "]]" in tests/ is empty) and none contains an apostrophe. A generator over {']', '[', '\'', '--', '/*', newline, ';'} would have caught both injection findings and would keep catching them for the 13th object kind and beyond. The repo already has an FsCheck property-test project, so the machinery exists.

**Sketch**

Arbitrary<string> HostileIdentifier = names built from the hostile char set; for each emitter, assert the emitted text contains exactly the expected number of statement terminators and that every `[`...`]` region round-trips back to the input name when un-escaping `]]`. Optionally validate against the target with `EXEC sp_executesql N'SET PARSEONLY ON; ...'` in the LiveDb integration suite.

---

## [alto/M] cli-credentials-off-the-command-line  ·  sql-injection-security

**Give the CLI a way to pass credentials that is not argv** — requisito **sicuro**

`dbdelta compare/script/report/apply --target "Server=PROD;User Id=sa;Password=..."` (ApplyCommand.cs:17, ScriptCommand.cs:26) is the only way to authenticate. On Windows any local user can read another process's command line (wmic/Get-CimInstance Win32_Process), on Linux /proc/<pid>/cmdline is world-readable, and the string lands in shell history and in CI job logs. For a tool whose CLI exists precisely to run in pipelines against production, this is the most likely real-world leak after the deploy-script header.

**Sketch**

Add --source-env/--target-env (read the connection string from a named environment variable), accept `-` to read it from stdin, and/or accept a saved-connection name resolved through ICredentialStore so the CI job only names the entry. Keep the current options but mark them discouraged in --help.

---

## [alto/M] pre-flight-validation-pass  ·  scriptgen-correctness

**Run a pre-flight validation query set before executing, instead of discovering blockers as SQL errors** — requisito **resiliente**

Most findings above surface as a mid-deploy Msg 3726 / 4901 / 5074 / 547 that aborts and rolls back — safe, but the operator learns nothing actionable and cannot plan. The information needed to predict them is cheap: row counts for tables gaining a NOT NULL column, NULL counts for columns becoming NOT NULL, max-length/precision probes for narrowing changes, and existence checks for referenced logins/schemas. Reporting them up front turns a failed deploy into an informed decision.

**Sketch**

Have the generator return, alongside the script, a list of typed preconditions (table + probe SQL + human message). The GUI runs them read-only before showing the confirm dialog and lists any that fail; the CLI exposes them under `dbdelta script --preflight`.

---

## [alto/M] destructive-operation-inventory-in-the-api  ·  scriptgen-correctness

**Make destructive statements first-class output of the generator, not just text in the script** — requisito **sicuro**

Today the only way to know a script drops a table, drops a column, or narrows a type is to read the SQL. ConfirmExecuteViewModel is constructed from objectCount / differentCount / onlyInTargetCount (MainWindowViewModel.cs:636-643) — counts of *objects*, not of destructive *statements* — so the confirmation gate cannot escalate on risk. A structured inventory lets the UI require typed confirmation for the genuinely irreversible subset and lets the CLI exit non-zero without --allow-data-loss.

**Sketch**

Introduce `record RiskNote(RiskKind Kind, ObjectIdentity Target, string Detail)` with kinds DropTable / DropColumn / NarrowType / TableRebuild / SequenceRestart / RevokePermission. Emitters return notes alongside their DDL; Generate aggregates them; DeployScriptBuilder surfaces them; ConfirmExecuteDialog shows them and requires the user to type the target DB name when any irreversible note is present.

---

## [alto/M] round-trip-property-test-per-object-kind  ·  scriptgen-correctness

**Add a model → CREATE → reparse → model round-trip property test per object kind** — requisito **affidabile**

Three findings here (index options, table-type constraints, alias-UDT qualification) share one root cause: no test asserts that the emitted CREATE reproduces the model it came from. tests/DbDelta.Property.Tests already exists with FsCheck, so the generators are largely in place. A round-trip property would mechanically enumerate every property that is silently dropped on the floor and convert the whole 'compared but not emitted / captured but not emitted' class into a compile-time-ish guarantee.

**Sketch**

For each kind, generate a random model instance, emit CREATE, parse it with ScriptDom, project the AST back into the model record, and assert equality on every member. Members deliberately not emitted must be listed in an explicit allow-list in the test, so adding a model property forces an explicit decision.

---

## [alto/M] collation-driven-identifier-comparer  ·  diff-engine-correctness

**Drive identifier comparison from the catalog collation instead of hardcoding one rule** — requisito **affidabile**

SQL Server decides identifier case-sensitivity by the DATABASE collation, which the reader already fetches (Database.DefaultCollation via DATABASEPROPERTYEX). A single comparer resolved from that value (`_CS_` → Ordinal, else OrdinalIgnoreCase) makes the engine correct for both worlds instead of trading one class of bug for another, and gives a natural place to report the (legitimate) mismatch when the two sides disagree on case-sensitivity.

**Sketch**

Resolve `StringComparer IdentifierComparer` once in ComparisonEngine.Compare from a.DefaultCollation/b.DefaultCollation (warn if they disagree), thread it into the identity comparer and every name dictionary. Note that Database.DefaultCollation is currently only used by the collation emitter.

---

## [alto/M] server-version-and-edition-gating  ·  livedb-readers

**Detect server version / engine edition once and gate the query set on it** — requisito **resiliente**

docs/01_architecture.md:328 states "The reader must gate queries by detected version" — nothing in the provider does (`grep -rn SERVERPROPERTY src/` hits only ProjectEndpointPanelViewModel and SqlServerDiscovery). Today the query set is fixed, which blocks two things at once: (a) adding the 2016+ columns the findings above need (is_masked, temporal_type, generated_always_type) while staying safe on anything older, and (b) knowing when a server-scope view is unavailable. `UserReader` is the only reader that leaves the database scope — `LEFT JOIN sys.server_principals AS sp ON sp.sid = p.sid` — which is also the one construct whose availability differs on Azure SQL Database, an explicitly advertised target (README:12). I did not have an Azure instance to verify against, so this is framed as gating rather than a confirmed break: as written, if that view is unavailable the whole load fails with a bare CatalogQueryFailed, and if it is merely permission-filtered every user's LoginName silently reads as NULL, which makes `UsersEqual` (ComparisonEngine.cs:59-62) compare NULL against a real login and report every user as Different.

**Sketch**

At the top of `LoadAsync`: `SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int), CAST(SERVERPROPERTY('EngineEdition') AS int), DB_NAME()`; pass a small `ServerCapabilities` record into every reader; make the login join conditional on EngineEdition<>5 (Azure SQL DB) and fall back to `p.authentication_type_desc` for user classification. Add an Azure SQL DB smoke target to the compat matrix (or at least an EngineEdition=5 unit test over the capability switch).

---

## [alto/M] single-source-of-truth-for-catalog-queries  ·  livedb-readers

**Delete the duplicated query set in LiveDbObjectBodyResolver** — requisito **quality**

`LiveDbObjectBodyResolver` re-implements the column, key-constraint, FK, check, default and index queries a second time (690 lines against the readers' 2211 total) purely because "ConstraintReader is internal" (its own comment at line 333). The collation drift finding above is the first observed consequence; the included-column ORDER BY bug now has to be fixed in two places, and any attribute added to fix the findings above must be added twice or the diff viewer will keep disagreeing with the verdict. The spec also anticipated `Providers.LiveDb/Sql/` embedded .sql resources, which never materialised.

**Sketch**

Give each reader an optional `int? objectId` filter (`WHERE (@objectId IS NULL OR t.object_id = @objectId)`) and have the resolver call `new TableReader().ReadAsync(cn, objectId, ct)` etc. Net deletion of roughly 400 lines and the drift class disappears.

---

## [alto/M] per-operation-cancellation  ·  app-ui-robustness

**Give the long-running operations a real CancellationTokenSource instead of CancellationToken.None** — requisito **resiliente**

Every await in the app passes CancellationToken.None: CompareAsync's token comes from the command but the two callers that matter (App startup, LoadProjectFromPathAsync, EditProjectAsync) pass None, and the network scan, database enumeration, body resolution and script execution all hardcode None. The busy overlay has no Cancel button. A compare against an unreachable server therefore blocks the whole window for the full SqlClient connect timeout × N object-kind queries, and a slow deploy blocks a modal that refuses to close. The only existing CTS (_autoConnectCts in ProjectEndpointPanelViewModel) is cancelled but never disposed and is only used for the debounce delay, not for the connection it guards.

**Sketch**

One CTS field per operation-owning VM, cancelled+disposed at the start of the next run and in a finally; add a Cancel button to the busy overlay bound to a CancelCommand, and to the execute dialog while IsRunning (with copy that says an aborted execution rolls back). Pass the token through SqlServerDiscovery/SqlExecutor/IObjectBodyResolver, which already accept one.

---

## [alto/M] real-environment-tagging  ·  app-ui-robustness

**Make the environment/production marker real and show it on the destructive path** — requisito **sicuro**

The safety affordance is decorative today: ProjectEndpointPanelViewModel.ToEndpoint() hardcodes `EnvironmentTag: "Dev"` and `EnvironmentColorHex: "#0054BD"` for BOTH endpoints regardless of the server, RebuildRows hardcodes `envColor = "#0054BD"` with a "comes in Wave 2C" comment, and the only views that render EnvironmentTag are the unreachable connection dialogs. A production target is visually indistinguishable from a scratch database in the header strip and in the execute confirmation, which is exactly where the distinction matters.

**Sketch**

Add an environment picker (Dev/Test/Prod + colour) to the endpoint panels, persist it in ProjectEndpoint, and render it as a coloured band in ProjectHeaderStrip and as the header colour of ConfirmExecuteDialog. Gate the execute button behind typed confirmation when the target is tagged Prod.

---

## [alto/M] filters-and-cli-object-selection  ·  redgate-parity

**Object filters: saved filter files, object-type toggles, CLI --include/--exclude** — requisito **sicuro**

Redgate has `.scpf` filter files with wildcard/NOT LIKE/compound expressions, per-object-type toggles, and inline `/Exclude:StoredProcedure:usp.*Temp` (docs/00_overview.md §4.3). DbDelta has per-row checkboxes in the GUI, persisted as `Selections` in the .dbd (`ObjectSelectionKey`), and **nothing in the CLI** — `ScriptCommand` calls `Generate(comparison, selection: null, ...)`, so `dbdelta script` always emits every single difference. That is a safety gap as much as a parity one: there is no way to run an automated deploy that excludes the one object you know must not be touched, and no way to exclude a whole kind (e.g. Users/Permissions) from a CI run.

**Sketch**

`--exclude <Kind>:<regex>` / `--include <Kind>:<regex>` (repeatable) building the `selection` enumerable that `ScriptGenerator.Generate` already accepts — the plumbing exists, only the filter predicate is missing. Then a per-kind checkbox row in the GUI, and store the filter expressions in the .dbd next to `Selections` so a project drives both GUI and CLI identically.

---

## [alto/M] extended-properties-kind  ·  redgate-parity

**Extended properties (MS_Description and friends) as a compared kind** — requisito **parity**

`sys.extended_properties` is not read anywhere (`grep -rni extended_properties src/` → nothing; `ProjectOptions.TreatExtendedPropertiesAsObjects` exists as a dead boolean). In practice every documented enterprise schema carries `MS_Description` on tables and columns, and data-dictionary tooling depends on it. Today DbDelta reports two schemas as identical when one is fully documented and the other has no descriptions at all, and it can never deploy documentation. Redgate compares them by default with `IgnoreExtendedProperties` (`ie`) as the opt-out — note that means DbDelta's current behaviour is not even the safe default.

**Sketch**

One reader over `sys.extended_properties` joined to class/major_id/minor_id → (object, column, name, value); attach to the owning object; emit `sp_addextendedproperty` / `sp_updateextendedproperty` / `sp_dropextendedproperty`. Ship it behind an `IgnoreExtendedProperties` flag defaulting ON so the diff does not suddenly light up for existing users.

---

## [alto/M] index-physical-properties  ·  redgate-parity

**Index and table physical properties: fill factor, locks, compression, filegroup, disabled state** — requisito **affidabile**

`IndexReader` reads only name / is_unique / type / filter / key columns / included columns. Missing: `fill_factor`, `is_padded`, `allow_row_locks`, `allow_page_locks`, `ignore_dup_key`, `is_disabled`, `optimize_for_sequential_key`, `data_space_id` (filegroup / partition scheme) and `sys.partitions.data_compression`. That is why `IgnoreFillFactor` and `IgnoreFileGroups` can sit in the enum unused — there is nothing to ignore. Concretely: a disabled index on the target and an enabled index of the same name on the source compare Identical, and a PAGE-compressed table compares Identical to an uncompressed one (a 4x storage difference). These are also the properties whose absence Redgate users most often notice in a first comparison.

**Sketch**

Widen `IndexReader.IndexesQuery` (all the columns are on `sys.indexes` / `sys.partitions` already) and `TableReader` for `data_space_id`; add the fields to `TableIndex`/`Table`; compare them behind the matching Ignore* flags; extend `IndexScriptEmitter` with the `WITH (...)` / `ON [filegroup]` clauses. Do it together with the columnstore fix — same query, one pass.

---

## [alto/M] syntax-highlighting-intra-line-diff  ·  redgate-parity

**SQL syntax highlighting and intra-line (character-level) diff in the diff pane** — requisito **quality**

The side-by-side pane is the surface users judge a compare tool by, and it is currently plain monospace text: `grep -rni 'syntax|highlight|AvaloniaEdit|TextMate' src/DbDelta.App.Avalonia` returns **nothing**. `LineDiffer` produces `LineStatus.Modified` for a paired delete+insert but nothing renders *which characters* changed, so a one-character difference inside a 200-character line still requires the user to read both lines and spot it manually. Redgate highlights keywords and the changed span inside the line. This is the single most visible 'this is not SQL Compare' gap and it does not affect correctness at all — which is exactly why it should not be near the top of the queue, but should be on it.

**Sketch**

For intra-line: run the existing LCS over characters for `Modified` rows and emit spans (reuse `LineDiffer`, no new algorithm). For highlighting: AvaloniaEdit + a TextMate T-SQL grammar is the boring path, but a `Regex`-based keyword/string/comment colouriser over the already-split lines is ~40 lines and adds no dependency — take that one first.

---

## [alto/S] configurable-command-timeout  ·  undo-rollback

**Make CommandTimeout a parameter with 0 = unlimited** — requisito **resiliente**

Directly unblocks large rebuilds and index builds, which are exactly the deploys where an undo matters most. One parameter, one CLI option, one spin box.

**Sketch**

`SqlExecutor.ExecuteAsync(..., int commandTimeoutSeconds = CommandTimeoutSeconds)`; thread from a `--command-timeout` option in `ApplyCommand` and from a numeric field (32 px, per the shell height rule) in `ConfirmExecuteDialog`. Persist the last value in the project XML so a slow target does not need re-entering.

---

## [alto/S] case-permuted-clone-property  ·  diff-engine-correctness

**Add FsCheck properties for case-permuted clones and duplicate identities** — requisito **affidabile**

SchemaArbitraries dedupes by exact (Schema,Name) and never permutes case, so the property suite structurally cannot find the critical pairing bug. Two cheap properties would: (1) Compare(db, casePermuted(db)) must be all Identical on a case-insensitive catalog; (2) Compare must not throw when a generated database contains two objects whose names differ only by case (today ToDictionary would throw ArgumentException once a case-insensitive comparer is introduced).

**Sketch**

Add `Gen<Database> CasePermuted(Database)` to SchemaArbitraries (upper/lower-case schema, object and column names) and two Facts alongside Compare_With_Itself_Is_Identical in ComparisonEngineProperties.cs.

---

## [alto/S] cli-exit-codes-and-project-arg  ·  redgate-parity

**CI-usable CLI: --project, drift exit codes, --assert-identical** — requisito **parity**

`ExitCodes` defines 11 codes but `dbdelta script` returns `SuccessNoDifferences` (0) unconditionally at ScriptCommand.cs:101 — even when it just wrote 400 lines of DDL. Only `compare` distinguishes 0 (identical) from 1 (differences). Redgate's CI contract is richer and load-bearing: 63 identical, 61 warnings encountered, 79 with `/AssertIdentical`, 77 insufficient permissions (docs/00_overview.md §4.8). Separately, there is no `--project` switch, so every automated run must pass full connection strings on the command line — which lands passwords in shell history, CI logs, and the process table, undoing the care taken by DPAPI credential storage and `ConnectionStringRedactor`.

**Sketch**

Return `SuccessDifferencesFound` from `script`/`report` when the comparison is non-empty; add `--assert-identical` (non-zero when they differ) for drift-check pipelines; add `dbdelta <verb> --project <file.dbd>` resolving endpoints and credentials through the existing `XmlProjectStore` + `ICredentialStore`. All three are small and unblock the whole scheduled-drift-detection workflow.

---

## [alto/S] backup-and-pre-post-scripts  ·  redgate-parity

**Backup-before-deploy and pre/post deployment scripts** — requisito **resiliente**

Redgate has `/MakeBackup` with provider/type/folder options (§4.4) and reserved `Pre-Deployment\`/`Post-Deployment\` script directories (§4.7). DbDelta has neither, and 'take a backup first' is the only genuinely complete undo that exists for a schema deploy that changes data (a rollback script cannot resurrect dropped rows). Pre/post scripts are what real teams use to back-fill a new NOT NULL column or migrate data around a column split — without them any change that needs a data step has to leave DbDelta entirely.

**Sketch**

Backup: a checkbox + `--make-backup [--backup-folder <path>]` prepending a `BACKUP DATABASE [db] TO DISK = N'…' WITH COPY_ONLY, INIT` batch and refusing to continue if it fails. This is small, and it is the highest-confidence undo the product can offer today. Pre/post: two optional file paths whose contents are spliced inside the transaction before the first and after the last phase.

---

## [alto/S] ci-gate-whole-solution  ·  tests-cicd-arch

**CI should test the solution, not an enumerated list of projects** — requisito **quality**

The enumerated `dotnet test tests/<name>` list in ci.yml is why 2 of 11 test projects have never run in CI. Any project added by a future contributor is silently excluded, and nothing fails to signal it. This is the root cause behind the ci-skips-two-test-projects finding — fixing the symptom by appending two lines leaves the trap in place.

**Sketch**

`dotnet test DbDelta.sln --no-build -c Release --filter "Category!=Compat&Category!=RequiresDocker"` on windows; the linux job keeps the Docker-requiring projects. Tag classes with `[Trait("Category","RequiresDocker")]` (the Compat trait already exists as precedent). Optionally add a tiny architecture test that asserts every `tests/*/**.csproj` appears in the sln, so the sln itself becomes the manifest.

---

## [medio/L] target-model-snapshot-as-upgrade-path  ·  undo-rollback

**Later: serialize the pre-deploy target model next to down.sql so the down script can be regenerated after drift** — requisito **resiliente**

down.sql is frozen at generation time. If the target drifts before the user decides to undo, replaying it may fail or clobber. Persisting the captured target `Database` model lets a fresh, drift-aware inverse be generated at undo time — and doubles as the .snp/snapshot source the architecture doc already anticipates (docs/01_architecture.md §3.4, docs/BACKLOG.md:85 lists the Snapshot provider as pending). Not the MVP: down.sql covers the 95% case at a fraction of the cost.

**Sketch**

Serialize `DbDelta.Core.ObjectModel.Database` to gzipped JSON in the per-run deploy folder. The object model is records, but the `Constraint` hierarchy (`PrimaryKey`/`UniqueConstraint`/`CheckConstraint`/`DefaultConstraint`/`ForeignKey`) needs `[JsonPolymorphic]` discriminators before System.Text.Json will round-trip it — that is the real cost, and it is the same work the Snapshot provider needs, so do it once for both. Then "Undo" = load the snapshot as side A, read the live target as side B, run the normal comparison, and deploy that.

---

## [medio/L] tier3-object-kinds  ·  redgate-parity

**Tier-3 object kinds: full-text, partition function/scheme, filegroups, Service Broker, CLR, XML schema collections, statistics, and the rest** — requisito **affidabile**

Verified against `KindCatalog.KnownKinds`: 12 kinds covered (Table, View, Procedure, Function, Trigger, Sequence, Synonym, UserDefinedType, TableType, User, Role, Permission). Redgate covers ~35 (docs/00_overview.md §4.1). Beyond what is already itemised above (Schema, extended properties, DDL triggers, RLS, encryption keys, temporal, memory-optimized), the remaining absences are: CLR Assembly, XML Schema Collection, Full Text Catalog / Stoplist / Search Property List, Partition Function, Partition Scheme, Filegroup, Service Broker (Contract, Message Type, Queue, Route, Service, Service Binding), Symmetric/Asymmetric Key, Certificate, Rule, Default (legacy standalone), Event Notification, External Data Source / File Format / Table (PolyBase), user statistics, plan guides, database credentials and audits. The v1 decision to defer these is defensible — most appear in a minority of databases. What is not defensible is the failure mode: an object of an unmodelled kind is not 'not compared', it is invisible, so DbDelta reports 'identical' about a pair of databases that differ.

**Sketch**

Before implementing any of them, add a cheap honesty backstop that covers all of them at once: one query counting user objects by `sys.objects.type` (plus assemblies, xml collections, fulltext catalogs, partition functions/schemes, service broker objects) on both sides, and if either side holds an object of a kind DbDelta does not model, surface it as an explicit 'N oggetti di tipo X non confrontati' banner in the UI/report and a distinct CLI exit code. That converts a silent false negative into a stated limitation for ~50 lines — and gives real usage data on which tier-3 kinds to build first.

---

## [medio/M] shorten-password-lifetime-in-memory  ·  sql-injection-security

**Drop the plaintext password from long-lived view-model state once the connection string has been materialised** — requisito **sicuro**

Passwords live as `string` in fields that survive the whole session: ProjectEndpointPanelViewModel.Password, ConnectionEditViewModel.Password, and - most persistently - AppState.SourceConnectionString / TargetConnectionString, which hold the fully materialised string with the password for as long as the app is open (they are re-read on every Compare, every Genera script, every Esegui). Any crash dump, hibernation file or page-file capture of the process contains them. SecureString is not a real answer on .NET and SqlClient wants a string anyway, but the exposure window can be cut from hours to milliseconds.

**Sketch**

Store only the connection ID / credential key in AppState and materialise the full string inside the method that opens the connection, discarding it on exit. Clear ProjectEndpointPanelViewModel.Password after TryPersistCredentialsAsync succeeds. Keep the DPAPI store as the single home of the secret - the architecture already assumes this (ConnectionEntry's doc comment), the UI layer just holds on longer than it needs to.

---

## [medio/M] owner-table-mappings-never-applied  ·  diff-engine-correctness

**Owner / table mappings are persisted and round-tripped but never applied by the engine — and the app has no UI for them** — requisito **parity**

OwnerMappingEntry and TableMappingEntry are modelled (Core/Abstractions), persisted by XmlProjectStore.cs:95-117 and reloaded into ProjectSetupViewModel, but grep shows Core/Diff never reads them and no .axaml exposes an editor. A .dbd project that declares `sales → vendite` therefore compares raw identities: every mapped object shows up as OnlyInA + OnlyInB, and accepting that produces a script that creates the source-schema objects and (if selected) DROPs the target ones. Today this is only reachable by hand-editing a project file, but it is a silent, destructive no-op.

**Sketch**

Short term: when a loaded project carries non-empty OwnerMappings/TableMappings, block the compare with an explicit "mappings not yet supported" message instead of ignoring them. Proper fix: apply the mapping while building the B-side pairing keys (rewrite the target identity into source space before the union) so both sides are mapped consistently in one place.

---

## [medio/M] remaining-unread-attributes  ·  livedb-readers

**Close the remaining attribute gaps that currently read as "Identical"** — requisito **parity**

Beyond the findings, the readers ignore a set of attributes that each turn a real difference into Identical: FK/CHECK `is_not_trusted` (a WITH NOCHECK constraint on the target compares equal to a validated one on the source, so the operator believes the target's data is as constrained as the source's), index `is_disabled` / `fill_factor` / `data_compression` / `ignore_dup_key` / filegroup, PK/UQ index options, `c.is_sparse`, `c.is_rowguidcol`, `c.is_filestream`, and extended properties (MS_Description). None is individually dramatic, but together they are the long tail of "DbDelta said identical, Redgate said different" reports. XML Schema Collection and Always Encrypted are explicitly v1 non-goals (spec §1.3) and should stay out — but then a column typed `xml(dbo.Sch)` or an encrypted column should be *flagged as unsupported*, not silently equated.

**Sketch**

One PR per family, each: add the sys.* column, add the model member, add it to the *Equal method, emit it, add a golden test. Prioritise `is_not_trusted` (correctness-relevant) and index `is_disabled` (a disabled index that looks enabled is a query-plan trap).

---

## [medio/M] report-parity  ·  redgate-parity

**Report parity: per-object DDL in the HTML report, XML output, and an --identical toggle** — requisito **parity**

`HtmlReportGenerator` produces a self-contained page of collapsible per-kind tables with a status badge per object — and nothing else: no CREATE script per object, no line-level diff, no source/target metadata header beyond the four counts (verified by reading the whole generator; `AppendKindSections` renders rows only). Redgate's Interactive report embeds the creation scripts and the per-object diffs, which is what makes it usable as a DBA review artifact and as the evidence attached to a change ticket. Formats: DbDelta has HTML + JSON (`ReportCommand` exposes only `--html`/`--json`); Redgate has Interactive / Html / XML / Excel with `/ReportType`.

**Sketch**

Embed the already-available per-object bodies (`IObjectBodyResolver` + `TableScriptEmitter.GenerateFullTableBody`) and the `LineDiffer` output into the existing `<details>` sections — the HTML report then becomes the review artifact rather than a summary. Add a source/target + timestamp + options header. XML is a trivial third serializer off the existing DTOs; skip Excel (a CSV is enough for management review and needs no dependency).

---

## [medio/M] ddl-triggers-kind  ·  redgate-parity

**Database-level DDL triggers** — requisito **parity**

`ModuleReader.TriggerQuery` filters `WHERE tr.parent_class = 1` — DML triggers only. `parent_class = 0` (database-scoped DDL triggers) are invisible, so a source database with an auditing DDL trigger and a target without one compare clean. Redgate treats DDL Trigger as a first-class object type (docs/00_overview.md §4.1) and additionally offers `DisableAndReenableDdlTriggers` for deployment — which matters here for a second reason: DbDelta's own deploy script fires the target's DDL triggers with no way to suppress them, so a target with a 'log/deny all schema changes' trigger can reject or noisily audit a DbDelta deploy.

**Sketch**

A second query with `parent_class = 0` joined to `sys.server_sql_modules`-equivalent for the body, a new kind in `KindCatalog`, and an emitter for `CREATE/ALTER TRIGGER … ON DATABASE`. Cheap once the trigger reader and emitter already exist — it is largely a second WHERE clause and a scope flag.

---

## [medio/M] column-and-security-attributes  ·  redgate-parity

**Remaining column and security attributes: sparse, rowguidcol, XML schema binding, Always Encrypted, sensitivity classification, RLS** — requisito **sicuro**

Beyond masking (reported as a finding), `Column` carries no `is_sparse`, `is_rowguidcol`, `is_filestream`, `xml_collection_id`, `encryption_type` (Always Encrypted CEK/CMK), `generated_always_type`, or sensitivity classification, and there is no reader for security policies (RLS) or column master/encryption keys. Each is a case where two columns compare Identical while behaving differently; the two with real security weight are Always Encrypted (a deploy that recreates the column without its encryption metadata silently decrypts it) and RLS (a security policy present on the source and absent on the target means the target leaks rows across tenants).

**Sketch**

All the column flags are already-present columns of `sys.columns` — one widened SELECT plus fields on `Column` plus lines in `ColumnsEqual`. RLS and the encryption keys are new kinds (`sys.security_policies`, `sys.column_encryption_keys`, `sys.column_master_keys`); at minimum detect their presence and refuse to claim 'Identical' on an object they touch.

---

## [medio/M] scale-10k-objects  ·  redgate-parity

**Validate and tune the UI at Redgate's scale (10k+ objects)** — requisito **quality**

Redgate handles 10k+ object schemas routinely. DbDelta's readers are batched full-catalog queries (a handful of round-trips regardless of size — good), but the UI path is untested at scale: `RebuildRows` materialises one `DifferenceRowViewModel` per object into a single `ObservableCollection`, `RowsView` layers three `SortDescriptions` plus a grouping plus a filter over it, and `OnSearchTextChanged` calls `_rowsView.Refresh()` on **every keystroke** — a full re-filter and re-sort of 10k rows per character typed, on the UI thread, with no debounce. There is no measurement anywhere in the repo to say whether that is 5 ms or 500 ms.

**Sketch**

Measure first — generate a 10k-object fixture and time compare→render→type-in-search; do not optimise blind. If search is the bottleneck the fix is a ~150 ms debounce on `OnSearchTextChanged` (a `DispatcherTimer`, ~10 lines) before anything structural. Also confirm the DataGrid is actually virtualising with grouping enabled — grouped Avalonia DataGrids historically are not.

---

## [medio/M] code-duplication-body-resolver  ·  redgate-parity

**LiveDbObjectBodyResolver duplicates the whole reader layer — fix-one-miss-one risk** — requisito **quality**

`src/DbDelta.Providers.LiveDb/ObjectBody/LiveDbObjectBodyResolver.cs` is 690 lines (over the 500-line limit in CLAUDE.md) and carries its own private copies of the constraint, foreign-key and index queries — including the same `AND i.type IN (1, 2)` filter at line 582 that hides columnstore indexes in `IndexReader.cs:35`. Every reader bug therefore has to be fixed twice, and the diff pane can silently disagree with the comparison engine about what an object even looks like. This is precisely the copy-paste failure mode CLAUDE.md rule 3 was written after.

**Sketch**

Give the existing readers an optional `objectId`/identity filter parameter and have the resolver call them, deleting its duplicated SQL. The resolver then shrinks to 'read one object via the shared readers, hand it to the emitter' — and every future catalog fix lands in one place.

---

## [medio/M] unused-scriptdom-dependency  ·  tests-cicd-arch

**Microsoft.SqlServer.TransactSql.ScriptDom is referenced by Core but used nowhere — either use it or delete it** — requisito **quality**

`grep -rn 'ScriptDom|TSqlParser|TSqlScript' src/` returns zero hits, yet the package is a PackageReference on DbDelta.Core and therefore flows into every project and into the ~94 MB self-contained MSI. Meanwhile the two places that most need a real T-SQL parser — `SqlExecutor.SplitOnGo` (regex line matching) and `BodyNormalizer` (hand-rolled whitespace/comment normalisation, the engine's false-positive/false-negative frontier) — hand-roll it. Paying for a parser and then not using it is the worst of both.

**Sketch**

Use it for SplitOnGo (token-level GO detection, comment/string aware) and consider it for BodyNormalizer's canonicalisation; if neither lands, drop the PackageReference and shrink the installer. Either outcome is an improvement over the status quo.

---

## [medio/S] emit-batch-boundaries-inside-composite-bodies  ·  scriptgen-correctness

**Split multi-statement bodies (notably the rebuild) into separate GO batches** — requisito **affidabile**

DeploymentScriptWriter.WriteBatch wraps each emitter body in a single GO batch, but EmitRebuild puts DROP CONSTRAINT + CREATE TABLE + SET IDENTITY_INSERT + INSERT…SELECT + DROP TABLE + sp_rename + ADD CONSTRAINT into one batch. The statements after the DROP+rename reference an object whose identity changed mid-batch; SQL Server usually recompiles, but this is exactly the fragile pattern that produces 'Invalid object name' / 'schema changed' failures on some versions and compatibility levels, and it makes the failure point ambiguous when it does break. Redgate splits these.

**Sketch**

Let emitters return `IReadOnlyList<string>` batches instead of one string (or have WriteBatch split on a sentinel), and put a boundary after the temp-table CREATE and after sp_rename in EmitRebuild.

---

## [medio/S] encrypted-vs-unreadable-definition  ·  diff-engine-correctness

**ModuleReader conflates WITH ENCRYPTION with "definition not readable" and the engine then reports Different forever** — requisito **affidabile**

ModuleReader coerces `Body IS NULL → IsEncrypted = true` for views, procs, functions and triggers (its own remark admits it also captures the permission edge case). ClassifyModule (ComparisonEngine.cs:320-323) then returns Different unconditionally, and the emitters produce only a `-- WARNING: … is encrypted` comment. A low-privilege account without VIEW DEFINITION therefore sees a wall of "Different" rows labelled encrypted that no deploy can ever flatten, with no hint that the real problem is a missing permission.

**Sketch**

Distinguish the two cases: probe `OBJECTPROPERTY(object_id,'IsEncrypted')` (or sys.sql_modules row presence) and carry a `BodyUnavailableReason` on Module. Report the permission case as an explicit warning status rather than Different, and never let a null-vs-null body pair reach the `Normalize(null) == Normalize(null) → Identical` path in ClassifyModule (currently unreachable from LiveDb, but a latent false negative for any future ISchemaSource).

---

## [medio/S] clr-modules-mislabelled-as-encrypted  ·  livedb-readers

**Stop reporting CLR modules as encrypted-and-forever-Different** — requisito **affidabile**

`sys.procedures` includes CLR procedures (type 'PC') and `sys.objects`-based reads exclude CLR functions ('FS','FT') while `DependencyReader.MapKind` already maps 'FS'/'FT' to "Function". A CLR procedure has no `sys.sql_modules` row, so `ModuleReader` coerces `IsEncrypted = true` (ModuleReader.cs:87), `ClassifyModule` then returns Different unconditionally (ComparisonEngine.cs:320), and the emitter produces `-- WARNING: procedure […] is encrypted (WITH ENCRYPTION)` (ProcedureScriptEmitter.cs:30). Two identical CLR procedures therefore show as a Different row that no deploy can ever converge, with a diagnosis that is factually wrong. CLR assemblies are a declared v1 non-goal, so the right answer is honest exclusion, not support.

**Sketch**

Add `AND p.type = 'P'` to ProcQuery (and keep FN/IF/TF in FunctionQuery), then count the excluded CLR modules into the coverage manifest above so they are reported as "not compared (CLR)" instead of "encrypted / different".

---

## [medio/S] delete-dead-ui-layer  ·  app-ui-robustness

**Delete or re-wire the unreachable UI layer (ConnectionPickerView, ConnectionPickerSlot, EnvironmentBadge, ConnectionManagerDialog)** — requisito **quality**

ConnectionPickerView.axaml is referenced by no other view and constructed by no code; EnvironmentBadge is instantiated nowhere; MainWindowViewModel.OpenConnectionManagerCommand is bound in no .axaml, so ConnectionManagerDialog and everything it reaches (ConnectionEditDialog/ConnectionEditViewModel, ConnectionStoreViewModel.Delete/UpsertExplicit) is unreachable from the shipped shell. That is ~700 lines of code carrying its own async void handlers and an `async partial void OnSelectedEntryChanged` in ConnectionPickerSlot (a non-event-handler async void that would crash the process on a credential-store throw) — all reviewed, formatted and maintained for nothing. It also hides a real regression: the only binding for AppState.LastError lives in the dead ConnectionPickerView, which is why errors are now invisible.

**Sketch**

Either delete the four files plus ConnectionPickerSlot and the AppState SourceSlot/TargetSlot properties, or bind OpenConnectionManagerCommand to a toolbar button and fix the async void handlers. Decide per file; do not leave both states half-wired.

---

## [medio/S] configurable-command-timeout  ·  app-ui-robustness

**Surface SqlExecutor's 60 s per-batch command timeout in the UI** — requisito **affidabile**

CommandTimeoutSeconds is a private const 60 in SqlExecutor. A single ALTER TABLE that rebuilds a large table, or an index create on a big fact table, exceeds that routinely; the batch is aborted, the whole transaction rolls back, and the user sees only "Esecuzione fallita: Timeout expired" with no way to raise the limit short of a rebuild. Combined with SET TRANSACTION ISOLATION LEVEL SERIALIZABLE, long deploys also hold locks for the entire run.

**Sketch**

Add an optional timeout parameter to SqlExecutor.ExecuteAsync (default 60) and a numeric field in the execute dialog or project options; mention the value in the confirmation text.

---

## [medio/S] script-generation-options  ·  redgate-parity

**Missing deploy-script options: USE statement, IF EXISTS guards, DDL-trigger suppression, per-module QUOTED_IDENTIFIER** — requisito **sicuro**

`DeploymentScriptWriter.WritePreamble` emits the SET block, XACT_ABORT, SERIALIZABLE and BEGIN TRANSACTION — good parity on the envelope (confirmed by docs/parity/redgate-2026-05-28.md). What it does not emit: `USE [TargetDatabase]` (Redgate's `AddDatabaseUseStatement`, whose whole purpose is preventing a reviewed script from being run against the wrong database in SSMS — a `sicuro` concern, not cosmetic), `IF EXISTS`/`IF NOT EXISTS` guards (`ObjectExistenceChecks`, needed for re-runnable scripts), `DisableAndReenableDdlTriggers`, and `AddNoPopulation`. `NoTransactions` and `DoNotOutputCommentHeader` are the only two script options wired up.

**Sketch**

Add the flags to `ComparisonOptions` only as you implement them in `DeploymentScriptWriter`. `AddDatabaseUseStatement` first and default it ON — a saved script that runs against the wrong database is exactly the accident the deploy flow should make impossible, and it is three lines (the target database name is already in the connection string the generator's caller holds).

---

## [medio/S] no-coverage-signal  ·  tests-cicd-arch

**No coverage collection anywhere in CI** — requisito **quality**

455 tests with no coverage number means nobody can see which of the 13 kinds' emitters, which of the 13 readers, or which UI view-models are exercised. The concrete holes found in this review (6 untested readers, 8 kinds without round-trip, --include-permissions untested) would all have been visible on a coverage report. This is diagnosis tooling, not a gate — do not add a threshold that fails builds.

**Sketch**

`dotnet test … --collect:"XPlat Code Coverage"` (coverlet ships with Microsoft.NET.Test.Sdk) + `actions/upload-artifact` for the cobertura file, and a job-summary line with the line/branch percentage. No minimum threshold initially — just make the number visible on every PR.

---

## [medio/S] golden-tests-missing-seven-kinds  ·  tests-cicd-arch

**Seven object kinds have no golden (snapshot) test** — requisito **affidabile**

Golden tests exist for Table, TableWithConstraints, View, Procedure, Function, Trigger, Index, ForeignKey and two ordering scenarios (31 snapshots). Sequence, Synonym, UserDefinedType, TableType, User, Role and Permission are covered only by `ScriptGeneratorOrphanedKindsTests`' `Should().Contain("CREATE SEQUENCE [dbo].[OrderNo]")`-style substring assertions, which pass even if the rest of the statement is malformed (a wrong MINVALUE clause, a missing NOT NULL, a lost `WITH GRANT OPTION`). A snapshot pins the whole statement.

**Sketch**

Seven `Task`-returning tests in DbDelta.ScriptGen.GoldenTests mirroring the existing files, one per kind, covering OnlyInA/OnlyInB/Different. Verify's first run writes the .verified.txt for review — cheap to add, and it makes the substring assertions redundant.

---

## [basso/M] files-over-500-lines  ·  tests-cicd-arch

**Five src files exceed CLAUDE.md's 500-line limit; two of them are the safety-critical ones** — requisito **quality**

LiveDbObjectBodyResolver 690, ScriptGenerator 685, ProjectEndpointPanelViewModel 677, MainWindowViewModel 667, ComparisonEngine 593 (TableScriptEmitter 499 is at the line). Size alone is not a defect, but ScriptGenerator and ComparisonEngine are the two files where a mistake becomes a wrong deploy or a false negative, and both are single flat classes with per-kind logic inlined — ComparisonEngine has twelve near-identical `ToDictionary` / union-of-keys / classify blocks, which is exactly the copy-paste shape CLAUDE.md's DRY rule bans and the reason a new kind can be added without a round-trip test noticing.

**Sketch**

In ComparisonEngine, extract the repeated pattern once: `static IEnumerable<DifferencePair> PairByIdentity<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T,ObjectIdentity> id, Func<T,T,bool> equal)` — the twelve Compare* methods collapse to twelve one-line calls, and a new kind then cannot forget the OnlyInA/OnlyInB/Identical/Different classification. Split ScriptGenerator's per-kind `DispatchBuild` arms into the emitters they already delegate to. Leave the view-models alone unless they are being edited anyway.

---

## [basso/S] non-windows-credential-store-fails-closed-but-silently  ·  sql-injection-security

**Say out loud that credentials cannot be saved on macOS/Linux** — requisito **quality**

KeychainCredentialStore and SecretServiceCredentialStore return IsAvailable=false and throw NotSupportedException from every method. Every call site correctly checks IsAvailable first (verified across ConnectionStoreViewModel and ProjectEndpointPanelViewModel), so the behaviour is fail-closed, not fail-insecure - good. But it is also fail-silent: on Linux the user ticks "Ricorda credenziali", TryPersistCredentialsAsync returns early, and nothing is stored or reported. The user reasonably concludes the secret was saved somewhere.

**Sketch**

When ICredentialStore.IsAvailable is false, disable the "Ricorda credenziali" checkbox and show the reason in its tooltip. One binding plus one string; no new subsystem.

---

## [basso/S] culture-sensitive-ordering  ·  diff-engine-correctness

**Pair ordering uses culture-sensitive string comparison, unlike the resolver which uses ordinal** — requisito **quality**

ComparisonEngine sorts with `OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName)` (lines 147, 181, 207, 238, 277, 296, 565) — no comparer, so Comparer<string>.Default → culture-sensitive. DependencyResolver.CompareNodes deliberately uses string.CompareOrdinal. The result is that grid/report ordering (and any future order-dependent output) varies with the machine locale, which undermines repeatability and makes golden-style assertions fragile.

**Sketch**

Pass StringComparer.Ordinal (or OrdinalIgnoreCase, consistent with the identifier comparer) to every OrderBy/ThenBy in ComparisonEngine.

---

## [basso/S] schema-permission-label  ·  diff-engine-correctness

**SCHEMA-class permissions are labelled "ON DATABASE" in the grid and reports** — requisito **quality**

PermissionReader only joins sys.objects for OBJECT_OR_COLUMN, so a SCHEMA-class row has ObjectName = null; Permission.Identity (Permission.cs) then renders `"{State} {Action} TO [{Grantee}] ON {ObjectName ?? "DATABASE"}"`. A schema-level grant is therefore displayed to the user as a database-level grant even though the emitter correctly writes `SCHEMA::[name]` from ObjectSchema. Two schema grants of the same action to the same grantee are also visually indistinguishable in the grid.

**Sketch**

Build the display name from ClassDesc: SCHEMA → `SCHEMA::[{ObjectSchema}]`, DATABASE → `DATABASE`, else `[{ObjectSchema}].[{ObjectName}]`. Include ClassDesc in Permission.Identity so two classes can never collide in the app's (Kind,Schema,Name) pairMap (MainWindowViewModel.cs:478 uses ToDictionary, which throws on a duplicate key).

---

## [basso/S] cli-automation-extras  ·  redgate-parity

**Lower-priority automation parity: argument files, MSBuild task, Linux/macOS CLI** — requisito **parity**

Redgate offers `/Argfile:` XML argument files, documented MSBuild/pipeline patterns, and (in v16) beta Linux CLI support. DbDelta is .NET 10 and the CLI has no Windows-only dependency beyond DPAPI credential storage (`CredentialStoreFactory` already selects Keychain/SecretService per platform), so the Linux/macOS CLI is close to free — but none of this matters until `--project`, filters and the exit codes exist, because those are what a pipeline actually calls.

**Sketch**

Skip argument files entirely — a `.dbd` project plus a handful of switches covers the same need without a second config format (YAGNI). Publish linux-x64/osx-arm64 CLI builds in the release workflow once the DB-backed integration tests are green on Linux (they already run there in CI).

---

## [basso/S] prerelease-dependency-in-ga-product  ·  tests-cicd-arch

**System.CommandLine 2.0.0-beta5.25277.114 is a prerelease pinned into a v1.0 GA product** — requisito **quality**

Directory.Packages.props pins `System.CommandLine` to a beta build; the CLI's whole surface (`RootCommand`, `Option<T>`, `SetAction`, `parseResult.GetValue`) is built on APIs that have already changed shape repeatedly across the beta series. Central pinning at least makes the version explicit and `CentralPackageTransitivePinningEnabled` is on, so the exposure is bounded — but a GA product whose CLI cannot be rebuilt from source in two years' time without an API migration is a real maintenance liability. Worth a conscious decision, not a silent one. (Everything else in Directory.Packages.props is a stable release; I found no clearly vulnerable version and did not guess at CVEs.)

**Sketch**

Either accept it explicitly with a comment in Directory.Packages.props recording the API-churn risk, or replace the ~4 verbs' plumbing with hand-rolled `args` parsing (the CLI has 4 commands and 8 options total — under 100 lines, no dependency) and delete both System.CommandLine and Spectre.Console if the latter is equally unused.

---
