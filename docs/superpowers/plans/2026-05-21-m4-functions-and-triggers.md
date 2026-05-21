# DbDelta — M4 Functions & Triggers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the M3 module pipeline with SQL Server **functions** (scalar `FN`, inline table-valued `IF`, multi-statement table-valued `TF`) and **DML triggers** (INSERT / UPDATE / DELETE) — read from live SQL Server (including encrypted modules), paired and body-diffed by `ComparisonEngine`, and emitted as deployable `CREATE OR ALTER` / `DROP IF EXISTS` DDL.

**Architecture:** Two new record types extend the existing `DbDelta.Core.ObjectModel.Module` base: `Function` (with a `FunctionKind` discriminator for the three SQL kinds) and `Trigger` (carries parent-table identity + `IsDisabled` + `IsNotForReplication` flags). `Database` gains `Functions` + `Triggers` collections. `LiveDb` ModuleReader grows two new readers that join `sys.objects` / `sys.triggers` with `sys.sql_modules`. `ComparisonEngine` reuses the generic `CompareModules<TModule>` helper introduced in M3; triggers add a state-only diff path for the enable/disable flag. `ScriptGenerator` orders the new kinds into the pipeline: tables → indexes → views → **functions** → procedures → **triggers** → FKs.

**Tech Stack:** Same as M1/M2/M3 — .NET 10, C# 14, xUnit v3, FluentAssertions, Verify.Xunit, Testcontainers.MsSql 4.x, Microsoft.Data.SqlClient 6.x. No new package versions; M4 is purely Core + Provider + ScriptGen growth.

---

## Reference: Spec Sections This Plan Implements

| Spec section | Plan task(s) |
|--------------|--------------|
| §1.2 Object kinds — Function (scalar / inline-TVF / multi-TVF), Trigger (DML only) | T4.1 – T4.14 |
| §3.1 Compare flow — `CREATE OR ALTER` for modules | T4.9 – T4.11 |
| §3.4 Encrypted module (`WITH ENCRYPTION`) → opaque + warning | T4.6, T4.7, T4.12 |
| §3.4 Trigger enable/disable as a non-DDL diff (ALTER TABLE … ENABLE/DISABLE TRIGGER) | T4.5, T4.10 |
| §5.2 Provider integration tests per kind | T4.12 |
| §5.2 Script-gen golden tests per `(kind, change-type)` | T4.9, T4.10 |
| §6.2 M4 milestone scope | All tasks |

Out of scope for M4 (per roadmap):
- CLR functions (`FS`, `FT`) and CLR triggers — **never** (M4 covers T-SQL only; CLR is a v2 parking-lot item).
- DDL / logon triggers — **never** (spec §1.2 explicitly says "DML only").
- Schemabinding dependency resolution between functions and tables — **M7** (the dependency resolver).
- Trigger ORDER (`sp_settriggerorder`) — **M7** or later.

---

## File Structure Map

```
DbDelta/
├─ src/
│  ├─ DbDelta.Core/
│  │  ├─ ObjectModel/
│  │  │  ├─ Function.cs                             T4.1   (NEW — sealed record : Module + FunctionKind)
│  │  │  ├─ FunctionKind.cs                         T4.1   (NEW — enum Scalar / InlineTableValued / MultiStatementTableValued)
│  │  │  ├─ Trigger.cs                              T4.2   (NEW — sealed record : Module + ParentSchema/ParentName + IsDisabled + IsNotForReplication)
│  │  │  └─ Database.cs                             T4.3   (MODIFY — add Functions + Triggers collections + 7-arg ctor overload)
│  │  ├─ Diff/
│  │  │  └─ ComparisonEngine.cs                     T4.4, T4.5   (MODIFY — pair functions + triggers; ClassifyTrigger overrides ClassifyModule to fold IsDisabled / IsNotForReplication into the diff)
│  │  └─ ScriptGen/
│  │     ├─ FunctionScriptEmitter.cs                T4.9   (NEW)
│  │     ├─ TriggerScriptEmitter.cs                 T4.10  (NEW)
│  │     └─ ScriptGenerator.cs                      T4.11  (MODIFY — orchestrate tables → indexes → views → functions → procs → triggers → FKs)
│  ├─ DbDelta.Providers.LiveDb/
│  │  ├─ Readers/
│  │  │  └─ ModuleReader.cs                         T4.6, T4.7   (MODIFY — add ReadFunctionsAsync + ReadTriggersAsync)
│  │  └─ LiveDbSource.cs                            T4.8   (MODIFY — compose Functions + Triggers into Database)
└─ tests/
   ├─ DbDelta.Core.UnitTests/
   │  ├─ ObjectModel/
   │  │  └─ FunctionTriggerTests.cs                 T4.1, T4.2, T4.3   (NEW — identity + Kind + Database collections)
   │  └─ Diff/
   │     └─ FunctionTriggerDiffTests.cs             T4.4, T4.5   (NEW — engine pairing for both kinds + trigger state-diff)
   ├─ DbDelta.ScriptGen.GoldenTests/
   │  ├─ FunctionGoldenTests.cs                     T4.9   (NEW + verified snapshots)
   │  ├─ TriggerGoldenTests.cs                      T4.10  (NEW + verified snapshots)
   │  └─ ScriptGeneratorOrderingGoldenTests.cs      T4.11  (NEW — assert the new ordering through Generate())
   ├─ DbDelta.Providers.LiveDb.IntegrationTests/
   │  ├─ FunctionReaderTests.cs                     T4.12  (NEW — scalar + inline-TVF + encrypted)
   │  └─ TriggerReaderTests.cs                      T4.12  (NEW — INSERT trigger + disabled trigger)
   └─ DbDelta.Cli.AcceptanceTests/
      └─ CompareCommandTests.cs                     T4.13  (MODIFY — add scalar-fn-added + trigger-body-changed scenarios)
```

Existing files **not touched** in M4:
- `src/DbDelta.Core/ObjectModel/Module.cs` — the abstract base already exposes everything triggers + functions need (`Schema`, `Name`, `Body`, `IsEncrypted`, abstract `Kind`, `Identity`).
- `src/DbDelta.Core/Diff/BodyNormalizer.cs` — reused unchanged.
- `src/DbDelta.Core/Abstractions/*` — `ISchemaSource` is generic over `Database`.
- `src/DbDelta.Core/Options/*` — no new comparison flags in M4 scope.
- `src/DbDelta.Cli/Commands/*` — the compare command writes any new kind through the existing `DifferencePair → DifferenceDto` pipeline automatically.
- `src/DbDelta.App.Avalonia/*` — the results DataGrid groups by `Kind`; the new `Function` / `Trigger` rows render automatically.

---

## Conventions Used in This Plan

- Every step that adds code includes the full source — no "fill in".
- Every test has the actual assertion code.
- Conventional Commits with the established footer `Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>`.
- `dotnet build` after every code change; `TreatWarningsAsErrors=true` analyzers must stay clean.
- `dotnet format` runs at the end (Task T4.14) — interim file-level drift is OK, the global gate is the last step.
- Integration + acceptance tests target the Linux MSSQL container via the existing fixtures (CI runs them on `ubuntu-latest`).

---

## Phase A — Object model

### Task T4.1: Add `Function` record + `FunctionKind` enum + identity tests

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/FunctionKind.cs`
- Create: `src/DbDelta.Core/ObjectModel/Function.cs`
- Create: `tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class FunctionTriggerTests
{
    [Fact]
    public void Scalar_function_identity_uses_Function_kind()
    {
        Function fn = new("dbo", "fnSum", "CREATE FUNCTION dbo.fnSum() RETURNS int AS BEGIN RETURN 1; END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        fn.Identity.SchemaName.Should().Be("dbo");
        fn.Identity.ObjectName.Should().Be("fnSum");
        fn.Identity.Kind.Should().Be("Function");
        fn.FunctionKind.Should().Be(FunctionKind.Scalar);
    }

    [Fact]
    public void Inline_TVF_function_kind_is_inline_table_valued()
    {
        Function fn = new("dbo", "fnList", "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        fn.FunctionKind.Should().Be(FunctionKind.InlineTableValued);
    }

    [Fact]
    public void Multi_statement_TVF_function_kind_set()
    {
        Function fn = new("dbo", "fnRows", "CREATE FUNCTION dbo.fnRows() RETURNS @T TABLE(X int) AS BEGIN INSERT INTO @T VALUES (1); RETURN; END",
            IsEncrypted: false, FunctionKind: FunctionKind.MultiStatementTableValued);
        fn.FunctionKind.Should().Be(FunctionKind.MultiStatementTableValued);
    }

    [Fact]
    public void Function_records_have_value_equality()
    {
        Function a = new("dbo", "fnA", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        Function b = new("dbo", "fnA", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        a.Should().Be(b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (compile error)**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter FunctionTriggerTests`
Expected: compile error — `Function` and `FunctionKind` do not exist.

- [ ] **Step 3: Create `FunctionKind.cs`**

```csharp
// src/DbDelta.Core/ObjectModel/FunctionKind.cs
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// SQL Server function kinds that DbDelta supports in v1. Mirrors the
/// <c>sys.objects.type</c> codes: <c>FN</c> / <c>IF</c> / <c>TF</c>.
/// </summary>
/// <remarks>
/// CLR functions (<c>FS</c>, <c>FT</c>) are intentionally excluded — they
/// fall under the v2 parking-lot per spec §6.3.
/// </remarks>
public enum FunctionKind
{
    /// <summary>Scalar T-SQL function (<c>sys.objects.type = 'FN'</c>).</summary>
    Scalar,
    /// <summary>Inline table-valued function (<c>sys.objects.type = 'IF'</c>).</summary>
    InlineTableValued,
    /// <summary>Multi-statement table-valued function (<c>sys.objects.type = 'TF'</c>).</summary>
    MultiStatementTableValued,
}
```

- [ ] **Step 4: Create `Function.cs`**

```csharp
// src/DbDelta.Core/ObjectModel/Function.cs
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server user-defined function. Body holds the full <c>CREATE FUNCTION …</c>
/// text as stored in <c>sys.sql_modules.definition</c>; <c>null</c> when the
/// function is encrypted (see <see cref="Module.IsEncrypted"/>).
/// </summary>
/// <remarks>
/// <see cref="Kind"/> always returns <c>"Function"</c> — the per-row kind discriminant
/// used by <see cref="ObjectIdentity"/> and the comparison engine. The SQL-level
/// shape (scalar vs inline-TVF vs multi-TVF) is carried by <see cref="FunctionKind"/>
/// so the script emitter can decide whether to use <c>CREATE OR ALTER FUNCTION</c>
/// or fall back to <c>DROP + CREATE</c> when the return-shape changed.
/// </remarks>
public sealed record Function(
    string Schema,
    string Name,
    string? Body,
    bool IsEncrypted,
    FunctionKind FunctionKind)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "Function";
}
```

- [ ] **Step 5: Run tests + build**

Run:
```
dotnet build src/DbDelta.Core -warnaserror
dotnet test tests/DbDelta.Core.UnitTests --filter FunctionTriggerTests
```
Expected: 0 warnings, 4 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/FunctionKind.cs \
        src/DbDelta.Core/ObjectModel/Function.cs \
        tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add Function record + FunctionKind enum

- FunctionKind: Scalar / InlineTableValued / MultiStatementTableValued
  mirrors sys.objects.type codes FN / IF / TF. CLR functions (FS/FT)
  stay out of scope per spec §6.3 parking-lot.
- Function: sealed record : Module, Kind = "Function". Carries the
  SQL-level shape via FunctionKind so the script emitter can later
  switch between CREATE OR ALTER and DROP + CREATE when the return
  shape changes.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.2: Add `Trigger` record (DML only, parent-table aware)

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/Trigger.cs`
- Modify: `tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs` (append trigger tests)

- [ ] **Step 1: Append failing trigger tests**

Append inside the `FunctionTriggerTests` class:

```csharp
    [Fact]
    public void Trigger_identity_uses_Trigger_kind_and_carries_parent_table()
    {
        Trigger trg = new(
            Schema: "dbo",
            Name: "trg_Customer_Audit",
            Body: "CREATE TRIGGER dbo.trg_Customer_Audit ON dbo.Customer AFTER INSERT AS BEGIN INSERT INTO dbo.Audit DEFAULT VALUES; END",
            IsEncrypted: false,
            ParentSchema: "dbo",
            ParentTable: "Customer",
            IsDisabled: false,
            IsNotForReplication: false);
        trg.Identity.SchemaName.Should().Be("dbo");
        trg.Identity.ObjectName.Should().Be("trg_Customer_Audit");
        trg.Identity.Kind.Should().Be("Trigger");
        trg.ParentSchema.Should().Be("dbo");
        trg.ParentTable.Should().Be("Customer");
        trg.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public void Disabled_trigger_carries_the_flag()
    {
        Trigger trg = new("dbo", "trg", "BODY", IsEncrypted: false,
            ParentSchema: "dbo", ParentTable: "T",
            IsDisabled: true, IsNotForReplication: false);
        trg.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public void Trigger_records_have_value_equality()
    {
        Trigger a = new("dbo", "trg", "BODY", false, "dbo", "T", false, false);
        Trigger b = new("dbo", "trg", "BODY", false, "dbo", "T", false, false);
        a.Should().Be(b);
    }
```

- [ ] **Step 2: Run tests to verify compile error**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter FunctionTriggerTests`
Expected: compile error — `Trigger` type missing.

- [ ] **Step 3: Create `Trigger.cs`**

```csharp
// src/DbDelta.Core/ObjectModel/Trigger.cs
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server DML trigger (INSERT / UPDATE / DELETE). DDL triggers, logon
/// triggers, and CLR triggers are out of scope for v1 per spec §1.2.
/// </summary>
/// <param name="Schema">Owning schema of the trigger (matches the parent table's schema in normal usage).</param>
/// <param name="Name">Trigger name.</param>
/// <param name="Body">
/// Full T-SQL definition as returned by <c>sys.sql_modules.definition</c>, or <c>null</c>
/// when the trigger is encrypted (<see cref="Module.IsEncrypted"/>).
/// </param>
/// <param name="IsEncrypted"><c>true</c> when the trigger was created <c>WITH ENCRYPTION</c>.</param>
/// <param name="ParentSchema">Schema of the table the trigger fires on.</param>
/// <param name="ParentTable">Name of the table the trigger fires on.</param>
/// <param name="IsDisabled">
/// <c>true</c> when the trigger is currently disabled via
/// <c>DISABLE TRIGGER … ON …</c>. Toggling this is a non-DDL operation
/// emitted as an <c>ENABLE TRIGGER</c> / <c>DISABLE TRIGGER</c> statement
/// rather than a full <c>CREATE OR ALTER</c>.
/// </param>
/// <param name="IsNotForReplication"><c>true</c> when defined with <c>NOT FOR REPLICATION</c>.</param>
public sealed record Trigger(
    string Schema,
    string Name,
    string? Body,
    bool IsEncrypted,
    string ParentSchema,
    string ParentTable,
    bool IsDisabled,
    bool IsNotForReplication)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "Trigger";
}
```

- [ ] **Step 4: Build + test**

Run:
```
dotnet build src/DbDelta.Core -warnaserror
dotnet test tests/DbDelta.Core.UnitTests --filter FunctionTriggerTests
```
Expected: 0 warnings; 7 tests PASS (4 from T4.1 + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Trigger.cs \
        tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add Trigger record (DML only, parent-table aware)

- Trigger: sealed record : Module, Kind = "Trigger". Carries
  ParentSchema + ParentTable (so the script emitter can ALTER TABLE …
  ENABLE/DISABLE TRIGGER without an extra round-trip), IsDisabled,
  and IsNotForReplication.
- DDL / logon / CLR triggers stay out of scope per spec §1.2.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.3: Extend `Database` with `Functions` + `Triggers` collections

**Files:**
- Modify: `src/DbDelta.Core/ObjectModel/Database.cs`
- Modify: `tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs`

- [ ] **Step 1: Append failing tests**

Inside `FunctionTriggerTests`:

```csharp
    [Fact]
    public void Database_carries_functions_and_triggers_collections()
    {
        Schema dbo = new("dbo");
        Function fn = new("dbo", "fnA", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        Trigger trg = new("dbo", "trgA", "BODY", IsEncrypted: false,
            ParentSchema: "dbo", ParentTable: "T", IsDisabled: false, IsNotForReplication: false);
        Database db = new("Db", Schemas: [dbo], Tables: [], Views: [], Procedures: [],
            Functions: [fn], Triggers: [trg]);
        db.Functions.Should().ContainSingle().Which.Should().Be(fn);
        db.Triggers.Should().ContainSingle().Which.Should().Be(trg);
    }

    [Fact]
    public void Database_defaults_functions_and_triggers_to_empty()
    {
        Schema dbo = new("dbo");
        Database db = new("Db", Schemas: [dbo], Tables: []);
        db.Functions.Should().BeEmpty();
        db.Triggers.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run tests — verify FAIL (compile)**

Run: `dotnet test tests/DbDelta.Core.UnitTests`
Expected: compile error — the 7-arg ctor doesn't exist.

- [ ] **Step 3: Extend `Database.cs`**

Replace the entire file:

```csharp
// src/DbDelta.Core/ObjectModel/Database.cs
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// The root of an in-memory schema graph: a single SQL Server database snapshot.
/// </summary>
public sealed record Database(
    string Name,
    IReadOnlyList<Schema> Schemas,
    IReadOnlyList<Table> Tables)
{
    /// <summary>All views defined in the database (flattened across schemas).</summary>
    public IReadOnlyList<View> Views { get; init; } = [];

    /// <summary>All stored procedures defined in the database (flattened across schemas).</summary>
    public IReadOnlyList<StoredProcedure> Procedures { get; init; } = [];

    /// <summary>All user-defined functions (scalar + inline TVF + multi-TVF).</summary>
    public IReadOnlyList<Function> Functions { get; init; } = [];

    /// <summary>All DML triggers defined in the database.</summary>
    public IReadOnlyList<Trigger> Triggers { get; init; } = [];

    /// <summary>
    /// M3 ctor — tables + views + procedures. Kept so existing call sites still compile.
    /// </summary>
    public Database(
        string Name,
        IReadOnlyList<Schema> Schemas,
        IReadOnlyList<Table> Tables,
        IReadOnlyList<View> Views,
        IReadOnlyList<StoredProcedure> Procedures)
        : this(Name, Schemas, Tables)
    {
        this.Views = Views;
        this.Procedures = Procedures;
    }

    /// <summary>
    /// M4 ctor — tables + views + procedures + functions + triggers.
    /// </summary>
    public Database(
        string Name,
        IReadOnlyList<Schema> Schemas,
        IReadOnlyList<Table> Tables,
        IReadOnlyList<View> Views,
        IReadOnlyList<StoredProcedure> Procedures,
        IReadOnlyList<Function> Functions,
        IReadOnlyList<Trigger> Triggers)
        : this(Name, Schemas, Tables, Views, Procedures)
    {
        this.Functions = Functions;
        this.Triggers = Triggers;
    }
}
```

- [ ] **Step 4: Build + run all UnitTests (no regression)**

Run:
```
dotnet build src/DbDelta.Core -warnaserror
dotnet test tests/DbDelta.Core.UnitTests
```
Expected: 0 warnings; all tests PASS (including every M1/M2/M3 test still using the 3- or 5-arg ctor).

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Database.cs \
        tests/DbDelta.Core.UnitTests/ObjectModel/FunctionTriggerTests.cs
git commit -m "$(cat <<'EOF'
feat(core): extend Database with Functions + Triggers collections

Adds init-only Functions and Triggers collections defaulting to empty,
plus a 7-arg ctor overload accepting all five module collections so
providers can populate the full graph in a single call. The earlier
3-arg (M1) and 5-arg (M3) ctors keep their behaviour — no call sites
break.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase B — Diff

### Task T4.4: Pair functions through the generic `CompareModules<TModule>` helper

**Files:**
- Modify: `src/DbDelta.Core/Diff/ComparisonEngine.cs` (one new line inside `Compare`)
- Create: `tests/DbDelta.Core.UnitTests/Diff/FunctionTriggerDiffTests.cs`

The existing `CompareModules<TModule>` works generically for any `Module` subtype, so adding functions to `Compare` is one extra call.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/DbDelta.Core.UnitTests/Diff/FunctionTriggerDiffTests.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class FunctionTriggerDiffTests
{
    // Module diff tests pass ComparisonOptions.None on purpose — BodyNormalizer
    // is invoked unconditionally inside ClassifyModule (see ModuleDiffTests).
    private static Database DbWithFns(params Function[] fns) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: [], Functions: fns, Triggers: []);

    [Fact]
    public void Function_only_in_A_is_OnlyInA()
    {
        Database a = DbWithFns(new Function("dbo", "fnSum", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar));
        Database b = DbWithFns();
        DifferencePair pair = new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Function");
        pair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Function_with_identical_bodies_is_Identical()
    {
        Database a = DbWithFns(new Function("dbo", "fn", "SELECT 1", false, FunctionKind.Scalar));
        Database b = DbWithFns(new Function("dbo", "fn", "SELECT 1", false, FunctionKind.Scalar));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Function")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Function_with_substantively_different_bodies_is_Different()
    {
        Database a = DbWithFns(new Function("dbo", "fn", "SELECT 1", false, FunctionKind.Scalar));
        Database b = DbWithFns(new Function("dbo", "fn", "SELECT 2", false, FunctionKind.Scalar));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Function")
            .Status.Should().Be(DifferenceStatus.Different);
    }
}
```

- [ ] **Step 2: Run tests — verify the engine ignores functions today**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter FunctionTriggerDiffTests`
Expected: FAIL — `Single(p => p.Identity.Kind == "Function")` throws because no Function pair was emitted.

- [ ] **Step 3: Extend `ComparisonEngine.Compare`**

Open `src/DbDelta.Core/Diff/ComparisonEngine.cs`. Find the `Compare` method that currently calls:

```csharp
pairs.AddRange(CompareModules(a.Procedures, b.Procedures));
```

Add the function pairing immediately after that line:

```csharp
pairs.AddRange(CompareModules(a.Functions, b.Functions));
```

- [ ] **Step 4: Run tests — confirm GREEN**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter FunctionTriggerDiffTests`
Expected: 3 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Diff/ComparisonEngine.cs \
        tests/DbDelta.Core.UnitTests/Diff/FunctionTriggerDiffTests.cs
git commit -m "$(cat <<'EOF'
feat(core/diff): pair functions through the generic CompareModules helper

The M3 CompareModules<TModule> helper already body-normalizes and
classifies any Module subtype. Adding functions is a single
`pairs.AddRange(CompareModules(a.Functions, b.Functions))` call.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.5: Pair triggers + fold `IsDisabled` / `IsNotForReplication` into the diff

Triggers need more than body comparison: the engine should also flag a pair as `Different` when the body matches but the `IsDisabled` (or `IsNotForReplication`) flag has flipped — because the deployer needs to emit `ENABLE TRIGGER` / `DISABLE TRIGGER`.

**Files:**
- Modify: `src/DbDelta.Core/Diff/ComparisonEngine.cs`
- Modify: `tests/DbDelta.Core.UnitTests/Diff/FunctionTriggerDiffTests.cs`

- [ ] **Step 1: Append failing tests**

```csharp
    private static Database DbWithTriggers(params Trigger[] triggers) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: [], Functions: [], Triggers: triggers);

    [Fact]
    public void Trigger_only_in_A_is_OnlyInA()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "BODY", false, "dbo", "T", false, false));
        Database b = DbWithTriggers();
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Trigger_with_identical_body_and_state_is_Identical()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", false, false));
        Database b = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", false, false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Trigger_with_identical_body_but_disabled_only_on_one_side_is_Different()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", IsDisabled: false, IsNotForReplication: false));
        Database b = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", IsDisabled: true,  IsNotForReplication: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Trigger_with_substantively_different_body_is_Different()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", false, false));
        Database b = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 2", false, "dbo", "T", false, false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.Different);
    }
```

- [ ] **Step 2: Verify FAIL — engine doesn't pair triggers yet AND has no IsDisabled check**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter FunctionTriggerDiffTests`
Expected: FAIL — triggers aren't paired.

- [ ] **Step 3: Extend `ComparisonEngine.Compare` to pair triggers + add a `ClassifyModuleExtended` step**

Open `src/DbDelta.Core/Diff/ComparisonEngine.cs`. After the line that pairs functions, add:

```csharp
pairs.AddRange(CompareTriggers(a.Triggers, b.Triggers));
```

Then add the new private helper next to `CompareModules`:

```csharp
    private static IEnumerable<DifferencePair> CompareTriggers(
        IReadOnlyList<Trigger> ax,
        IReadOnlyList<Trigger> bx)
    {
        var aByIdentity = ax.ToDictionary(m => m.Identity);
        var bByIdentity = bx.ToDictionary(m => m.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Trigger? sideA);
            bByIdentity.TryGetValue(id, out Trigger? sideB);
            DifferenceStatus status = ClassifyTrigger(sideA, sideB);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static DifferenceStatus ClassifyTrigger(Trigger? a, Trigger? b)
    {
        // Reuse the module-body classification first.
        DifferenceStatus body = ClassifyModule(a, b);
        if (body != DifferenceStatus.Identical)
        {
            return body;
        }
        // Body is byte-equal AND neither side is encrypted — drop into the
        // trigger-specific state check. Both `a` and `b` are guaranteed
        // non-null at this point because ClassifyModule returned Identical
        // only for the both-present case.
        if (a!.IsDisabled != b!.IsDisabled
            || a.IsNotForReplication != b.IsNotForReplication
            || !string.Equals(a.ParentSchema, b.ParentSchema, StringComparison.Ordinal)
            || !string.Equals(a.ParentTable, b.ParentTable, StringComparison.Ordinal))
        {
            return DifferenceStatus.Different;
        }
        return DifferenceStatus.Identical;
    }
```

- [ ] **Step 4: Run all unit tests to confirm GREEN + no regression**

Run: `dotnet test tests/DbDelta.Core.UnitTests`
Expected: every test PASS including the new trigger ones and every M1/M2/M3 test.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Diff/ComparisonEngine.cs \
        tests/DbDelta.Core.UnitTests/Diff/FunctionTriggerDiffTests.cs
git commit -m "$(cat <<'EOF'
feat(core/diff): pair triggers + fold IsDisabled / NFR / parent into diff

ComparisonEngine.Compare now emits DifferencePairs for triggers. The
trigger path piggy-backs on ClassifyModule for body equality and adds
a trigger-specific check so a flipped IsDisabled flag (or a moved
parent table) registers as Different even when the body bytes match —
the deployer needs to emit ENABLE TRIGGER / DISABLE TRIGGER for that
case.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase C — Provider

### Task T4.6: `ModuleReader.ReadFunctionsAsync` (sys.objects type ∈ {FN, IF, TF})

Functions live in `sys.objects` rather than a dedicated `sys.functions` view. The `type` column carries the FN / IF / TF discriminator.

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs` (add a new query constant + method)

- [ ] **Step 1: Add the SQL constant + method**

Inside `ModuleReader`, alongside the existing `ViewQuery` and `ProcQuery`, add:

```csharp
    private const string FunctionQuery = """
        SELECT s.name AS SchemaName,
               o.name AS Name,
               sm.definition AS Body,
               o.type AS RawType
        FROM sys.objects AS o
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = o.object_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('FN', 'IF', 'TF')
        ORDER BY s.name, o.name;
        """;

    /// <summary>
    /// Reads user-defined functions (scalar, inline-TVF, multi-TVF). Same
    /// NULL-body coercion as the view + procedure readers — when the
    /// definition row is missing (encrypted or permission edge case) the
    /// result has <c>Body = null</c> and <c>IsEncrypted = true</c>.
    /// </summary>
    public async Task<IReadOnlyList<Function>> ReadFunctionsAsync(SqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<Function> functions = [];
        await using SqlCommand cmd = new(FunctionQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            string rawType = r.GetString(3).Trim();
            FunctionKind kind = rawType switch
            {
                "FN" => FunctionKind.Scalar,
                "IF" => FunctionKind.InlineTableValued,
                "TF" => FunctionKind.MultiStatementTableValued,
                _ => FunctionKind.Scalar, // Unreachable per WHERE clause; defensive fallback.
            };
            bool encrypted = body is null;
            functions.Add(new Function(schema, name, body, encrypted, kind));
        }
        return functions;
    }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/DbDelta.Providers.LiveDb -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs
git commit -m "$(cat <<'EOF'
feat(providers/livedb): ModuleReader.ReadFunctionsAsync via sys.objects

Reads scalar (FN), inline TVF (IF), and multi-statement TVF (TF)
functions in a single query joining sys.objects + sys.schemas +
sys.sql_modules. Maps the type column onto FunctionKind. NULL-body
coercion matches the existing view + procedure readers, so encrypted
or permission-blocked functions arrive with IsEncrypted = true.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.7: `ModuleReader.ReadTriggersAsync` (sys.triggers + parent table)

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs`

- [ ] **Step 1: Add the trigger SQL + method**

Append inside `ModuleReader`:

```csharp
    private const string TriggerQuery = """
        SELECT ps.name AS TriggerSchemaName,
               tr.name AS TriggerName,
               sm.definition AS Body,
               ts.name AS ParentSchema,
               t.name  AS ParentTable,
               CAST(tr.is_disabled AS BIT)             AS IsDisabled,
               CAST(tr.is_not_for_replication AS BIT)  AS IsNotForReplication
        FROM sys.triggers AS tr
        INNER JOIN sys.objects AS o ON o.object_id = tr.object_id
        INNER JOIN sys.schemas AS ps ON ps.schema_id = o.schema_id
        INNER JOIN sys.tables  AS t  ON t.object_id  = tr.parent_id
        INNER JOIN sys.schemas AS ts ON ts.schema_id = t.schema_id
        LEFT  JOIN sys.sql_modules AS sm ON sm.object_id = tr.object_id
        WHERE tr.parent_class = 1                       -- DML triggers only (1 = object/table parent)
          AND tr.is_ms_shipped = 0
        ORDER BY ps.name, tr.name;
        """;

    /// <summary>
    /// Reads user DML triggers (INSERT / UPDATE / DELETE) with their parent
    /// table identity and the IsDisabled / IsNotForReplication flags.
    /// Encrypted bodies surface as <c>IsEncrypted = true</c> + <c>Body = null</c>.
    /// </summary>
    public async Task<IReadOnlyList<Trigger>> ReadTriggersAsync(SqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<Trigger> triggers = [];
        await using SqlCommand cmd = new(TriggerQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string triggerSchema = r.GetString(0);
            string triggerName = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            string parentSchema = r.GetString(3);
            string parentTable = r.GetString(4);
            bool isDisabled = !r.IsDBNull(5) && r.GetBoolean(5);
            bool isNfr = !r.IsDBNull(6) && r.GetBoolean(6);
            bool encrypted = body is null;
            triggers.Add(new Trigger(
                Schema: triggerSchema,
                Name: triggerName,
                Body: body,
                IsEncrypted: encrypted,
                ParentSchema: parentSchema,
                ParentTable: parentTable,
                IsDisabled: isDisabled,
                IsNotForReplication: isNfr));
        }
        return triggers;
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DbDelta.Providers.LiveDb -warnaserror`
Expected: 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs
git commit -m "$(cat <<'EOF'
feat(providers/livedb): ModuleReader.ReadTriggersAsync via sys.triggers

Reads DML triggers + parent table identity + IsDisabled +
IsNotForReplication. The query filters parent_class = 1 to exclude
DDL / database / server triggers (those stay out of scope per
spec §1.2). Encryption coercion matches the rest of the reader
family.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.8: Compose functions + triggers into `LiveDbSource.LoadAsync`

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs`

- [ ] **Step 1: Edit `LoadAsync` to read both kinds + pass them to the 7-arg ctor**

Find the existing `LoadAsync` `try` block. After the lines that read views + procedures:

```csharp
ModuleReader moduleReader = new();
IReadOnlyList<View> views = await moduleReader.ReadViewsAsync(connection, cancellationToken);
IReadOnlyList<StoredProcedure> procs = await moduleReader.ReadProceduresAsync(connection, cancellationToken);
```

Add the function + trigger reads:

```csharp
IReadOnlyList<Function> functions = await moduleReader.ReadFunctionsAsync(connection, cancellationToken);
IReadOnlyList<Trigger> triggers = await moduleReader.ReadTriggersAsync(connection, cancellationToken);
```

Then change the final `new Database(...)` call to use the 7-arg ctor:

```csharp
return Result<Database>.Success(new Database(dbName, schemas, tables, views, procs, functions, triggers));
```

- [ ] **Step 2: Build full solution**

Run: `dotnet build -warnaserror`
Expected: 0 warnings, 0 errors across every project (Architecture.Tests included).

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/LiveDbSource.cs
git commit -m "$(cat <<'EOF'
feat(providers/livedb): LoadAsync reads functions + triggers via ModuleReader

LiveDbSource now populates Database.Functions and Database.Triggers
so the engine sees them. Cost: two extra round trips per side,
dominated by network latency not row count.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase D — ScriptGen

### Task T4.9: `FunctionScriptEmitter` + golden tests

**Files:**
- Create: `src/DbDelta.Core/ScriptGen/FunctionScriptEmitter.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/FunctionGoldenTests.cs`
- Generates 4 `*.verified.txt` snapshots co-located with the test class (Verify pattern established in M3).

- [ ] **Step 1: Write failing golden tests (mirror the established Verify base-class pattern)**

```csharp
// tests/DbDelta.ScriptGen.GoldenTests/FunctionGoldenTests.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

public class FunctionGoldenTests
{
    private readonly FunctionScriptEmitter _emitter = new();

    [Fact]
    public Task Add_scalar_function_emits_CREATE_OR_ALTER()
    {
        Function fn = new("dbo", "fnSum",
            "CREATE FUNCTION dbo.fnSum(@a int, @b int) RETURNS int AS BEGIN RETURN @a + @b; END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        DifferencePair pair = new(fn.Identity, DifferenceStatus.OnlyInA, fn, null);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Drop_function_emits_DROP_FUNCTION_IF_EXISTS()
    {
        Function fn = new("dbo", "fnSum",
            "CREATE FUNCTION dbo.fnSum() RETURNS int AS BEGIN RETURN 0; END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        DifferencePair pair = new(fn.Identity, DifferenceStatus.OnlyInB, null, fn);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Different_inline_TVF_emits_CREATE_OR_ALTER_with_new_body()
    {
        Function a = new("dbo", "fnList",
            "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        Function b = new("dbo", "fnList",
            "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 2 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Encrypted_function_emits_warning_comment()
    {
        Function fn = new("dbo", "fnSecret", Body: null, IsEncrypted: true, FunctionKind: FunctionKind.Scalar);
        DifferencePair pair = new(fn.Identity, DifferenceStatus.OnlyInA, fn, null);
        return Verify(_emitter.Emit(pair));
    }
}
```

- [ ] **Step 2: Verify FAIL (emitter type missing)**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter FunctionGoldenTests`
Expected: compile error.

- [ ] **Step 3: Create `FunctionScriptEmitter`**

```csharp
// src/DbDelta.Core/ScriptGen/FunctionScriptEmitter.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for user-defined function differences. Mirrors the procedure
/// emitter but rewrites the leading <c>CREATE FUNCTION</c> token instead.
/// </summary>
public sealed class FunctionScriptEmitter
{
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is Function f => EmitCreateOrAlter(f),
            DifferenceStatus.OnlyInB when pair.SideB is Function f => EmitDrop(f),
            DifferenceStatus.Different when pair.SideA is Function f => EmitCreateOrAlter(f),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(Function f)
    {
        if (f.IsEncrypted || f.Body is null)
        {
            return $"-- WARNING: function [{f.Schema}].[{f.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }
        string body = f.Body.TrimStart();
        const string createFn = "CREATE FUNCTION";
        const string createOrAlterFn = "CREATE OR ALTER FUNCTION";
        if (body.StartsWith(createFn, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterFn, body.AsSpan(createFn.Length));
        }
        return body.EndsWith(';') ? body : body + ";";
    }

    private static string EmitDrop(Function f) =>
        $"DROP FUNCTION IF EXISTS [{f.Schema}].[{f.Name}];";
}
```

- [ ] **Step 4: Build + accept snapshots**

Run:
```
dotnet build src/DbDelta.Core -warnaserror
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter FunctionGoldenTests
```

First run FAILS with Verify mismatches (no `*.verified.txt` files). Inspect each received file under `tests/DbDelta.ScriptGen.GoldenTests/`.

Expected contents:

`FunctionGoldenTests.Add_scalar_function_emits_CREATE_OR_ALTER.verified.txt`:
```
CREATE OR ALTER FUNCTION dbo.fnSum(@a int, @b int) RETURNS int AS BEGIN RETURN @a + @b; END;
```

`FunctionGoldenTests.Drop_function_emits_DROP_FUNCTION_IF_EXISTS.verified.txt`:
```
DROP FUNCTION IF EXISTS [dbo].[fnSum];
```

`FunctionGoldenTests.Different_inline_TVF_emits_CREATE_OR_ALTER_with_new_body.verified.txt`:
```
CREATE OR ALTER FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X);
```

`FunctionGoldenTests.Encrypted_function_emits_warning_comment.verified.txt`:
```
-- WARNING: function [dbo].[fnSecret] is encrypted (WITH ENCRYPTION); body cannot be scripted.
```

Promote each `*.received.txt` → `*.verified.txt` only after confirming the content matches. Re-run the filter — 4 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/FunctionScriptEmitter.cs \
        tests/DbDelta.ScriptGen.GoldenTests/FunctionGoldenTests.cs \
        tests/DbDelta.ScriptGen.GoldenTests/FunctionGoldenTests.*.verified.txt
git commit -m "$(cat <<'EOF'
feat(core/scriptgen): FunctionScriptEmitter (CREATE OR ALTER / DROP IF EXISTS)

Same shape as ViewScriptEmitter / ProcedureScriptEmitter. Recognises
the CREATE FUNCTION prefix and rewrites it to CREATE OR ALTER FUNCTION
so generated scripts stay idempotent regardless of function kind
(scalar / inline TVF / multi TVF). Encrypted functions emit a
-- WARNING comment instead of unscriptable DDL.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.10: `TriggerScriptEmitter` + golden tests

Trigger DDL diff is slightly richer than function/proc because of the enable/disable case: when body bytes match but `IsDisabled` flipped, the emitter must produce `ENABLE TRIGGER` / `DISABLE TRIGGER` rather than re-emitting the body.

The classifier already returns `Different` for the state-only case. The emitter inspects both sides to decide which statement to produce.

**Files:**
- Create: `src/DbDelta.Core/ScriptGen/TriggerScriptEmitter.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/TriggerGoldenTests.cs`

- [ ] **Step 1: Write failing golden tests**

```csharp
// tests/DbDelta.ScriptGen.GoldenTests/TriggerGoldenTests.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

public class TriggerGoldenTests
{
    private readonly TriggerScriptEmitter _emitter = new();

    [Fact]
    public Task Add_trigger_emits_CREATE_OR_ALTER()
    {
        Trigger trg = new("dbo", "trg_Customer_Audit",
            "CREATE TRIGGER dbo.trg_Customer_Audit ON dbo.Customer AFTER INSERT AS BEGIN INSERT INTO dbo.Audit DEFAULT VALUES; END",
            IsEncrypted: false,
            ParentSchema: "dbo", ParentTable: "Customer",
            IsDisabled: false, IsNotForReplication: false);
        DifferencePair pair = new(trg.Identity, DifferenceStatus.OnlyInA, trg, null);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Drop_trigger_emits_DROP_TRIGGER_IF_EXISTS()
    {
        Trigger trg = new("dbo", "trg_Customer_Audit", "BODY", false, "dbo", "Customer", false, false);
        DifferencePair pair = new(trg.Identity, DifferenceStatus.OnlyInB, null, trg);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Different_trigger_with_changed_body_emits_CREATE_OR_ALTER()
    {
        Trigger a = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", false, false);
        Trigger b = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 2;", false, "dbo", "T", false, false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task State_only_diff_disabled_in_source_emits_DISABLE_TRIGGER()
    {
        // Body bytes match; only IsDisabled differs (source disabled, target enabled).
        Trigger a = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: true,  IsNotForReplication: false);
        Trigger b = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: false, IsNotForReplication: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task State_only_diff_enabled_in_source_emits_ENABLE_TRIGGER()
    {
        Trigger a = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: false, IsNotForReplication: false);
        Trigger b = new("dbo", "trg", "CREATE TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;", false, "dbo", "T", IsDisabled: true,  IsNotForReplication: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Encrypted_trigger_emits_warning_comment()
    {
        Trigger trg = new("dbo", "trgSecret", Body: null, IsEncrypted: true,
            ParentSchema: "dbo", ParentTable: "T", IsDisabled: false, IsNotForReplication: false);
        DifferencePair pair = new(trg.Identity, DifferenceStatus.OnlyInA, trg, null);
        return Verify(_emitter.Emit(pair));
    }
}
```

- [ ] **Step 2: Verify FAIL**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter TriggerGoldenTests`
Expected: compile error.

- [ ] **Step 3: Create `TriggerScriptEmitter`**

```csharp
// src/DbDelta.Core/ScriptGen/TriggerScriptEmitter.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for trigger differences. Mirrors the procedure / function
/// emitters for body-bearing diffs and additionally emits
/// <c>ENABLE TRIGGER</c> / <c>DISABLE TRIGGER</c> for state-only diffs
/// (body unchanged but <see cref="Trigger.IsDisabled"/> flipped).
/// </summary>
public sealed class TriggerScriptEmitter
{
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is Trigger t => EmitCreateOrAlter(t),
            DifferenceStatus.OnlyInB when pair.SideB is Trigger t => EmitDrop(t),
            DifferenceStatus.Different when pair.SideA is Trigger a && pair.SideB is Trigger b
                => EmitDifferent(a, b),
            DifferenceStatus.Different when pair.SideA is Trigger t => EmitCreateOrAlter(t),
            DifferenceStatus.Identical => string.Empty,
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(Trigger t)
    {
        if (t.IsEncrypted || t.Body is null)
        {
            return $"-- WARNING: trigger [{t.Schema}].[{t.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }
        string body = t.Body.TrimStart();
        const string create = "CREATE TRIGGER";
        const string createOrAlter = "CREATE OR ALTER TRIGGER";
        if (body.StartsWith(create, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlter, body.AsSpan(create.Length));
        }
        return body.EndsWith(';') ? body : body + ";";
    }

    private static string EmitDrop(Trigger t) =>
        $"DROP TRIGGER IF EXISTS [{t.Schema}].[{t.Name}];";

    private static string EmitDifferent(Trigger sideA, Trigger sideB)
    {
        // State-only diff: bodies match (after normalization) but IsDisabled / NFR /
        // parent moved. Emit the minimum required statement instead of rewriting the body.
        bool bodiesMatch = !sideA.IsEncrypted && !sideB.IsEncrypted
            && string.Equals(
                BodyNormalizer.Normalize(sideA.Body),
                BodyNormalizer.Normalize(sideB.Body),
                StringComparison.Ordinal);

        if (bodiesMatch && sideA.IsDisabled != sideB.IsDisabled)
        {
            string verb = sideA.IsDisabled ? "DISABLE" : "ENABLE";
            return $"{verb} TRIGGER [{sideA.Schema}].[{sideA.Name}] ON [{sideA.ParentSchema}].[{sideA.ParentTable}];";
        }

        // Any other diff (body changed, parent moved, NFR flipped) → rebuild via CREATE OR ALTER.
        return EmitCreateOrAlter(sideA);
    }
}
```

- [ ] **Step 4: Build + accept snapshots**

Run:
```
dotnet build src/DbDelta.Core -warnaserror
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter TriggerGoldenTests
```

Expected snapshot contents:

`TriggerGoldenTests.Add_trigger_emits_CREATE_OR_ALTER.verified.txt`:
```
CREATE OR ALTER TRIGGER dbo.trg_Customer_Audit ON dbo.Customer AFTER INSERT AS BEGIN INSERT INTO dbo.Audit DEFAULT VALUES; END;
```

`TriggerGoldenTests.Drop_trigger_emits_DROP_TRIGGER_IF_EXISTS.verified.txt`:
```
DROP TRIGGER IF EXISTS [dbo].[trg_Customer_Audit];
```

`TriggerGoldenTests.Different_trigger_with_changed_body_emits_CREATE_OR_ALTER.verified.txt`:
```
CREATE OR ALTER TRIGGER dbo.trg ON dbo.T AFTER INSERT AS SELECT 1;
```

`TriggerGoldenTests.State_only_diff_disabled_in_source_emits_DISABLE_TRIGGER.verified.txt`:
```
DISABLE TRIGGER [dbo].[trg] ON [dbo].[T];
```

`TriggerGoldenTests.State_only_diff_enabled_in_source_emits_ENABLE_TRIGGER.verified.txt`:
```
ENABLE TRIGGER [dbo].[trg] ON [dbo].[T];
```

`TriggerGoldenTests.Encrypted_trigger_emits_warning_comment.verified.txt`:
```
-- WARNING: trigger [dbo].[trgSecret] is encrypted (WITH ENCRYPTION); body cannot be scripted.
```

Promote each `*.received.txt` → `*.verified.txt` after confirming content. Re-run — 6 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/TriggerScriptEmitter.cs \
        tests/DbDelta.ScriptGen.GoldenTests/TriggerGoldenTests.cs \
        tests/DbDelta.ScriptGen.GoldenTests/TriggerGoldenTests.*.verified.txt
git commit -m "$(cat <<'EOF'
feat(core/scriptgen): TriggerScriptEmitter with state-only ENABLE/DISABLE

- Mirrors the procedure / function emitters for body-bearing diffs
  (CREATE OR ALTER TRIGGER, DROP TRIGGER IF EXISTS).
- For Different pairs where the body matches but IsDisabled flipped,
  emits a minimal ENABLE TRIGGER / DISABLE TRIGGER statement scoped
  to the parent table — no body re-emission.
- Encrypted triggers fall back to a -- WARNING comment.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.11: Wire functions + triggers into `ScriptGenerator` orchestration

Order so far: tables → indexes → views → procedures → FKs.
After M4: tables → indexes → views → **functions** → procedures → **triggers** → FKs.

Reasoning:
- Functions land after views so view-of-function dependencies hold (rare but possible).
- Procedures stay between functions and triggers because procs frequently call functions and triggers frequently call procs.
- Triggers go after procedures so procs (and tables) referenced by trigger bodies already exist.
- FKs remain last so referenced tables are guaranteed to exist by the time we add them.

**Files:**
- Modify: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/ScriptGeneratorOrderingGoldenTests.cs`

- [ ] **Step 1: Write a failing ordering test**

```csharp
// tests/DbDelta.ScriptGen.GoldenTests/ScriptGeneratorOrderingGoldenTests.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

public class ScriptGeneratorOrderingGoldenTests
{
    [Fact]
    public Task Generate_orders_tables_indexes_views_functions_procs_triggers_FKs()
    {
        Schema dbo = new("dbo");
        Table customer = new("dbo", "Customer",
            Columns: [new Column("Id", "int", IsNullable: false, Ordinal: 1)],
            Constraints: [],
            Indexes: []);
        View v = new("dbo", "vCustomer",
            "CREATE VIEW dbo.vCustomer AS SELECT 1 AS Id;", IsEncrypted: false);
        Function fn = new("dbo", "fnList",
            "CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)",
            IsEncrypted: false, FunctionKind: FunctionKind.InlineTableValued);
        StoredProcedure p = new("dbo", "uspGet",
            "CREATE PROCEDURE dbo.uspGet AS SELECT 1;", IsEncrypted: false);
        Trigger trg = new("dbo", "trgCustomerAudit",
            "CREATE TRIGGER dbo.trgCustomerAudit ON dbo.Customer AFTER INSERT AS SELECT 1;",
            IsEncrypted: false, ParentSchema: "dbo", ParentTable: "Customer",
            IsDisabled: false, IsNotForReplication: false);

        Database source = new("Db", Schemas: [dbo], Tables: [customer],
            Views: [v], Procedures: [p], Functions: [fn], Triggers: [trg]);
        Database target = new("Db", Schemas: [dbo], Tables: [], Views: [], Procedures: [], Functions: [], Triggers: []);

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(result);
        return Verify(script);
    }
}
```

- [ ] **Step 2: Verify FAIL — generator does not yet emit functions or triggers**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter ScriptGeneratorOrderingGoldenTests`
Expected: FAIL (Verify mismatch — output is missing the function / trigger blocks).

- [ ] **Step 3: Replace `ScriptGenerator.cs`**

```csharp
// src/DbDelta.Core/ScriptGen/ScriptGenerator.cs
using System.Text;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Orchestrates per-object emitters and wraps the output in a deployment-ready
/// batch. Order: tables (with PK/UQ/CK/DF/identity/computed inline) → standalone
/// CREATE INDEX → views (CREATE OR ALTER) → functions (CREATE OR ALTER) →
/// procedures (CREATE OR ALTER) → triggers (CREATE OR ALTER) → ALTER TABLE
/// ADD CONSTRAINT … FOREIGN KEY.
/// </summary>
public sealed class ScriptGenerator
{
    private readonly TableScriptEmitter _tableEmitter = new();
    private readonly IndexScriptEmitter _indexEmitter = new();
    private readonly ForeignKeyScriptEmitter _fkEmitter = new();
    private readonly ViewScriptEmitter _viewEmitter = new();
    private readonly ProcedureScriptEmitter _procEmitter = new();
    private readonly FunctionScriptEmitter _functionEmitter = new();
    private readonly TriggerScriptEmitter _triggerEmitter = new();

    public string Generate(ComparisonResult result, IEnumerable<DifferencePair>? selection = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<DifferencePair> pairs = [.. (selection ?? result.Differences)
            .Where(p => p.Status != DifferenceStatus.Identical)];

        StringBuilder sb = new();
        sb.AppendLine("-- Generated by DbDelta");
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRANSACTION;");
        sb.AppendLine("GO");

        // 1. Tables
        foreach (DifferencePair pair in pairs.Where(p => p.Identity.Kind == "Table"))
        {
            string ddl = _tableEmitter.Emit(pair);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                sb.AppendLine(ddl);
                sb.AppendLine("GO");
            }
        }

        // 2. Indexes (for newly created tables)
        foreach (DifferencePair pair in pairs.Where(p => p.Identity.Kind == "Table"))
        {
            if (pair.Status != DifferenceStatus.OnlyInA || pair.SideA is not Table t || t.Indexes.Count == 0)
            {
                continue;
            }
            foreach (TableIndex ix in t.Indexes)
            {
                sb.AppendLine(_indexEmitter.EmitCreate(t.Schema, t.Name, ix));
            }
            sb.AppendLine("GO");
        }

        // 3. Views
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "View")
            .OrderBy(p => p.Identity.SchemaName)
            .ThenBy(p => p.Identity.ObjectName))
        {
            string ddl = _viewEmitter.Emit(pair);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                sb.AppendLine(ddl);
                sb.AppendLine("GO");
            }
        }

        // 4. Functions
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "Function")
            .OrderBy(p => p.Identity.SchemaName)
            .ThenBy(p => p.Identity.ObjectName))
        {
            string ddl = _functionEmitter.Emit(pair);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                sb.AppendLine(ddl);
                sb.AppendLine("GO");
            }
        }

        // 5. Procedures
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "Procedure")
            .OrderBy(p => p.Identity.SchemaName)
            .ThenBy(p => p.Identity.ObjectName))
        {
            string ddl = _procEmitter.Emit(pair);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                sb.AppendLine(ddl);
                sb.AppendLine("GO");
            }
        }

        // 6. Triggers — emitted after their parent tables are guaranteed to exist.
        foreach (DifferencePair pair in pairs
            .Where(p => p.Identity.Kind == "Trigger")
            .OrderBy(p => p.Identity.SchemaName)
            .ThenBy(p => p.Identity.ObjectName))
        {
            string ddl = _triggerEmitter.Emit(pair);
            if (!string.IsNullOrWhiteSpace(ddl))
            {
                sb.AppendLine(ddl);
                sb.AppendLine("GO");
            }
        }

        // 7. Foreign keys — last so referenced tables already exist.
        foreach (DifferencePair pair in pairs.Where(p => p.Identity.Kind == "Table"))
        {
            if (pair.Status != DifferenceStatus.OnlyInA || pair.SideA is not Table t)
            {
                continue;
            }
            List<ForeignKey> fks = [.. t.Constraints.OfType<ForeignKey>()];
            if (fks.Count == 0)
            {
                continue;
            }
            foreach (ForeignKey fk in fks)
            {
                sb.AppendLine(_fkEmitter.EmitAdd(t.Schema, t.Name, fk));
            }
            sb.AppendLine("GO");
        }

        sb.AppendLine("COMMIT TRANSACTION;");
        sb.AppendLine("GO");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Accept ordering snapshot**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter ScriptGeneratorOrderingGoldenTests`
Inspect the `*.received.txt`. Confirm the block order is:

```
-- Generated by DbDelta
SET XACT_ABORT ON;
BEGIN TRANSACTION;
GO
CREATE TABLE [dbo].[Customer] (...);
GO
CREATE OR ALTER VIEW dbo.vCustomer AS SELECT 1 AS Id;
GO
CREATE OR ALTER FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X);
GO
CREATE OR ALTER PROCEDURE dbo.uspGet AS SELECT 1;
GO
CREATE OR ALTER TRIGGER dbo.trgCustomerAudit ON dbo.Customer AFTER INSERT AS SELECT 1;
GO
COMMIT TRANSACTION;
GO
```

(Exact whitespace and the precise table-DDL line are governed by the existing `TableScriptEmitter`; what matters is the block order.)

Promote `*.received.txt` → `*.verified.txt`. Re-run all golden tests:
```
dotnet test tests/DbDelta.ScriptGen.GoldenTests
```
Expected: every golden test still passes.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/ScriptGenerator.cs \
        tests/DbDelta.ScriptGen.GoldenTests/ScriptGeneratorOrderingGoldenTests.cs \
        tests/DbDelta.ScriptGen.GoldenTests/ScriptGeneratorOrderingGoldenTests.*.verified.txt
git commit -m "$(cat <<'EOF'
feat(core/scriptgen): ScriptGenerator orders functions + triggers in pipeline

New order: tables → indexes → views → functions → procedures → triggers → FKs.

Functions slot after views so view-of-function dependencies hold;
procedures stay between functions and triggers because procs frequently
call functions and triggers frequently call procs; triggers go after
procedures so procs referenced by trigger bodies already exist; FKs
remain last so referenced tables exist by the time they're added.

Locked in by a Verify snapshot exercising every kind through Generate().

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase E — End-to-end (provider + CLI)

### Task T4.12: Provider integration tests — functions + triggers

**Files:**
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/FunctionReaderTests.cs`
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/TriggerReaderTests.cs`

These run on the Linux CI job against the Testcontainers MSSQL instance.

- [ ] **Step 1: Write the function integration tests**

```csharp
// tests/DbDelta.Providers.LiveDb.IntegrationTests/FunctionReaderTests.cs
using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using DbDelta.Providers.LiveDb;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class FunctionReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_scalar_function()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaFnScalar";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.fnSum') IS NULL
                EXEC('CREATE FUNCTION dbo.fnSum(@a int, @b int) RETURNS int AS BEGIN RETURN @a + @b; END');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue();

        Function fn = result.Value.Functions.Should().ContainSingle(f => f.Name == "fnSum").Subject;
        fn.Schema.Should().Be("dbo");
        fn.FunctionKind.Should().Be(FunctionKind.Scalar);
        fn.IsEncrypted.Should().BeFalse();
        fn.Body.Should().Contain("RETURN @a + @b");
    }

    [Fact]
    public async Task LiveDbSource_loads_inline_TVF_function()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaFnInline";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.fnList') IS NULL
                EXEC('CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        Function fn = result.Value.Functions.Should().ContainSingle(f => f.Name == "fnList").Subject;
        fn.FunctionKind.Should().Be(FunctionKind.InlineTableValued);
    }

    [Fact]
    public async Task LiveDbSource_surfaces_encrypted_function_with_null_body()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaFnEncrypted";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.fnSecret') IS NULL
                EXEC('CREATE FUNCTION dbo.fnSecret() RETURNS int WITH ENCRYPTION AS BEGIN RETURN 1; END');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        Function fn = result.Value.Functions.Should().ContainSingle(f => f.Name == "fnSecret").Subject;
        fn.IsEncrypted.Should().BeTrue();
        fn.Body.Should().BeNull();
    }

    private async Task<string> CreateAndSeedAsync(string dbName, string seedSql, CancellationToken ct)
    {
        await using SqlConnection master = new(fixture.ConnectionString);
        await master.OpenAsync(ct);
        await using (SqlCommand cmd = new($"IF DB_ID('{dbName}') IS NULL CREATE DATABASE [{dbName}];", master))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string conn = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = dbName }.ConnectionString;
        await using SqlConnection target = new(conn);
        await target.OpenAsync(ct);
        await using SqlCommand seed = new(seedSql, target);
        await seed.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
```

- [ ] **Step 2: Write the trigger integration tests**

```csharp
// tests/DbDelta.Providers.LiveDb.IntegrationTests/TriggerReaderTests.cs
using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using DbDelta.Providers.LiveDb;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class TriggerReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_DML_trigger_with_parent_table()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaTriggerInsert";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer(Id int NOT NULL);
            IF OBJECT_ID('dbo.trgCustomerAudit') IS NULL
                EXEC('CREATE TRIGGER dbo.trgCustomerAudit ON dbo.Customer AFTER INSERT AS BEGIN SET NOCOUNT ON; END');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue();

        Trigger trg = result.Value.Triggers.Should().ContainSingle(t => t.Name == "trgCustomerAudit").Subject;
        trg.ParentSchema.Should().Be("dbo");
        trg.ParentTable.Should().Be("Customer");
        trg.IsDisabled.Should().BeFalse();
        trg.IsEncrypted.Should().BeFalse();
        trg.Body.Should().Contain("AFTER INSERT");
    }

    [Fact]
    public async Task LiveDbSource_surfaces_disabled_trigger()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaTriggerDisabled";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer(Id int NOT NULL);
            IF OBJECT_ID('dbo.trgCustomerAudit') IS NULL
                EXEC('CREATE TRIGGER dbo.trgCustomerAudit ON dbo.Customer AFTER UPDATE AS BEGIN SET NOCOUNT ON; END');
            DISABLE TRIGGER dbo.trgCustomerAudit ON dbo.Customer;
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        Trigger trg = result.Value.Triggers.Should().ContainSingle(t => t.Name == "trgCustomerAudit").Subject;
        trg.IsDisabled.Should().BeTrue();
    }

    private async Task<string> CreateAndSeedAsync(string dbName, string seedSql, CancellationToken ct)
    {
        await using SqlConnection master = new(fixture.ConnectionString);
        await master.OpenAsync(ct);
        await using (SqlCommand cmd = new($"IF DB_ID('{dbName}') IS NULL CREATE DATABASE [{dbName}];", master))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        string conn = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = dbName }.ConnectionString;
        await using SqlConnection target = new(conn);
        await target.OpenAsync(ct);
        await using SqlCommand seed = new(seedSql, target);
        await seed.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
```

- [ ] **Step 3: Build the integration test project**

Run: `dotnet build tests/DbDelta.Providers.LiveDb.IntegrationTests -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add tests/DbDelta.Providers.LiveDb.IntegrationTests/FunctionReaderTests.cs \
        tests/DbDelta.Providers.LiveDb.IntegrationTests/TriggerReaderTests.cs
git commit -m "$(cat <<'EOF'
test(providers/livedb): integration tests for functions + triggers

- FunctionReaderTests: scalar (FN), inline TVF (IF), encrypted scalar.
- TriggerReaderTests: AFTER-INSERT with parent table, disabled trigger.

Runs on the linux-integration-tests CI job against the real MSSQL
container. Local runs without Docker (Linux containers) will be
skipped — same behaviour as the M3 integration tests.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T4.13: CLI acceptance — function added + trigger body changed yield exit code 1

**Files:**
- Modify: `tests/DbDelta.Cli.AcceptanceTests/CompareCommandTests.cs`

- [ ] **Step 1: Append two acceptance tests + their seeds**

Inside the `CompareCommandTests` class, append:

```csharp
    [Fact]
    public async Task Returns_exit_code_1_when_source_has_an_extra_function()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaFnSrc";
        const string tgtDb = "DbDeltaFnTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateScalarFunction(srcDb, ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    [Fact]
    public async Task Returns_exit_code_1_when_a_trigger_body_differs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaTrgSrc";
        const string tgtDb = "DbDeltaTrgTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateTriggerWithBody(srcDb, "SET NOCOUNT ON;", ct);
        await CreateTriggerWithBody(tgtDb, "DECLARE @x int = 1;", ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    private async Task CreateScalarFunction(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            "IF OBJECT_ID('dbo.fnSum') IS NULL EXEC('CREATE FUNCTION dbo.fnSum() RETURNS int AS BEGIN RETURN 1; END');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateTriggerWithBody(string db, string innerSql, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand setup = new(
            "IF OBJECT_ID('dbo.Customer') IS NULL CREATE TABLE dbo.Customer(Id int NOT NULL);", c);
        await setup.ExecuteNonQueryAsync(ct);
        await using SqlCommand cmd = new(
            $"IF OBJECT_ID('dbo.trgCustomer') IS NULL EXEC('CREATE TRIGGER dbo.trgCustomer ON dbo.Customer AFTER INSERT AS BEGIN {innerSql} END');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
```

- [ ] **Step 2: Build the acceptance project**

Run: `dotnet build tests/DbDelta.Cli.AcceptanceTests -warnaserror`
Expected: 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Cli.AcceptanceTests/CompareCommandTests.cs
git commit -m "$(cat <<'EOF'
test(cli): acceptance scenarios for function-added + trigger-body-changed

End-to-end CLI scenarios that drive a live MSSQL container through
the compare command and assert exit-code 1 — the diff path now
includes function + trigger pairing all the way from sys.objects /
sys.triggers to the exit code.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase F — Verification & wrap-up

### Task T4.14: Full local matrix + `dotnet format` + push + CI green

- [ ] **Step 1: Build whole solution Release**

Run: `dotnet build --configuration Release -warnaserror`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Run every non-DB test project**

Run each in turn so failures are obvious:
```
dotnet test tests/DbDelta.Core.UnitTests --no-build --configuration Release
dotnet test tests/DbDelta.Architecture.Tests --no-build --configuration Release
dotnet test tests/DbDelta.ScriptGen.GoldenTests --no-build --configuration Release
```
Expected: every test green.

- [ ] **Step 3: `dotnet format`**

Run: `dotnet format`
Expected: at most line-ending / using-sort tweaks.
Then: `dotnet format --verify-no-changes` must report clean.

- [ ] **Step 4: Commit format changes if any**

If `git status --short` is non-empty:
```bash
git add -A
git commit -m "$(cat <<'EOF'
chore: dotnet format (M4 wrap-up)

Whitespace + using-sort normalization. No behavioural change.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 5: Push + watch CI**

```bash
git push origin main
gh run list --limit 1
gh run watch <run-id> --exit-status
```
Expected: both `windows-build` and `linux-integration-tests` jobs go green.

---

## Self-Review Checklist

1. **Spec coverage**
   - §1.2 Function + Trigger object kinds → T4.1 / T4.2 (model), T4.6 / T4.7 (read), T4.9 / T4.10 (emit) ✓
   - §3.1 `CREATE OR ALTER` for modules → T4.9, T4.10, T4.11 ✓
   - §3.4 Encrypted module → opaque + warning → T4.6, T4.7, T4.9, T4.10, T4.12 ✓
   - §3.4 Trigger enable/disable → T4.5 (engine flags state diff), T4.10 (emitter produces ENABLE/DISABLE TRIGGER) ✓
   - §5.2 Provider integration tests per kind → T4.12 ✓
   - §5.2 Script-gen golden tests per kind/change-type → T4.9 (4 snapshots), T4.10 (6 snapshots), T4.11 (1 ordering snapshot) ✓
   - §6.2 M4 milestone scope → All tasks ✓

2. **Placeholder scan** — no "TBD" / "implement later" / "similar to Task N" / "fill in".

3. **Type consistency**
   - `FunctionKind` values: `Scalar`, `InlineTableValued`, `MultiStatementTableValued` (used in T4.1 model + T4.6 reader + T4.12 integration tests).
   - `Function.Kind` discriminator string: `"Function"` (T4.1, T4.11 filters).
   - `Trigger.Kind` discriminator string: `"Trigger"` (T4.2, T4.5 engine, T4.11 filters).
   - `Trigger.ParentSchema` + `Trigger.ParentTable` (string) — consistent across T4.2, T4.5 engine, T4.7 reader, T4.10 emitter.
   - `Trigger.IsDisabled` (bool) — T4.2 model, T4.5 engine state-diff, T4.7 reader, T4.10 emitter for ENABLE/DISABLE.
   - `Database` ctors: 3-arg (M1), 5-arg (M3), 7-arg (M4) — all coexist (T4.3).

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-21-m4-functions-and-triggers.md`. Two execution options:

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
