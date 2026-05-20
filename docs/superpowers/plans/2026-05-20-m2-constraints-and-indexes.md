# DbDelta — M2 Constraints & Indexes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the walking skeleton built in M1 with first-class support for **table constraints** (primary key, foreign key, unique, check, default), **indexes** (clustered, non-clustered, unique, filtered, included columns), **identity properties** (seed + increment), and **computed columns** — read from live SQL Server, surfaced in the diff, and emitted as deployable DDL.

**Architecture:** All new types extend the pure `DbDelta.Core.ObjectModel` graph: `Constraint` (abstract record) + concrete `PrimaryKey` / `ForeignKey` / `UniqueConstraint` / `CheckConstraint` / `DefaultConstraint`; `Index` + `IndexColumn`; `Column` gains `ComputedExpression` / `IdentitySeed` / `IdentityIncrement`; `Table` gains `Indexes` and `Constraints` collections. The `LiveDb` provider grows new readers (`ConstraintReader`, `IndexReader`) that join `sys.*` views in batched queries. `ComparisonEngine` compares the new shapes within a paired table. `ScriptGenerator` emits a deterministic, dependency-aware batch: `CREATE TABLE` (with PK/UQ/CK/DEFAULT/computed/identity inline) → `CREATE INDEX` (per index) → `ALTER TABLE ADD CONSTRAINT` (per FK) — so a generated script can be executed top-to-bottom without a full dependency resolver (that lands in M7).

**Tech Stack:** Same as M1 — .NET 10, C# 14, xUnit v3, FluentAssertions, Verify.Xunit, Testcontainers.MsSql 4.x, Microsoft.Data.SqlClient 6.x. No new package versions; M2 is purely Core + Provider + ScriptGen growth.

---

## Reference: Spec Sections This Plan Implements

| Spec section | Plan task(s) |
|--------------|--------------|
| §1.2 Object kinds — Table (PK/FK/UQ/CK/DEFAULT, Identity, computed cols, indexes) | T2.1 – T2.18 |
| §3.4 Identity column change → table rebuild | (Detected as `Different`; rebuild emission deferred to M8) |
| §3.4 Computed column expression change → drop + recreate | T2.4, T2.18 |
| §3.4 FK cycle across schemas | (FKs emitted after all tables exist via ScriptGenerator ordering; cycle break is M7) |
| §5.2 Provider integration tests per kind | T2.19 – T2.22 |
| §5.2 Script-gen golden tests per `(kind, change-type)` | T2.13 – T2.18 |
| §6.2 M2 milestone scope | All tasks |

---

## File Structure Map

```
DbDelta/
├─ src/
│  ├─ DbDelta.Core/
│  │  ├─ ObjectModel/
│  │  │  ├─ Column.cs                                 T2.1   (MODIFY — add ComputedExpression, IdentitySeed, IdentityIncrement)
│  │  │  ├─ Constraint.cs                             T2.2   (NEW — abstract base record)
│  │  │  ├─ PrimaryKey.cs                             T2.2   (NEW)
│  │  │  ├─ UniqueConstraint.cs                       T2.2   (NEW)
│  │  │  ├─ ForeignKey.cs                             T2.3   (NEW)
│  │  │  ├─ CheckConstraint.cs                        T2.4   (NEW)
│  │  │  ├─ DefaultConstraint.cs                      T2.4   (NEW)
│  │  │  ├─ Index.cs                                  T2.5   (NEW)
│  │  │  ├─ IndexColumn.cs                            T2.5   (NEW)
│  │  │  └─ Table.cs                                  T2.6   (MODIFY — add Constraints + Indexes collections)
│  │  ├─ Diff/
│  │  │  └─ ComparisonEngine.cs                       T2.7   (MODIFY — compare constraints + indexes)
│  │  └─ ScriptGen/
│  │     ├─ TableScriptEmitter.cs                     T2.13–T2.16  (MODIFY — emit PK/UQ/CK/DF/computed/identity inline)
│  │     ├─ IndexScriptEmitter.cs                     T2.17  (NEW)
│  │     ├─ ForeignKeyScriptEmitter.cs                T2.18  (NEW)
│  │     └─ ScriptGenerator.cs                        T2.18  (MODIFY — orchestrate tables → indexes → FKs)
│  ├─ DbDelta.Providers.LiveDb/
│  │  ├─ Readers/
│  │  │  ├─ TableReader.cs                            T2.8   (MODIFY — enrich Column with seed/increment + computed)
│  │  │  ├─ ConstraintReader.cs                       T2.9–T2.11   (NEW)
│  │  │  └─ IndexReader.cs                            T2.12  (NEW)
│  │  └─ LiveDbSource.cs                              T2.12  (MODIFY — compose constraints + indexes into tables)
│  └─ DbDelta.Shared/
│     └─ Dtos/                                        (unchanged — DTOs already flat-string; new kinds reuse DifferenceDto)
└─ tests/
   ├─ DbDelta.Core.UnitTests/
   │  ├─ ObjectModel/
   │  │  ├─ ColumnExtensionsTests.cs                  T2.1   (NEW)
   │  │  ├─ ConstraintTests.cs                        T2.2–T2.4   (NEW)
   │  │  └─ IndexTests.cs                             T2.5   (NEW)
   │  └─ Diff/
   │     ├─ ConstraintDiffTests.cs                    T2.7   (NEW)
   │     └─ IndexDiffTests.cs                         T2.7   (NEW)
   ├─ DbDelta.ScriptGen.GoldenTests/
   │  ├─ TableWithConstraintsGoldenTests.cs           T2.13–T2.16   (NEW)
   │  ├─ IndexGoldenTests.cs                          T2.17  (NEW)
   │  ├─ ForeignKeyGoldenTests.cs                     T2.18  (NEW)
   │  └─ snapshots/                                   (Verify-managed)
   ├─ DbDelta.Providers.LiveDb.IntegrationTests/
   │  ├─ TableReaderTests.cs                          T2.19  (MODIFY — assert seed/increment + computed)
   │  ├─ ConstraintReaderTests.cs                     T2.20–T2.21  (NEW)
   │  └─ IndexReaderTests.cs                          T2.22  (NEW)
   └─ DbDelta.Cli.AcceptanceTests/
      └─ CompareCommandTests.cs                       T2.23  (MODIFY — add PK-only diff scenario)
```

Existing files we DO NOT touch in M2: `src/DbDelta.Core/Abstractions/*`, `src/DbDelta.Cli/Commands/*` (the CLI surface stays the same — new kinds flow through the existing `DifferencePair` → `DifferenceDto` pipeline automatically), `src/DbDelta.App/Components/*` (the existing `ResultsTree` already renders any `DifferenceDto`).

---

## Conventions Used in This Plan

- Every step that adds code includes the full source — no "fill in".
- Every test has the actual assertion code.
- Every commit message follows Conventional Commits; the M1 plan established the footer `Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>` and we keep it.
- `dotnet build` after every code change; if `TreatWarningsAsErrors` flags an analyzer rule the engineer fixes it in the same step before committing.
- `dotnet format` runs at the end (Task T2.24) — interim file-level formatting drift is fine, the global verification gate is the last step.

---

## Phase A — Column extensions

### Task T2.1: Extend `Column` with computed expression + identity seed/increment

**Files:**
- Modify: `src/DbDelta.Core/ObjectModel/Column.cs`
- Test: `tests/DbDelta.Core.UnitTests/ObjectModel/ColumnExtensionsTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/DbDelta.Core.UnitTests/ObjectModel/ColumnExtensionsTests.cs`:

```csharp
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class ColumnExtensionsTests
{
    [Fact]
    public void Column_carries_identity_seed_and_increment_when_identity()
    {
        Column col = new(
            Name: "Id",
            DataType: "int",
            IsNullable: false,
            Ordinal: 1,
            IsIdentity: true,
            IdentitySeed: 1000,
            IdentityIncrement: 5);

        col.IsIdentity.Should().BeTrue();
        col.IdentitySeed.Should().Be(1000);
        col.IdentityIncrement.Should().Be(5);
    }

    [Fact]
    public void Column_carries_computed_expression_for_persisted_columns()
    {
        Column col = new(
            Name: "FullName",
            DataType: "nvarchar(200)",
            IsNullable: true,
            Ordinal: 5,
            ComputedExpression: "([FirstName]+N' '+[LastName])",
            IsPersistedComputed: true);

        col.ComputedExpression.Should().Be("([FirstName]+N' '+[LastName])");
        col.IsPersistedComputed.Should().BeTrue();
    }

    [Fact]
    public void Column_defaults_have_no_identity_or_computed()
    {
        Column col = new("Name", "nvarchar(100)", true, 2);

        col.IdentitySeed.Should().BeNull();
        col.IdentityIncrement.Should().BeNull();
        col.ComputedExpression.Should().BeNull();
        col.IsPersistedComputed.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ColumnExtensionsTests"
```

Expected: FAIL — `IdentitySeed`, `IdentityIncrement`, `ComputedExpression`, `IsPersistedComputed` not yet defined.

- [ ] **Step 3: Extend `Column.cs`**

Replace `src/DbDelta.Core/ObjectModel/Column.cs` with:

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A table column (sys.columns row). Extended in M2 to carry identity seed /
/// increment (when <see cref="IsIdentity"/>) and the persisted-computed
/// expression (when <see cref="ComputedExpression"/> is non-null).
/// </summary>
public sealed record Column(
    string Name,
    string DataType,
    bool IsNullable,
    int Ordinal,
    string? DefaultExpression = null,
    bool IsIdentity = false,
    long? IdentitySeed = null,
    long? IdentityIncrement = null,
    string? ComputedExpression = null,
    bool IsPersistedComputed = false);
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ColumnExtensionsTests"
```

Expected: PASS — 3 tests green.

- [ ] **Step 5: Build the whole solution to confirm no callsite regressed**

```bash
dotnet build DbDelta.sln
```

Expected: green. All existing `new Column(...)` call sites still compile because the new properties default to null/false.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Column.cs tests/DbDelta.Core.UnitTests/ObjectModel/ColumnExtensionsTests.cs
git commit -m "feat(core): extend Column with identity seed/increment + computed expression"
```

---

## Phase B — Constraint object model

### Task T2.2: Add `Constraint` base + `PrimaryKey` + `UniqueConstraint`

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/Constraint.cs`
- Create: `src/DbDelta.Core/ObjectModel/PrimaryKey.cs`
- Create: `src/DbDelta.Core/ObjectModel/UniqueConstraint.cs`
- Test: `tests/DbDelta.Core.UnitTests/ObjectModel/ConstraintTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/DbDelta.Core.UnitTests/ObjectModel/ConstraintTests.cs`:

```csharp
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class ConstraintTests
{
    [Fact]
    public void PrimaryKey_carries_ordered_columns_and_clustered_flag()
    {
        PrimaryKey pk = new(
            Name: "PK_Customer",
            Columns: ["Id", "TenantId"],
            IsClustered: true);

        pk.Name.Should().Be("PK_Customer");
        pk.Columns.Should().Equal("Id", "TenantId");
        pk.IsClustered.Should().BeTrue();
        pk.Kind.Should().Be("PrimaryKey");
    }

    [Fact]
    public void UniqueConstraint_records_ordered_columns()
    {
        UniqueConstraint uq = new(
            Name: "UQ_Customer_Email",
            Columns: ["Email"],
            IsClustered: false);

        uq.Name.Should().Be("UQ_Customer_Email");
        uq.Columns.Should().Equal("Email");
        uq.IsClustered.Should().BeFalse();
        uq.Kind.Should().Be("UniqueConstraint");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ConstraintTests"
```

Expected: FAIL — types not defined.

- [ ] **Step 3: Write `Constraint.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Common shape for every table-level constraint. Concrete records carry the
/// shape-specific data; <see cref="Kind"/> is the discriminator used by
/// emitters and the diff engine.
/// </summary>
public abstract record Constraint(string Name)
{
    public abstract string Kind { get; }
}
```

- [ ] **Step 4: Write `PrimaryKey.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `PRIMARY KEY` constraint. Column ordering is significant and is preserved.
/// </summary>
public sealed record PrimaryKey(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsClustered) : Constraint(Name)
{
    public override string Kind => "PrimaryKey";
}
```

- [ ] **Step 5: Write `UniqueConstraint.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `UNIQUE` constraint (not the same as a unique index — see <see cref="Index"/>).
/// </summary>
public sealed record UniqueConstraint(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsClustered) : Constraint(Name)
{
    public override string Kind => "UniqueConstraint";
}
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ConstraintTests"
```

Expected: PASS — 2 tests green.

- [ ] **Step 7: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Constraint.cs src/DbDelta.Core/ObjectModel/PrimaryKey.cs src/DbDelta.Core/ObjectModel/UniqueConstraint.cs tests/DbDelta.Core.UnitTests/ObjectModel/ConstraintTests.cs
git commit -m "feat(core): add Constraint base + PrimaryKey + UniqueConstraint records"
```

---

### Task T2.3: Add `ForeignKey`

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/ForeignKey.cs`
- Modify: `tests/DbDelta.Core.UnitTests/ObjectModel/ConstraintTests.cs`

- [ ] **Step 1: Add the failing test (append to `ConstraintTests`)**

Append the following `[Fact]` to `ConstraintTests.cs`:

```csharp
    [Fact]
    public void ForeignKey_pairs_local_columns_to_referenced_columns_with_actions()
    {
        ForeignKey fk = new(
            Name: "FK_Order_Customer",
            Columns: ["CustomerId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Customer",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.Cascade,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);

        fk.Kind.Should().Be("ForeignKey");
        fk.Columns.Should().Equal("CustomerId");
        fk.ReferencedTable.Should().Be("Customer");
        fk.ReferencedColumns.Should().Equal("Id");
        fk.OnDelete.Should().Be(ReferentialAction.Cascade);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ForeignKey_pairs_local_columns"
```

Expected: FAIL — `ForeignKey` / `ReferentialAction` not defined.

- [ ] **Step 3: Write `ForeignKey.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Action SQL Server takes when a referenced row is updated/deleted.
/// </summary>
public enum ReferentialAction
{
    NoAction,
    Cascade,
    SetNull,
    SetDefault,
}

/// <summary>
/// `FOREIGN KEY` constraint. Always columnar; cross-database references are
/// out-of-scope for v1.
/// </summary>
public sealed record ForeignKey(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnDelete,
    ReferentialAction OnUpdate,
    bool IsDisabled,
    bool IsNotForReplication) : Constraint(Name)
{
    public override string Kind => "ForeignKey";
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ForeignKey_pairs_local_columns"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/ForeignKey.cs tests/DbDelta.Core.UnitTests/ObjectModel/ConstraintTests.cs
git commit -m "feat(core): add ForeignKey record with ReferentialAction enum"
```

---

### Task T2.4: Add `CheckConstraint` + `DefaultConstraint`

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/CheckConstraint.cs`
- Create: `src/DbDelta.Core/ObjectModel/DefaultConstraint.cs`
- Modify: `tests/DbDelta.Core.UnitTests/ObjectModel/ConstraintTests.cs`

- [ ] **Step 1: Append failing tests to `ConstraintTests`**

```csharp
    [Fact]
    public void CheckConstraint_carries_expression_and_disabled_flag()
    {
        CheckConstraint ck = new(
            Name: "CK_Customer_Age",
            Expression: "([Age] >= 0)",
            IsDisabled: false,
            IsNotForReplication: false);

        ck.Kind.Should().Be("CheckConstraint");
        ck.Expression.Should().Be("([Age] >= 0)");
    }

    [Fact]
    public void DefaultConstraint_binds_an_expression_to_a_single_column()
    {
        DefaultConstraint df = new(
            Name: "DF_Customer_CreatedAt",
            ColumnName: "CreatedAt",
            Expression: "(sysutcdatetime())");

        df.Kind.Should().Be("DefaultConstraint");
        df.ColumnName.Should().Be("CreatedAt");
        df.Expression.Should().Be("(sysutcdatetime())");
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ConstraintTests"
```

Expected: FAIL — `CheckConstraint` / `DefaultConstraint` not defined.

- [ ] **Step 3: Write `CheckConstraint.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// `CHECK` constraint. <see cref="Expression"/> is verbatim T-SQL captured
/// from <c>sys.check_constraints.definition</c>.
/// </summary>
public sealed record CheckConstraint(
    string Name,
    string Expression,
    bool IsDisabled,
    bool IsNotForReplication) : Constraint(Name)
{
    public override string Kind => "CheckConstraint";
}
```

- [ ] **Step 4: Write `DefaultConstraint.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Named `DEFAULT` constraint. The expression is also surfaced on
/// <see cref="Column.DefaultExpression"/> for convenience, but this record is
/// the source of truth for the constraint's name (which matters for ALTER /
/// DROP CONSTRAINT emission).
/// </summary>
public sealed record DefaultConstraint(
    string Name,
    string ColumnName,
    string Expression) : Constraint(Name)
{
    public override string Kind => "DefaultConstraint";
}
```

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ConstraintTests"
```

Expected: PASS — 5 tests green in `ConstraintTests`.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/CheckConstraint.cs src/DbDelta.Core/ObjectModel/DefaultConstraint.cs tests/DbDelta.Core.UnitTests/ObjectModel/ConstraintTests.cs
git commit -m "feat(core): add CheckConstraint + DefaultConstraint records"
```

---

## Phase C — Index object model

### Task T2.5: Add `Index` + `IndexColumn`

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/IndexColumn.cs`
- Create: `src/DbDelta.Core/ObjectModel/Index.cs`
- Test: `tests/DbDelta.Core.UnitTests/ObjectModel/IndexTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class IndexTests
{
    [Fact]
    public void Index_has_ordered_key_columns_and_optional_included_columns()
    {
        Index ix = new(
            Name: "IX_Order_CustomerId_OrderDate",
            IsUnique: false,
            IsClustered: false,
            FilterExpression: null,
            KeyColumns: [
                new IndexColumn("CustomerId", IsDescending: false),
                new IndexColumn("OrderDate",  IsDescending: true),
            ],
            IncludedColumns: ["TotalAmount"]);

        ix.Name.Should().Be("IX_Order_CustomerId_OrderDate");
        ix.KeyColumns.Should().HaveCount(2);
        ix.KeyColumns[1].IsDescending.Should().BeTrue();
        ix.IncludedColumns.Should().Equal("TotalAmount");
        ix.FilterExpression.Should().BeNull();
    }

    [Fact]
    public void Filtered_index_carries_its_predicate()
    {
        Index ix = new(
            Name: "IX_Order_Active",
            IsUnique: false,
            IsClustered: false,
            FilterExpression: "([IsDeleted]=(0))",
            KeyColumns: [new IndexColumn("Id", false)],
            IncludedColumns: []);

        ix.FilterExpression.Should().Be("([IsDeleted]=(0))");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~IndexTests"
```

Expected: FAIL — `Index` / `IndexColumn` not defined.

- [ ] **Step 3: Write `IndexColumn.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// One column participating in an <see cref="Index"/>'s key list.
/// </summary>
public sealed record IndexColumn(string Name, bool IsDescending);
```

- [ ] **Step 4: Write `Index.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A non-PK / non-UQ index on a table. PK and UQ indexes are modeled as
/// <see cref="PrimaryKey"/> / <see cref="UniqueConstraint"/> on the parent
/// <see cref="Table"/> — they are NOT duplicated here.
/// </summary>
public sealed record Index(
    string Name,
    bool IsUnique,
    bool IsClustered,
    string? FilterExpression,
    IReadOnlyList<IndexColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns);
```

- [ ] **Step 5: Run test to verify it passes**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~IndexTests"
```

Expected: PASS — 2 tests green.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/IndexColumn.cs src/DbDelta.Core/ObjectModel/Index.cs tests/DbDelta.Core.UnitTests/ObjectModel/IndexTests.cs
git commit -m "feat(core): add Index + IndexColumn records"
```

---

## Phase D — Table extensions

### Task T2.6: Extend `Table` with `Constraints` + `Indexes`

**Files:**
- Modify: `src/DbDelta.Core/ObjectModel/Table.cs`
- Modify: `tests/DbDelta.Core.UnitTests/ObjectModel/TableTests.cs`

- [ ] **Step 1: Append failing tests to `TableTests`**

```csharp
    [Fact]
    public void Table_can_hold_a_primary_key_and_indexes()
    {
        Table t = new(
            Schema: "dbo",
            Name: "Customer",
            Columns: [new Column("Id", "int", false, 1, IsIdentity: true)],
            Constraints: [new PrimaryKey("PK_Customer", ["Id"], IsClustered: true)],
            Indexes:
            [
                new Index(
                    Name: "IX_Customer_Name",
                    IsUnique: false,
                    IsClustered: false,
                    FilterExpression: null,
                    KeyColumns: [new IndexColumn("Name", false)],
                    IncludedColumns: [])
            ]);

        t.Constraints.Should().ContainSingle(c => c.Kind == "PrimaryKey");
        t.Indexes.Should().ContainSingle(i => i.Name == "IX_Customer_Name");
    }

    [Fact]
    public void Tables_constructed_with_legacy_two_arg_constructor_have_empty_collections()
    {
        Table t = new("dbo", "Legacy", [new Column("Id", "int", false, 1)]);

        t.Constraints.Should().BeEmpty();
        t.Indexes.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~TableTests"
```

Expected: FAIL — `Constraints` / `Indexes` not present on `Table`.

- [ ] **Step 3: Replace `Table.cs`**

```csharp
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A user table (sys.tables row) and everything that hangs off it: columns,
/// table-level constraints (PK/FK/UQ/CK/DEFAULT), and indexes.
/// </summary>
public sealed record Table(
    string Schema,
    string Name,
    IReadOnlyList<Column> Columns,
    IReadOnlyList<Constraint> Constraints,
    IReadOnlyList<Index> Indexes)
{
    /// <summary>
    /// Convenience constructor that creates a table with no constraints or
    /// indexes — used by M1 callers and tests that only care about columns.
    /// </summary>
    public Table(string schema, string name, IReadOnlyList<Column> columns)
        : this(schema, name, columns, [], []) { }

    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Table");
}

/// <summary>
/// Tuple identifying an object across two schemas being compared.
/// </summary>
public readonly record struct ObjectIdentity(string SchemaName, string ObjectName, string Kind);
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~TableTests"
```

Expected: PASS — existing M1 tests still green, two new tests green.

- [ ] **Step 5: Run the full Core unit suite to confirm no regression**

```bash
dotnet test tests/DbDelta.Core.UnitTests
```

Expected: every test in the assembly passes.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Table.cs tests/DbDelta.Core.UnitTests/ObjectModel/TableTests.cs
git commit -m "feat(core): extend Table with Constraints + Indexes collections"
```

---

## Phase E — Diff extensions

### Task T2.7: Extend `ComparisonEngine` to diff constraints + indexes

**Files:**
- Modify: `src/DbDelta.Core/Diff/ComparisonEngine.cs`
- Create: `tests/DbDelta.Core.UnitTests/Diff/ConstraintDiffTests.cs`
- Create: `tests/DbDelta.Core.UnitTests/Diff/IndexDiffTests.cs`

- [ ] **Step 1: Write `ConstraintDiffTests.cs`**

```csharp
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ConstraintDiffTests
{
    private static Database DbWithTable(Table t) =>
        new("X", [new Schema("dbo")], [t]);

    private static Table TableWith(params Constraint[] constraints) =>
        new("dbo", "Customer",
            Columns: [new Column("Id", "int", false, 1)],
            Constraints: constraints,
            Indexes: []);

    [Fact]
    public void Identical_PK_yields_Identical()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));
        Database b = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Should().ContainSingle().Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Different_PK_column_order_yields_Different()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id", "TenantId"], true)));
        Database b = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["TenantId", "Id"], true)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Missing_PK_on_target_yields_Different()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));
        Database b = DbWithTable(TableWith());

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Different_FK_referenced_table_yields_Different()
    {
        ForeignKey fkA = new("FK", ["CustomerId"], "dbo", "Customer", ["Id"],
            ReferentialAction.NoAction, ReferentialAction.NoAction, false, false);
        ForeignKey fkB = new("FK", ["CustomerId"], "dbo", "Client", ["Id"],
            ReferentialAction.NoAction, ReferentialAction.NoAction, false, false);

        Database a = DbWithTable(TableWith(fkA));
        Database b = DbWithTable(TableWith(fkB));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Check_constraint_expression_change_yields_Different()
    {
        Database a = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=0)", false, false)));
        Database b = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=1)", false, false)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void IgnoreKeys_option_skips_PK_diff()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK", ["Id"], true)));
        Database b = DbWithTable(TableWith());

        ComparisonResult r = new ComparisonEngine()
            .Compare(a, b, ComparisonOptions.Default | ComparisonOptions.IgnoreKeys);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }
}
```

- [ ] **Step 2: Write `IndexDiffTests.cs`**

```csharp
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class IndexDiffTests
{
    private static Database DbWithTable(Table t) =>
        new("X", [new Schema("dbo")], [t]);

    private static Table TableWith(params Index[] indexes) =>
        new("dbo", "Customer",
            Columns: [new Column("Id", "int", false, 1)],
            Constraints: [],
            Indexes: indexes);

    private static Index Ix(string name, params string[] keys) =>
        new(name, false, false, null,
            keys.Select(k => new IndexColumn(k, false)).ToArray(),
            []);

    [Fact]
    public void Identical_indexes_yield_Identical()
    {
        Database a = DbWithTable(TableWith(Ix("IX1", "Name")));
        Database b = DbWithTable(TableWith(Ix("IX1", "Name")));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Different_key_columns_yield_Different()
    {
        Database a = DbWithTable(TableWith(Ix("IX1", "Name")));
        Database b = DbWithTable(TableWith(Ix("IX1", "Email")));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Different_included_columns_yield_Different()
    {
        Index a1 = new("IX1", false, false, null,
            [new IndexColumn("Name", false)], ["Email"]);
        Index b1 = new("IX1", false, false, null,
            [new IndexColumn("Name", false)], []);

        Database a = DbWithTable(TableWith(a1));
        Database b = DbWithTable(TableWith(b1));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void IgnoreIndexes_option_skips_index_diff()
    {
        Database a = DbWithTable(TableWith(Ix("IX1", "Name")));
        Database b = DbWithTable(TableWith(Ix("IX1", "Email")));

        ComparisonResult r = new ComparisonEngine()
            .Compare(a, b, ComparisonOptions.Default | ComparisonOptions.IgnoreIndexes);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ConstraintDiffTests|FullyQualifiedName~IndexDiffTests"
```

Expected: FAIL — engine does not yet consider constraints / indexes.

- [ ] **Step 4: Replace `ComparisonEngine.cs`**

```csharp
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Core.Diff;

/// <summary>
/// Pure comparison engine: pair tables by identity, then within each pair
/// compare columns, constraints, and indexes per options. Pure → no I/O.
/// </summary>
public sealed class ComparisonEngine
{
    public ComparisonResult Compare(Database a, Database b, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        Dictionary<ObjectIdentity, Table> aByIdentity = a.Tables.ToDictionary(t => t.Identity);
        Dictionary<ObjectIdentity, Table> bByIdentity = b.Tables.ToDictionary(t => t.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys, .. bByIdentity.Keys];

        List<DifferencePair> pairs = new(allIdentities.Count);
        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Table? sideA);
            bByIdentity.TryGetValue(id, out Table? sideB);
            DifferenceStatus status = ClassifyTable(sideA, sideB, options);
            pairs.Add(new DifferencePair(id, status, sideA, sideB));
        }

        return new ComparisonResult(pairs);
    }

    private static DifferenceStatus ClassifyTable(Table? a, Table? b, ComparisonOptions options)
    {
        if (a is null && b is not null) return DifferenceStatus.OnlyInB;
        if (a is not null && b is null) return DifferenceStatus.OnlyInA;
        if (a is null || b is null) return DifferenceStatus.Identical;

        if (!ColumnsEqual(a.Columns, b.Columns, options)) return DifferenceStatus.Different;
        if (!options.HasFlag(ComparisonOptions.IgnoreKeys)
            && !ConstraintsEqual(a.Constraints, b.Constraints)) return DifferenceStatus.Different;
        if (!options.HasFlag(ComparisonOptions.IgnoreIndexes)
            && !IndexesEqual(a.Indexes, b.Indexes)) return DifferenceStatus.Different;

        return DifferenceStatus.Identical;
    }

    private static bool ColumnsEqual(
        IReadOnlyList<Column> ax,
        IReadOnlyList<Column> bx,
        ComparisonOptions options)
    {
        if (ax.Count != bx.Count) return false;
        Dictionary<string, Column> bByName = bx.ToDictionary(c => c.Name);
        foreach (Column col in ax)
        {
            if (!bByName.TryGetValue(col.Name, out Column? other)) return false;
            if (col.DataType != other.DataType) return false;
            if (col.IsNullable != other.IsNullable) return false;
            if (col.IsIdentity != other.IsIdentity) return false;
            if (col.IsIdentity && (col.IdentitySeed != other.IdentitySeed
                || col.IdentityIncrement != other.IdentityIncrement)) return false;
            if ((col.DefaultExpression ?? "") != (other.DefaultExpression ?? "")) return false;
            if ((col.ComputedExpression ?? "") != (other.ComputedExpression ?? "")) return false;
            if (col.IsPersistedComputed != other.IsPersistedComputed) return false;

            if (options.HasFlag(ComparisonOptions.ForceColumnOrder) && col.Ordinal != other.Ordinal)
                return false;
        }
        return true;
    }

    private static bool ConstraintsEqual(
        IReadOnlyList<Constraint> ax,
        IReadOnlyList<Constraint> bx)
    {
        if (ax.Count != bx.Count) return false;
        Dictionary<string, Constraint> bByName = bx.ToDictionary(c => c.Name);
        foreach (Constraint left in ax)
        {
            if (!bByName.TryGetValue(left.Name, out Constraint? right)) return false;
            if (left.Kind != right.Kind) return false;
            if (!ConstraintShapeEqual(left, right)) return false;
        }
        return true;
    }

    private static bool ConstraintShapeEqual(Constraint left, Constraint right) => left switch
    {
        PrimaryKey pk when right is PrimaryKey other =>
            pk.IsClustered == other.IsClustered && pk.Columns.SequenceEqual(other.Columns),
        UniqueConstraint uq when right is UniqueConstraint other =>
            uq.IsClustered == other.IsClustered && uq.Columns.SequenceEqual(other.Columns),
        ForeignKey fk when right is ForeignKey other =>
            fk.Columns.SequenceEqual(other.Columns)
            && fk.ReferencedSchema == other.ReferencedSchema
            && fk.ReferencedTable == other.ReferencedTable
            && fk.ReferencedColumns.SequenceEqual(other.ReferencedColumns)
            && fk.OnDelete == other.OnDelete
            && fk.OnUpdate == other.OnUpdate
            && fk.IsDisabled == other.IsDisabled
            && fk.IsNotForReplication == other.IsNotForReplication,
        CheckConstraint ck when right is CheckConstraint other =>
            ck.Expression == other.Expression
            && ck.IsDisabled == other.IsDisabled
            && ck.IsNotForReplication == other.IsNotForReplication,
        DefaultConstraint df when right is DefaultConstraint other =>
            df.ColumnName == other.ColumnName && df.Expression == other.Expression,
        _ => false,
    };

    private static bool IndexesEqual(
        IReadOnlyList<Index> ax,
        IReadOnlyList<Index> bx)
    {
        if (ax.Count != bx.Count) return false;
        Dictionary<string, Index> bByName = bx.ToDictionary(i => i.Name);
        foreach (Index left in ax)
        {
            if (!bByName.TryGetValue(left.Name, out Index? right)) return false;
            if (left.IsUnique != right.IsUnique) return false;
            if (left.IsClustered != right.IsClustered) return false;
            if ((left.FilterExpression ?? "") != (right.FilterExpression ?? "")) return false;
            if (left.KeyColumns.Count != right.KeyColumns.Count) return false;
            for (int i = 0; i < left.KeyColumns.Count; i++)
            {
                if (left.KeyColumns[i].Name != right.KeyColumns[i].Name) return false;
                if (left.KeyColumns[i].IsDescending != right.KeyColumns[i].IsDescending) return false;
            }
            if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns)) return false;
        }
        return true;
    }
}
```

- [ ] **Step 5: Run new diff tests**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ConstraintDiffTests|FullyQualifiedName~IndexDiffTests"
```

Expected: PASS — 10 tests green (6 constraint + 4 index).

- [ ] **Step 6: Run all M1 diff tests to confirm no regression**

```bash
dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~ComparisonEngineTests"
```

Expected: PASS — M1 tests still green.

- [ ] **Step 7: Commit**

```bash
git add src/DbDelta.Core/Diff/ComparisonEngine.cs tests/DbDelta.Core.UnitTests/Diff/ConstraintDiffTests.cs tests/DbDelta.Core.UnitTests/Diff/IndexDiffTests.cs
git commit -m "feat(core): diff constraints + indexes within paired tables (honors IgnoreKeys/IgnoreIndexes)"
```

---

## Phase F — LiveDb readers

### Task T2.8: Enrich `TableReader` with identity seed/increment + computed expression

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs`

- [ ] **Step 1: Replace the columns query and reader body**

Open `src/DbDelta.Providers.LiveDb/Readers/TableReader.cs` and replace the `ColumnsQuery` constant + the column-reading block (Step 2 inside `ReadAsync`) with the following.

Replace `ColumnsQuery`:

```csharp
    private const string ColumnsQuery = """
        SELECT
            c.object_id            AS ObjectId,
            c.name                 AS ColumnName,
            TYPE_NAME(c.user_type_id) AS TypeName,
            c.max_length           AS MaxLength,
            c.precision            AS [Precision],
            c.scale                AS Scale,
            c.is_nullable          AS IsNullable,
            c.is_identity          AS IsIdentity,
            CAST(ic.seed_value AS bigint)      AS IdentitySeed,
            CAST(ic.increment_value AS bigint) AS IdentityIncrement,
            dc.definition          AS DefaultExpression,
            cc.definition          AS ComputedExpression,
            ISNULL(cc.is_persisted, 0)         AS IsPersistedComputed,
            c.column_id            AS Ordinal
        FROM sys.columns AS c
        INNER JOIN sys.tables AS t ON t.object_id = c.object_id
        LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id
                                             AND ic.column_id = c.column_id
        LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id
                                                AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id
                                              AND cc.column_id = c.column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;
        """;
```

Replace the body of the second `await using` block in `ReadAsync` (the columns reader) so the `new Column(...)` call captures the new fields:

```csharp
            while (await columnsReader.ReadAsync(ct).ConfigureAwait(false))
            {
                int objectId = columnsReader.GetInt32(0);
                string columnName = columnsReader.GetString(1);
                string typeName = columnsReader.GetString(2);
                short maxLength = columnsReader.GetInt16(3);
                byte precision = columnsReader.GetByte(4);
                byte scale = columnsReader.GetByte(5);
                bool isNullable = columnsReader.GetBoolean(6);
                bool isIdentity = columnsReader.GetBoolean(7);
                long? identitySeed = columnsReader.IsDBNull(8) ? null : columnsReader.GetInt64(8);
                long? identityIncrement = columnsReader.IsDBNull(9) ? null : columnsReader.GetInt64(9);
                string? defaultExpr = columnsReader.IsDBNull(10) ? null : columnsReader.GetString(10);
                string? computedExpr = columnsReader.IsDBNull(11) ? null : columnsReader.GetString(11);
                bool isPersistedComputed = !columnsReader.IsDBNull(12) && columnsReader.GetBoolean(12);
                int ordinal = columnsReader.GetInt32(13);

                if (!tableShells.ContainsKey(objectId))
                {
                    continue;
                }

                if (!columnsByObjectId.TryGetValue(objectId, out List<Column>? list))
                {
                    list = [];
                    columnsByObjectId[objectId] = list;
                }
                list.Add(new Column(
                    Name: columnName,
                    DataType: FormatDataType(typeName, maxLength, precision, scale),
                    IsNullable: isNullable,
                    Ordinal: ordinal,
                    DefaultExpression: defaultExpr,
                    IsIdentity: isIdentity,
                    IdentitySeed: identitySeed,
                    IdentityIncrement: identityIncrement,
                    ComputedExpression: computedExpr,
                    IsPersistedComputed: isPersistedComputed));
            }
```

- [ ] **Step 2: Build**

```bash
dotnet build src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj
```

Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/TableReader.cs
git commit -m "feat(providers/livedb): enrich Column read with identity seed/increment + computed expression"
```

---

### Task T2.9: Create `ConstraintReader` skeleton + PK + UQ readers

**Files:**
- Create: `src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs`

- [ ] **Step 1: Write the reader**

```csharp
using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads PK / UQ / FK / CK / DEFAULT constraints in a small number of batched
/// queries against <c>sys.*</c> catalog views.
/// </summary>
internal sealed class ConstraintReader
{
    private const string KeysQuery = """
        SELECT
            kc.parent_object_id AS ObjectId,
            kc.name             AS ConstraintName,
            kc.type             AS ConstraintType,   -- 'PK' or 'UQ'
            i.type              AS IndexType,        -- 1 = clustered, 2 = nonclustered
            ic.key_ordinal      AS KeyOrdinal,
            c.name              AS ColumnName
        FROM sys.key_constraints AS kc
        INNER JOIN sys.indexes AS i ON i.object_id = kc.parent_object_id
                                    AND i.index_id = kc.unique_index_id
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                           AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c ON c.object_id = ic.object_id
                                    AND c.column_id = ic.column_id
        INNER JOIN sys.tables AS t ON t.object_id = kc.parent_object_id
        WHERE t.is_ms_shipped = 0
          AND ic.is_included_column = 0
        ORDER BY kc.parent_object_id, kc.name, ic.key_ordinal;
        """;

    public async Task<IReadOnlyDictionary<int, List<Constraint>>> ReadAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        Dictionary<int, List<Constraint>> byObject = [];

        // Accumulator state for the streaming aggregate
        int? currentObjectId = null;
        string? currentName = null;
        string? currentType = null;
        bool isClustered = false;
        List<string> currentCols = [];

        await using SqlCommand cmd = new(KeysQuery, connection);
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = reader.GetInt32(0);
            string name = reader.GetString(1);
            string type = reader.GetString(2).Trim();
            byte indexType = reader.GetByte(3);
            string column = reader.GetString(5);

            if (currentName is not null && (currentName != name || currentObjectId != objectId))
            {
                Append(byObject, currentObjectId!.Value, currentName, currentType!, isClustered, currentCols);
                currentCols = [];
            }

            currentObjectId = objectId;
            currentName = name;
            currentType = type;
            isClustered = indexType == 1;
            currentCols.Add(column);
        }

        if (currentName is not null)
        {
            Append(byObject, currentObjectId!.Value, currentName, currentType!, isClustered, currentCols);
        }

        return byObject;
    }

    private static void Append(
        Dictionary<int, List<Constraint>> byObject,
        int objectId,
        string name,
        string type,
        bool isClustered,
        List<string> columns)
    {
        Constraint c = type switch
        {
            "PK" => new PrimaryKey(name, columns.ToArray(), isClustered),
            "UQ" => new UniqueConstraint(name, columns.ToArray(), isClustered),
            _ => throw new InvalidOperationException($"Unexpected key constraint type '{type}'."),
        };
        if (!byObject.TryGetValue(objectId, out List<Constraint>? list))
        {
            list = [];
            byObject[objectId] = list;
        }
        list.Add(c);
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj
```

Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs
git commit -m "feat(providers/livedb): ConstraintReader reads PK + UQ via sys.key_constraints"
```

---

### Task T2.10: Extend `ConstraintReader` with FK reader

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs`

- [ ] **Step 1: Add a new query + method, and wire it into `ReadAsync`**

Inside `ConstraintReader` add the FK query and a small helper that fills the same `byObject` dictionary:

```csharp
    private const string ForeignKeysQuery = """
        SELECT
            fk.parent_object_id    AS ObjectId,
            fk.name                AS ConstraintName,
            cp.name                AS LocalColumn,
            sr.name                AS RefSchema,
            tr.name                AS RefTable,
            cr.name                AS RefColumn,
            fkc.constraint_column_id AS Ordinal,
            fk.delete_referential_action AS OnDelete,
            fk.update_referential_action AS OnUpdate,
            fk.is_disabled         AS IsDisabled,
            fk.is_not_for_replication AS IsNotForReplication
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        INNER JOIN sys.tables AS tp ON tp.object_id = fk.parent_object_id
        INNER JOIN sys.tables AS tr ON tr.object_id = fk.referenced_object_id
        INNER JOIN sys.schemas AS sr ON sr.schema_id = tr.schema_id
        INNER JOIN sys.columns AS cp ON cp.object_id = fkc.parent_object_id
                                     AND cp.column_id = fkc.parent_column_id
        INNER JOIN sys.columns AS cr ON cr.object_id = fkc.referenced_object_id
                                     AND cr.column_id = fkc.referenced_column_id
        WHERE tp.is_ms_shipped = 0
        ORDER BY fk.parent_object_id, fk.name, fkc.constraint_column_id;
        """;

    private static async Task ReadForeignKeysAsync(
        SqlConnection connection,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        int? currentObjectId = null;
        string? currentName = null;
        string? refSchema = null;
        string? refTable = null;
        ReferentialAction onDelete = ReferentialAction.NoAction;
        ReferentialAction onUpdate = ReferentialAction.NoAction;
        bool isDisabled = false;
        bool isNfr = false;
        List<string> localCols = [];
        List<string> refCols = [];

        await using SqlCommand cmd = new(ForeignKeysQuery, connection);
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = reader.GetInt32(0);
            string name = reader.GetString(1);
            string localCol = reader.GetString(2);
            string rs = reader.GetString(3);
            string rt = reader.GetString(4);
            string rc = reader.GetString(5);
            byte onDel = reader.GetByte(7);
            byte onUpd = reader.GetByte(8);
            bool disabled = reader.GetBoolean(9);
            bool nfr = reader.GetBoolean(10);

            if (currentName is not null && (currentName != name || currentObjectId != objectId))
            {
                FlushForeignKey(byObject, currentObjectId!.Value, currentName,
                    localCols, refSchema!, refTable!, refCols,
                    onDelete, onUpdate, isDisabled, isNfr);
                localCols = [];
                refCols = [];
            }

            currentObjectId = objectId;
            currentName = name;
            refSchema = rs;
            refTable = rt;
            onDelete = MapAction(onDel);
            onUpdate = MapAction(onUpd);
            isDisabled = disabled;
            isNfr = nfr;
            localCols.Add(localCol);
            refCols.Add(rc);
        }

        if (currentName is not null)
        {
            FlushForeignKey(byObject, currentObjectId!.Value, currentName,
                localCols, refSchema!, refTable!, refCols,
                onDelete, onUpdate, isDisabled, isNfr);
        }
    }

    private static void FlushForeignKey(
        Dictionary<int, List<Constraint>> byObject,
        int objectId,
        string name,
        List<string> localCols,
        string refSchema,
        string refTable,
        List<string> refCols,
        ReferentialAction onDelete,
        ReferentialAction onUpdate,
        bool isDisabled,
        bool isNotForReplication)
    {
        ForeignKey fk = new(
            Name: name,
            Columns: localCols.ToArray(),
            ReferencedSchema: refSchema,
            ReferencedTable: refTable,
            ReferencedColumns: refCols.ToArray(),
            OnDelete: onDelete,
            OnUpdate: onUpdate,
            IsDisabled: isDisabled,
            IsNotForReplication: isNotForReplication);

        if (!byObject.TryGetValue(objectId, out List<Constraint>? list))
        {
            list = [];
            byObject[objectId] = list;
        }
        list.Add(fk);
    }

    private static ReferentialAction MapAction(byte b) => b switch
    {
        0 => ReferentialAction.NoAction,
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction,
    };
```

Then update the public `ReadAsync` method body so it calls FK reading after the keys block. Replace the closing of `ReadAsync` so the final line set looks like this:

```csharp
        if (currentName is not null)
        {
            Append(byObject, currentObjectId!.Value, currentName, currentType!, isClustered, currentCols);
        }

        await ReadForeignKeysAsync(connection, byObject, ct).ConfigureAwait(false);

        return byObject;
    }
```

- [ ] **Step 2: Build**

```bash
dotnet build src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj
```

Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs
git commit -m "feat(providers/livedb): ConstraintReader reads foreign keys via sys.foreign_key_columns"
```

---

### Task T2.11: Extend `ConstraintReader` with CHECK + DEFAULT readers

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs`

- [ ] **Step 1: Add CHECK + DEFAULT query + handler methods**

Add inside the class:

```csharp
    private const string ChecksQuery = """
        SELECT
            cc.parent_object_id    AS ObjectId,
            cc.name                AS ConstraintName,
            cc.definition          AS Expression,
            cc.is_disabled         AS IsDisabled,
            cc.is_not_for_replication AS IsNotForReplication
        FROM sys.check_constraints AS cc
        INNER JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY cc.parent_object_id, cc.name;
        """;

    private const string DefaultsQuery = """
        SELECT
            dc.parent_object_id    AS ObjectId,
            dc.name                AS ConstraintName,
            c.name                 AS ColumnName,
            dc.definition          AS Expression
        FROM sys.default_constraints AS dc
        INNER JOIN sys.columns AS c ON c.object_id = dc.parent_object_id
                                    AND c.column_id = dc.parent_column_id
        INNER JOIN sys.tables AS t ON t.object_id = dc.parent_object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY dc.parent_object_id, dc.name;
        """;

    private static async Task ReadChecksAsync(
        SqlConnection connection,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        await using SqlCommand cmd = new(ChecksQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            CheckConstraint ck = new(
                Name: r.GetString(1),
                Expression: r.GetString(2),
                IsDisabled: r.GetBoolean(3),
                IsNotForReplication: r.GetBoolean(4));
            if (!byObject.TryGetValue(objectId, out List<Constraint>? list))
            {
                list = [];
                byObject[objectId] = list;
            }
            list.Add(ck);
        }
    }

    private static async Task ReadDefaultsAsync(
        SqlConnection connection,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        await using SqlCommand cmd = new(DefaultsQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            DefaultConstraint df = new(
                Name: r.GetString(1),
                ColumnName: r.GetString(2),
                Expression: r.GetString(3));
            if (!byObject.TryGetValue(objectId, out List<Constraint>? list))
            {
                list = [];
                byObject[objectId] = list;
            }
            list.Add(df);
        }
    }
```

Update `ReadAsync` so the end now looks like:

```csharp
        if (currentName is not null)
        {
            Append(byObject, currentObjectId!.Value, currentName, currentType!, isClustered, currentCols);
        }

        await ReadForeignKeysAsync(connection, byObject, ct).ConfigureAwait(false);
        await ReadChecksAsync(connection, byObject, ct).ConfigureAwait(false);
        await ReadDefaultsAsync(connection, byObject, ct).ConfigureAwait(false);

        return byObject;
    }
```

- [ ] **Step 2: Build**

```bash
dotnet build src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj
```

Expected: green.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs
git commit -m "feat(providers/livedb): ConstraintReader reads CHECK + DEFAULT constraints"
```

---

### Task T2.12: Create `IndexReader` and wire it into `LiveDbSource`

**Files:**
- Create: `src/DbDelta.Providers.LiveDb/Readers/IndexReader.cs`
- Modify: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs`

- [ ] **Step 1: Write `IndexReader.cs`**

```csharp
using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads non-PK / non-UQ indexes (those are modeled as constraints). Picks up
/// clustered/non-clustered key indexes, unique indexes, filtered indexes, and
/// included columns.
/// </summary>
internal sealed class IndexReader
{
    private const string IndexesQuery = """
        SELECT
            i.object_id          AS ObjectId,
            i.name               AS IndexName,
            i.is_unique          AS IsUnique,
            i.type               AS IndexType,           -- 1 = clustered, 2 = nonclustered
            i.has_filter         AS HasFilter,
            i.filter_definition  AS FilterDefinition,
            ic.index_id          AS IndexId,
            ic.key_ordinal       AS KeyOrdinal,
            ic.is_descending_key AS IsDescending,
            ic.is_included_column AS IsIncluded,
            c.name               AS ColumnName
        FROM sys.indexes AS i
        INNER JOIN sys.tables AS t ON t.object_id = i.object_id
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                           AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c ON c.object_id = ic.object_id
                                    AND c.column_id = ic.column_id
        WHERE t.is_ms_shipped = 0
          AND i.is_primary_key = 0
          AND i.is_unique_constraint = 0
          AND i.type IN (1, 2)
          AND i.name IS NOT NULL
        ORDER BY i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal;
        """;

    public async Task<IReadOnlyDictionary<int, List<Index>>> ReadAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        Dictionary<int, List<Index>> byObject = [];

        int? currentObjectId = null;
        int? currentIndexId = null;
        string? currentName = null;
        bool isUnique = false;
        bool isClustered = false;
        string? filter = null;
        List<IndexColumn> keys = [];
        List<string> included = [];

        await using SqlCommand cmd = new(IndexesQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            string name = r.GetString(1);
            bool isUq = r.GetBoolean(2);
            byte indexType = r.GetByte(3);
            bool hasFilter = r.GetBoolean(4);
            string? filterDef = r.IsDBNull(5) ? null : r.GetString(5);
            int indexId = r.GetInt32(6);
            bool isDesc = r.GetBoolean(8);
            bool isIncl = r.GetBoolean(9);
            string column = r.GetString(10);

            if (currentIndexId is not null && (currentIndexId != indexId || currentObjectId != objectId))
            {
                Flush(byObject, currentObjectId!.Value, currentName!, isUnique, isClustered, filter, keys, included);
                keys = [];
                included = [];
            }

            currentObjectId = objectId;
            currentIndexId = indexId;
            currentName = name;
            isUnique = isUq;
            isClustered = indexType == 1;
            filter = hasFilter ? filterDef : null;

            if (isIncl)
            {
                included.Add(column);
            }
            else
            {
                keys.Add(new IndexColumn(column, isDesc));
            }
        }

        if (currentIndexId is not null)
        {
            Flush(byObject, currentObjectId!.Value, currentName!, isUnique, isClustered, filter, keys, included);
        }

        return byObject;
    }

    private static void Flush(
        Dictionary<int, List<Index>> byObject,
        int objectId,
        string name,
        bool isUnique,
        bool isClustered,
        string? filter,
        List<IndexColumn> keys,
        List<string> included)
    {
        Index ix = new(
            Name: name,
            IsUnique: isUnique,
            IsClustered: isClustered,
            FilterExpression: filter,
            KeyColumns: keys.ToArray(),
            IncludedColumns: included.ToArray());
        if (!byObject.TryGetValue(objectId, out List<Index>? list))
        {
            list = [];
            byObject[objectId] = list;
        }
        list.Add(ix);
    }
}
```

- [ ] **Step 2: Wire constraints + indexes into `LiveDbSource`**

Open `src/DbDelta.Providers.LiveDb/LiveDbSource.cs`. Replace the body of `LoadAsync` so it reads constraints and indexes after tables, then composes them into the final `Table` graph:

```csharp
    public async Task<Result<Database>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using SqlConnection connection = await ConnectionFactory.OpenAsync(_connectionString, cancellationToken);
            IReadOnlyList<Schema> schemas = await new SchemaReader().ReadAsync(connection, cancellationToken);

            // Tables with their columns (M1 behaviour)
            IReadOnlyList<Table> bareTables = await new TableReader().ReadAsync(connection, cancellationToken);

            // M2: constraints + indexes, keyed by sys.objects.object_id
            IReadOnlyDictionary<int, List<Constraint>> constraintsByObject =
                await new ConstraintReader().ReadAsync(connection, cancellationToken);
            IReadOnlyDictionary<int, List<Index>> indexesByObject =
                await new IndexReader().ReadAsync(connection, cancellationToken);

            // Re-key our existing tables by object id. TableReader doesn't expose the
            // object id today, so we ask SQL Server one more time and join client-side
            // to keep the public Table record clean (no DB-only IDs in the domain model).
            IReadOnlyDictionary<(string Schema, string Name), int> objectIdByName =
                await ReadTableObjectIdsAsync(connection, cancellationToken);

            List<Table> tables = new(bareTables.Count);
            foreach (Table t in bareTables)
            {
                int? objectId = objectIdByName.TryGetValue((t.Schema, t.Name), out int id) ? id : null;
                IReadOnlyList<Constraint> cons = objectId is int cid
                    && constraintsByObject.TryGetValue(cid, out List<Constraint>? cl)
                        ? cl
                        : [];
                IReadOnlyList<Index> idx = objectId is int iid
                    && indexesByObject.TryGetValue(iid, out List<Index>? il)
                        ? il
                        : [];
                tables.Add(t with { Constraints = cons, Indexes = idx });
            }

            string dbName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            return Result<Database>.Success(new Database(dbName, schemas, tables));
        }
        catch (SqlException ex) when (ex.Number is 4060 or 18456)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.AuthFailed,
                ex.Message,
                "Verify credentials and that the user has CONNECT permission on the database."));
        }
        catch (SqlException ex) when (ex.Number is 53 or -2)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.CannotConnect,
                ex.Message,
                "Verify server name, network connectivity, and firewall rules."));
        }
        catch (SqlException ex)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.CatalogQueryFailed,
                ex.Message));
        }
    }

    private static async Task<IReadOnlyDictionary<(string Schema, string Name), int>> ReadTableObjectIdsAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS SchemaName, t.name AS TableName, t.object_id
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0;
            """;
        Dictionary<(string Schema, string Name), int> map = [];
        await using SqlCommand cmd = new(sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            map[(r.GetString(0), r.GetString(1))] = r.GetInt32(2);
        }
        return map;
    }
```

- [ ] **Step 3: Build**

```bash
dotnet build src/DbDelta.Providers.LiveDb/DbDelta.Providers.LiveDb.csproj
```

Expected: green.

- [ ] **Step 4: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/IndexReader.cs src/DbDelta.Providers.LiveDb/LiveDbSource.cs
git commit -m "feat(providers/livedb): IndexReader + compose constraints/indexes into LiveDbSource"
```

---

## Phase G — Script generation

### Task T2.13: `TableScriptEmitter` — emit PK / UQ inline in `CREATE TABLE`

**Files:**
- Modify: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.cs`

- [ ] **Step 1: Write the failing golden test**

```csharp
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

public class TableWithConstraintsGoldenTests
{
    [Fact]
    public Task Create_table_with_clustered_PK_and_UQ()
    {
        Table t = new(
            Schema: "dbo",
            Name: "Customer",
            Columns:
            [
                new Column("Id",    "int",            false, 1, IsIdentity: true, IdentitySeed: 1, IdentityIncrement: 1),
                new Column("Email", "nvarchar(200)",  false, 2),
                new Column("Name",  "nvarchar(100)",  false, 3),
            ],
            Constraints:
            [
                new PrimaryKey("PK_Customer", ["Id"], IsClustered: true),
                new UniqueConstraint("UQ_Customer_Email", ["Email"], IsClustered: false),
            ],
            Indexes: []);
        DifferencePair pair = new(t.Identity, DifferenceStatus.OnlyInA, t, null);

        string ddl = new TableScriptEmitter().Emit(pair);
        return Verifier.Verify(ddl);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (no snapshot yet, also wrong DDL)**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_clustered_PK_and_UQ"
```

Expected: FAIL — Verify writes a `*.received.txt` next to the test; M1 emitter produces no constraints inline.

- [ ] **Step 3: Update `TableScriptEmitter.EmitCreate` to inline PK / UQ / identity**

Open `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs` and replace the `EmitCreate` and `FormatColumn` methods:

```csharp
    private static string EmitCreate(Table table)
    {
        StringBuilder sb = new();
        sb.Append("CREATE TABLE [").Append(table.Schema).Append("].[").Append(table.Name).AppendLine("] (");

        // Columns with a named DefaultConstraint must NOT also carry inline `DEFAULT (...)`;
        // the constraint is the source of truth for naming + emission ordering.
        HashSet<string> colsWithNamedDefault =
            [.. table.Constraints.OfType<DefaultConstraint>().Select(d => d.ColumnName)];

        bool firstLine = true;
        for (int i = 0; i < table.Columns.Count; i++)
        {
            Column col = table.Columns[i];
            AppendLineSeparator(sb, ref firstLine);
            sb.Append("    ").Append(FormatColumn(col, colsWithNamedDefault.Contains(col.Name)));
        }

        foreach (Constraint c in table.Constraints)
        {
            switch (c)
            {
                case PrimaryKey pk:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(pk.Name).Append("] PRIMARY KEY ")
                      .Append(pk.IsClustered ? "CLUSTERED " : "NONCLUSTERED ")
                      .Append('(').Append(string.Join(", ", pk.Columns.Select(Bracket))).Append(')');
                    break;
                case UniqueConstraint uq:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(uq.Name).Append("] UNIQUE ")
                      .Append(uq.IsClustered ? "CLUSTERED " : "NONCLUSTERED ")
                      .Append('(').Append(string.Join(", ", uq.Columns.Select(Bracket))).Append(')');
                    break;
                case CheckConstraint:
                case DefaultConstraint:
                case ForeignKey:
                    // CK + DF land in T2.14; FK is emitted standalone in T2.18
                    break;
            }
        }

        sb.AppendLine();
        sb.AppendLine(");");
        return sb.ToString();
    }

    private static void AppendLineSeparator(StringBuilder sb, ref bool firstLine)
    {
        if (firstLine)
        {
            firstLine = false;
            return;
        }
        sb.AppendLine(",");
    }

    private static string Bracket(string identifier) => $"[{identifier}]";

    private static string FormatColumn(Column c, bool hasNamedDefault)
    {
        StringBuilder sb = new();
        sb.Append('[').Append(c.Name).Append("] ");

        if (c.ComputedExpression is not null)
        {
            sb.Append("AS ").Append(c.ComputedExpression);
            if (c.IsPersistedComputed)
            {
                sb.Append(" PERSISTED");
                if (!c.IsNullable)
                {
                    sb.Append(" NOT NULL");
                }
            }
            return sb.ToString();
        }

        sb.Append(c.DataType);
        if (c.IsIdentity)
        {
            long seed = c.IdentitySeed ?? 1;
            long inc = c.IdentityIncrement ?? 1;
            sb.Append(" IDENTITY(").Append(seed).Append(',').Append(inc).Append(')');
        }
        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        if (!hasNamedDefault && !string.IsNullOrEmpty(c.DefaultExpression))
        {
            sb.Append(" DEFAULT ").Append(c.DefaultExpression);
        }
        return sb.ToString();
    }
```

- [ ] **Step 4: Run the test, inspect `*.received.txt`, accept it as the snapshot**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_clustered_PK_and_UQ"
```

Open `tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_clustered_PK_and_UQ.received.txt`. Confirm it contains:

```
CREATE TABLE [dbo].[Customer] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [Email] nvarchar(200) NOT NULL,
    [Name] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Customer_Email] UNIQUE NONCLUSTERED ([Email])
);
```

Promote the snapshot:

```bash
mv tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_clustered_PK_and_UQ.received.txt tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_clustered_PK_and_UQ.verified.txt
```

Re-run the test:

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_clustered_PK_and_UQ"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.cs tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_clustered_PK_and_UQ.verified.txt
git commit -m "feat(core/scriptgen): inline PK + UQ + identity(seed,increment) in CREATE TABLE"
```

---

### Task T2.14: Emit `CHECK` + `DEFAULT` inline in `CREATE TABLE`

**Files:**
- Modify: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`
- Modify: `tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.cs`

- [ ] **Step 1: Append failing test**

```csharp
    [Fact]
    public Task Create_table_with_check_and_default_constraints()
    {
        Table t = new(
            Schema: "dbo",
            Name: "Person",
            Columns:
            [
                new Column("Id", "int", false, 1, IsIdentity: true, IdentitySeed: 1, IdentityIncrement: 1),
                new Column("Age", "int", false, 2),
                new Column("CreatedAt", "datetime2(7)", false, 3, DefaultExpression: "(sysutcdatetime())"),
            ],
            Constraints:
            [
                new PrimaryKey("PK_Person", ["Id"], IsClustered: true),
                new CheckConstraint("CK_Person_Age", "([Age] >= 0)", IsDisabled: false, IsNotForReplication: false),
                new DefaultConstraint("DF_Person_CreatedAt", "CreatedAt", "(sysutcdatetime())"),
            ],
            Indexes: []);
        DifferencePair pair = new(t.Identity, DifferenceStatus.OnlyInA, t, null);

        string ddl = new TableScriptEmitter().Emit(pair);
        return Verifier.Verify(ddl);
    }
```

- [ ] **Step 2: Run test (will fail — no CK/DF emission yet, also no snapshot)**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_check_and_default_constraints"
```

- [ ] **Step 3: Extend `EmitCreate` switch with CK + DF arms**

Replace the `CheckConstraint:` / `DefaultConstraint:` arms inside the constraint loop of `TableScriptEmitter.EmitCreate` with:

```csharp
                case CheckConstraint ck:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(ck.Name).Append("] CHECK ")
                      .Append(ck.Expression);
                    break;
                case DefaultConstraint df:
                    AppendLineSeparator(sb, ref firstLine);
                    sb.Append("    CONSTRAINT [").Append(df.Name).Append("] DEFAULT ")
                      .Append(df.Expression).Append(" FOR [").Append(df.ColumnName).Append(']');
                    break;
```

Note: duplicate `DEFAULT` emission is already prevented because `FormatColumn` was given the `hasNamedDefault` flag in T2.13 — when a `DefaultConstraint` is present for a column, the inline `DEFAULT` is suppressed and only the named-constraint form lands in the script.

- [ ] **Step 4: Run test, inspect `.received.txt`, promote**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_check_and_default_constraints"
mv tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_check_and_default_constraints.received.txt tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_check_and_default_constraints.verified.txt
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_check_and_default_constraints"
```

Expected: PASS after promotion.

- [ ] **Step 5: Re-run the full golden suite**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests
```

Expected: every M1 + M2 golden snapshot stays green. M1 columns built from `new Column(... DefaultExpression: "(0)")` continue to emit inline `DEFAULT` because no `DefaultConstraint` accompanies them — the `hasNamedDefault` flag from T2.13 keeps M1 behaviour intact.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs tests/DbDelta.ScriptGen.GoldenTests
git commit -m "feat(core/scriptgen): inline CHECK + DEFAULT constraints; default emission via constraint, not column"
```

---

### Task T2.15: Emit computed columns inline

**Files:**
- Modify: `tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.cs`

The emitter logic for computed columns is already in place from Task T2.13 (`FormatColumn`'s `ComputedExpression` branch). This task only adds a golden test to lock that contract in.

- [ ] **Step 1: Append failing test**

```csharp
    [Fact]
    public Task Create_table_with_persisted_computed_column()
    {
        Table t = new(
            Schema: "dbo",
            Name: "Person",
            Columns:
            [
                new Column("Id", "int", false, 1, IsIdentity: true, IdentitySeed: 1, IdentityIncrement: 1),
                new Column("FirstName", "nvarchar(50)", false, 2),
                new Column("LastName",  "nvarchar(50)", false, 3),
                new Column(
                    Name: "FullName",
                    DataType: "nvarchar(101)",
                    IsNullable: false,
                    Ordinal: 4,
                    ComputedExpression: "([FirstName]+N' '+[LastName])",
                    IsPersistedComputed: true),
            ],
            Constraints: [new PrimaryKey("PK_Person", ["Id"], true)],
            Indexes: []);
        DifferencePair pair = new(t.Identity, DifferenceStatus.OnlyInA, t, null);

        string ddl = new TableScriptEmitter().Emit(pair);
        return Verifier.Verify(ddl);
    }
```

- [ ] **Step 2: Run test, promote, re-run**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_persisted_computed_column"
mv tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_persisted_computed_column.received.txt tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Create_table_with_persisted_computed_column.verified.txt
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Create_table_with_persisted_computed_column"
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.ScriptGen.GoldenTests
git commit -m "test(scriptgen): golden for computed column with PERSISTED + NOT NULL"
```

---

### Task T2.16: Emit `ALTER TABLE ADD CONSTRAINT` for newly added constraints

**Files:**
- Modify: `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs`
- Modify: `tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.cs`

In M2 we cover the most common scenario: a constraint added on an existing table whose other columns are unchanged. Full ALTER coverage (drop + alter type) ships in M8.

- [ ] **Step 1: Append failing test**

```csharp
    [Fact]
    public Task Alter_table_add_new_PK_and_UQ_constraints()
    {
        Column[] cols =
        [
            new("Id",    "int",           false, 1),
            new("Email", "nvarchar(200)", false, 2),
        ];

        Table newT = new(
            Schema: "dbo",
            Name: "Customer",
            Columns: cols,
            Constraints:
            [
                new PrimaryKey("PK_Customer", ["Id"], true),
                new UniqueConstraint("UQ_Customer_Email", ["Email"], false),
            ],
            Indexes: []);

        Table oldT = new("dbo", "Customer", cols, [], []);

        DifferencePair pair = new(newT.Identity, DifferenceStatus.Different, newT, oldT);
        string ddl = new TableScriptEmitter().Emit(pair);
        return Verifier.Verify(ddl);
    }
```

- [ ] **Step 2: Run test (FAIL — emitter does not yet emit ADD CONSTRAINT)**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Alter_table_add_new_PK_and_UQ_constraints"
```

- [ ] **Step 3: Extend `EmitAlter`**

Replace the `EmitAlter` method body in `TableScriptEmitter.cs` with:

```csharp
    private static string EmitAlter(Table newT, Table oldT)
    {
        StringBuilder sb = new();

        HashSet<string> colsWithNamedDefault =
            [.. newT.Constraints.OfType<DefaultConstraint>().Select(d => d.ColumnName)];

        // 1. New columns
        Dictionary<string, Column> existingColsByName =
            oldT.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (Column newCol in newT.Columns)
        {
            if (!existingColsByName.ContainsKey(newCol.Name))
            {
                sb.Append("ALTER TABLE [").Append(newT.Schema).Append("].[").Append(newT.Name)
                  .Append("] ADD ")
                  .Append(FormatColumn(newCol, colsWithNamedDefault.Contains(newCol.Name)))
                  .AppendLine(";");
            }
        }

        // 2. New constraints (by name)
        HashSet<string> existingConstraintNames =
            [.. oldT.Constraints.Select(c => c.Name)];
        foreach (Constraint c in newT.Constraints)
        {
            if (existingConstraintNames.Contains(c.Name))
            {
                continue;
            }
            string body = FormatStandaloneConstraintBody(c);
            if (body.Length == 0)
            {
                continue; // FK / other kinds handled by ForeignKeyScriptEmitter (T2.18)
            }
            sb.Append("ALTER TABLE [").Append(newT.Schema).Append("].[").Append(newT.Name)
              .Append("] ADD CONSTRAINT [").Append(c.Name).Append("] ")
              .Append(body).AppendLine(";");
        }

        return sb.ToString();
    }

    private static string FormatStandaloneConstraintBody(Constraint c) => c switch
    {
        PrimaryKey pk => $"PRIMARY KEY {(pk.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({string.Join(", ", pk.Columns.Select(Bracket))})",
        UniqueConstraint uq => $"UNIQUE {(uq.IsClustered ? "CLUSTERED" : "NONCLUSTERED")} ({string.Join(", ", uq.Columns.Select(Bracket))})",
        CheckConstraint ck => $"CHECK {ck.Expression}",
        DefaultConstraint df => $"DEFAULT {df.Expression} FOR [{df.ColumnName}]",
        ForeignKey => string.Empty, // see T2.18
        _ => string.Empty,
    };
```

- [ ] **Step 4: Run test, promote, re-run**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Alter_table_add_new_PK_and_UQ_constraints"
mv tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Alter_table_add_new_PK_and_UQ_constraints.received.txt tests/DbDelta.ScriptGen.GoldenTests/TableWithConstraintsGoldenTests.Alter_table_add_new_PK_and_UQ_constraints.verified.txt
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~Alter_table_add_new_PK_and_UQ_constraints"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs tests/DbDelta.ScriptGen.GoldenTests
git commit -m "feat(core/scriptgen): emit ALTER TABLE ADD CONSTRAINT for new PK/UQ/CK/DEFAULT on existing tables"
```

---

### Task T2.17: `IndexScriptEmitter` (standalone CREATE INDEX / DROP INDEX)

**Files:**
- Create: `src/DbDelta.Core/ScriptGen/IndexScriptEmitter.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/IndexGoldenTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

public class IndexGoldenTests
{
    [Fact]
    public Task Create_nonclustered_index_with_included_columns_and_filter()
    {
        Index ix = new(
            Name: "IX_Order_Active",
            IsUnique: false,
            IsClustered: false,
            FilterExpression: "([IsDeleted]=(0))",
            KeyColumns:
            [
                new IndexColumn("CustomerId", false),
                new IndexColumn("OrderDate",  true),
            ],
            IncludedColumns: ["TotalAmount"]);

        string ddl = new IndexScriptEmitter().EmitCreate("dbo", "Order", ix);
        return Verifier.Verify(ddl);
    }

    [Fact]
    public Task Drop_index_emits_drop_statement()
    {
        Index ix = new("IX_Foo", false, false, null,
            [new IndexColumn("Bar", false)], []);

        string ddl = new IndexScriptEmitter().EmitDrop("dbo", "Order", ix);
        return Verifier.Verify(ddl);
    }
}
```

- [ ] **Step 2: Run tests (FAIL — type missing)**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~IndexGoldenTests"
```

- [ ] **Step 3: Write `IndexScriptEmitter.cs`**

```csharp
using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits standalone CREATE INDEX / DROP INDEX statements. Called by
/// <see cref="ScriptGenerator"/> after all tables have been created.
/// </summary>
public sealed class IndexScriptEmitter
{
    public string EmitCreate(string schema, string table, Index ix)
    {
        ArgumentNullException.ThrowIfNull(ix);
        StringBuilder sb = new();
        sb.Append("CREATE ");
        if (ix.IsUnique)
        {
            sb.Append("UNIQUE ");
        }
        sb.Append(ix.IsClustered ? "CLUSTERED " : "NONCLUSTERED ");
        sb.Append("INDEX [").Append(ix.Name).Append("] ON [")
          .Append(schema).Append("].[").Append(table).Append("] (");
        sb.Append(string.Join(", ", ix.KeyColumns.Select(k =>
            $"[{k.Name}] {(k.IsDescending ? "DESC" : "ASC")}")));
        sb.Append(')');

        if (ix.IncludedColumns.Count > 0)
        {
            sb.Append(" INCLUDE (");
            sb.Append(string.Join(", ", ix.IncludedColumns.Select(c => $"[{c}]")));
            sb.Append(')');
        }

        if (!string.IsNullOrEmpty(ix.FilterExpression))
        {
            sb.Append(" WHERE ").Append(ix.FilterExpression);
        }

        sb.Append(';');
        return sb.ToString();
    }

    public string EmitDrop(string schema, string table, Index ix)
    {
        ArgumentNullException.ThrowIfNull(ix);
        return $"DROP INDEX [{ix.Name}] ON [{schema}].[{table}];";
    }
}
```

- [ ] **Step 4: Run + promote + re-run**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~IndexGoldenTests"
mv tests/DbDelta.ScriptGen.GoldenTests/IndexGoldenTests.Create_nonclustered_index_with_included_columns_and_filter.received.txt tests/DbDelta.ScriptGen.GoldenTests/IndexGoldenTests.Create_nonclustered_index_with_included_columns_and_filter.verified.txt
mv tests/DbDelta.ScriptGen.GoldenTests/IndexGoldenTests.Drop_index_emits_drop_statement.received.txt tests/DbDelta.ScriptGen.GoldenTests/IndexGoldenTests.Drop_index_emits_drop_statement.verified.txt
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~IndexGoldenTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/IndexScriptEmitter.cs tests/DbDelta.ScriptGen.GoldenTests
git commit -m "feat(core/scriptgen): IndexScriptEmitter for CREATE/DROP INDEX (incl. INCLUDE + filter)"
```

---

### Task T2.18: `ForeignKeyScriptEmitter` + orchestrate in `ScriptGenerator`

**Files:**
- Create: `src/DbDelta.Core/ScriptGen/ForeignKeyScriptEmitter.cs`
- Modify: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/ForeignKeyGoldenTests.cs`

- [ ] **Step 1: Write the failing FK golden tests**

```csharp
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

public class ForeignKeyGoldenTests
{
    [Fact]
    public Task Foreign_key_with_cascade_delete()
    {
        ForeignKey fk = new(
            Name: "FK_Order_Customer",
            Columns: ["CustomerId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Customer",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.Cascade,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);

        string ddl = new ForeignKeyScriptEmitter().EmitAdd("dbo", "Order", fk);
        return Verifier.Verify(ddl);
    }

    [Fact]
    public Task Foreign_key_disabled_and_not_for_replication()
    {
        ForeignKey fk = new(
            Name: "FK_Order_Customer",
            Columns: ["CustomerId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Customer",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.SetNull,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: true,
            IsNotForReplication: true);

        string ddl = new ForeignKeyScriptEmitter().EmitAdd("dbo", "Order", fk);
        return Verifier.Verify(ddl);
    }
}
```

- [ ] **Step 2: Run tests (FAIL — type missing)**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~ForeignKeyGoldenTests"
```

- [ ] **Step 3: Write `ForeignKeyScriptEmitter.cs`**

```csharp
using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits ALTER TABLE ADD CONSTRAINT … FOREIGN KEY statements, including
/// cascading options + NOCHECK / NOT FOR REPLICATION flags.
/// </summary>
public sealed class ForeignKeyScriptEmitter
{
    public string EmitAdd(string schema, string table, ForeignKey fk)
    {
        ArgumentNullException.ThrowIfNull(fk);
        StringBuilder sb = new();
        sb.Append("ALTER TABLE [").Append(schema).Append("].[").Append(table).Append("] ");
        if (fk.IsDisabled)
        {
            sb.Append("WITH NOCHECK ");
        }
        sb.Append("ADD CONSTRAINT [").Append(fk.Name).Append("] FOREIGN KEY (")
          .Append(string.Join(", ", fk.Columns.Select(c => $"[{c}]")))
          .Append(") REFERENCES [").Append(fk.ReferencedSchema).Append("].[")
          .Append(fk.ReferencedTable).Append("] (")
          .Append(string.Join(", ", fk.ReferencedColumns.Select(c => $"[{c}]")))
          .Append(')');

        if (fk.OnDelete != ReferentialAction.NoAction)
        {
            sb.Append(" ON DELETE ").Append(FormatAction(fk.OnDelete));
        }
        if (fk.OnUpdate != ReferentialAction.NoAction)
        {
            sb.Append(" ON UPDATE ").Append(FormatAction(fk.OnUpdate));
        }
        if (fk.IsNotForReplication)
        {
            sb.Append(" NOT FOR REPLICATION");
        }
        sb.Append(';');

        if (fk.IsDisabled)
        {
            sb.Append('\n')
              .Append("ALTER TABLE [").Append(schema).Append("].[").Append(table).Append("] ")
              .Append("NOCHECK CONSTRAINT [").Append(fk.Name).Append("];");
        }

        return sb.ToString();
    }

    private static string FormatAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };
}
```

- [ ] **Step 4: Orchestrate emission order in `ScriptGenerator`**

Replace `ScriptGenerator.Generate` with:

```csharp
    private readonly TableScriptEmitter _tableEmitter = new();
    private readonly IndexScriptEmitter _indexEmitter = new();
    private readonly ForeignKeyScriptEmitter _fkEmitter = new();

    public string Generate(ComparisonResult result, IEnumerable<DifferencePair>? selection = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        IEnumerable<DifferencePair> pairs = (selection ?? result.Differences)
            .Where(p => p.Status != DifferenceStatus.Identical)
            .ToList();

        StringBuilder sb = new();
        sb.AppendLine("-- Generated by DbDelta");
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRANSACTION;");
        sb.AppendLine("GO");

        // 1. Tables (CREATE / DROP / ALTER ADD COLUMN + add-constraint)
        foreach (DifferencePair pair in pairs)
        {
            string tableDdl = _tableEmitter.Emit(pair);
            if (!string.IsNullOrWhiteSpace(tableDdl))
            {
                sb.AppendLine(tableDdl);
                sb.AppendLine("GO");
            }
        }

        // 2. Indexes — only for tables being created (OnlyInA) or changed.
        //    A full add/drop index diff lands in M8; M2 emits indexes for new tables only.
        foreach (DifferencePair pair in pairs)
        {
            if (pair.Status != DifferenceStatus.OnlyInA || pair.SideA is not Table t)
            {
                continue;
            }
            foreach (Index ix in t.Indexes)
            {
                sb.AppendLine(_indexEmitter.EmitCreate(t.Schema, t.Name, ix));
            }
            if (t.Indexes.Count > 0)
            {
                sb.AppendLine("GO");
            }
        }

        // 3. Foreign keys — emitted last so referenced tables exist.
        foreach (DifferencePair pair in pairs)
        {
            if (pair.Status != DifferenceStatus.OnlyInA || pair.SideA is not Table t)
            {
                continue;
            }
            foreach (ForeignKey fk in t.Constraints.OfType<ForeignKey>())
            {
                sb.AppendLine(_fkEmitter.EmitAdd(t.Schema, t.Name, fk));
            }
            if (t.Constraints.OfType<ForeignKey>().Any())
            {
                sb.AppendLine("GO");
            }
        }

        sb.AppendLine("COMMIT TRANSACTION;");
        sb.AppendLine("GO");
        return sb.ToString();
    }
```

Also add the necessary `using DbDelta.Core.ObjectModel;` at the top of `ScriptGenerator.cs` if not already present.

- [ ] **Step 5: Promote FK goldens**

```bash
dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~ForeignKeyGoldenTests"
mv tests/DbDelta.ScriptGen.GoldenTests/ForeignKeyGoldenTests.Foreign_key_with_cascade_delete.received.txt tests/DbDelta.ScriptGen.GoldenTests/ForeignKeyGoldenTests.Foreign_key_with_cascade_delete.verified.txt
mv tests/DbDelta.ScriptGen.GoldenTests/ForeignKeyGoldenTests.Foreign_key_disabled_and_not_for_replication.received.txt tests/DbDelta.ScriptGen.GoldenTests/ForeignKeyGoldenTests.Foreign_key_disabled_and_not_for_replication.verified.txt
dotnet test tests/DbDelta.ScriptGen.GoldenTests
```

Expected: full golden suite green.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/ForeignKeyScriptEmitter.cs src/DbDelta.Core/ScriptGen/ScriptGenerator.cs tests/DbDelta.ScriptGen.GoldenTests
git commit -m "feat(core/scriptgen): ForeignKeyScriptEmitter + ScriptGenerator orders tables → indexes → FKs"
```

---

## Phase H — Provider integration tests

### Task T2.19: Extend `TableReaderTests` for identity seed/increment + computed

**Files:**
- Modify: `tests/DbDelta.Providers.LiveDb.IntegrationTests/TableReaderTests.cs`

- [ ] **Step 1: Append a new fact to the existing test class**

```csharp
    [Fact]
    public async Task LiveDbSource_loads_identity_seed_increment_and_computed_columns()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaTestM2') IS NULL CREATE DATABASE DbDeltaTestM2;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaTestM2"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Person') IS NULL
                    CREATE TABLE dbo.Person (
                        Id        int IDENTITY(1000, 5) NOT NULL,
                        FirstName nvarchar(50)          NOT NULL,
                        LastName  nvarchar(50)          NOT NULL,
                        FullName  AS ([FirstName] + N' ' + [LastName]) PERSISTED
                    );
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table person = result.Value!.Tables.Single(t => t.Name == "Person");
        Column id = person.Columns.Single(c => c.Name == "Id");
        id.IsIdentity.Should().BeTrue();
        id.IdentitySeed.Should().Be(1000);
        id.IdentityIncrement.Should().Be(5);

        Column fullName = person.Columns.Single(c => c.Name == "FullName");
        fullName.ComputedExpression.Should().NotBeNull();
        fullName.IsPersistedComputed.Should().BeTrue();
    }
```

- [ ] **Step 2: Run the new test**

```bash
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter "FullyQualifiedName~LiveDbSource_loads_identity_seed_increment_and_computed_columns"
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Providers.LiveDb.IntegrationTests/TableReaderTests.cs
git commit -m "test(providers/livedb): integration test asserts identity seed/increment + computed expression"
```

---

### Task T2.20: `ConstraintReaderTests` — PK / UQ / CHECK / DEFAULT integration

**Files:**
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/ConstraintReaderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class ConstraintReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_PK_UQ_CHECK_DEFAULT_for_a_table()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaCons') IS NULL CREATE DATABASE DbDeltaCons;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaCons"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Customer') IS NULL
                BEGIN
                    CREATE TABLE dbo.Customer (
                        Id        int           NOT NULL,
                        Email     nvarchar(200) NOT NULL,
                        Age       int           NOT NULL,
                        CreatedAt datetime2(7)  NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT (sysutcdatetime()),
                        CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (Id),
                        CONSTRAINT UQ_Customer_Email UNIQUE NONCLUSTERED (Email),
                        CONSTRAINT CK_Customer_Age CHECK ([Age] >= 0)
                    );
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table customer = result.Value!.Tables.Single(t => t.Name == "Customer");
        customer.Constraints.Should().HaveCount(4);

        PrimaryKey pk = customer.Constraints.OfType<PrimaryKey>().Single();
        pk.Name.Should().Be("PK_Customer");
        pk.Columns.Should().Equal("Id");
        pk.IsClustered.Should().BeTrue();

        UniqueConstraint uq = customer.Constraints.OfType<UniqueConstraint>().Single();
        uq.Name.Should().Be("UQ_Customer_Email");
        uq.Columns.Should().Equal("Email");

        CheckConstraint ck = customer.Constraints.OfType<CheckConstraint>().Single();
        ck.Name.Should().Be("CK_Customer_Age");
        ck.Expression.Should().Contain("Age");

        DefaultConstraint df = customer.Constraints.OfType<DefaultConstraint>().Single();
        df.Name.Should().Be("DF_Customer_CreatedAt");
        df.ColumnName.Should().Be("CreatedAt");
        df.Expression.Should().Contain("sysutcdatetime");
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 2: Run**

```bash
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter "FullyQualifiedName~ConstraintReaderTests.LiveDbSource_loads_PK_UQ_CHECK_DEFAULT_for_a_table"
```

Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Providers.LiveDb.IntegrationTests/ConstraintReaderTests.cs
git commit -m "test(providers/livedb): integration test for PK + UQ + CHECK + DEFAULT readers"
```

---

### Task T2.21: `ConstraintReaderTests` — FK with cascade integration

**Files:**
- Modify: `tests/DbDelta.Providers.LiveDb.IntegrationTests/ConstraintReaderTests.cs`

- [ ] **Step 1: Append a new fact**

```csharp
    [Fact]
    public async Task LiveDbSource_loads_FK_with_cascade_delete()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaFk') IS NULL CREATE DATABASE DbDeltaFk;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaFk"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.OrderItem') IS NULL
                BEGIN
                    CREATE TABLE dbo.Customer (
                        Id int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY
                    );
                    CREATE TABLE dbo.OrderItem (
                        Id int NOT NULL CONSTRAINT PK_OrderItem PRIMARY KEY,
                        CustomerId int NOT NULL,
                        CONSTRAINT FK_OrderItem_Customer FOREIGN KEY (CustomerId)
                            REFERENCES dbo.Customer (Id) ON DELETE CASCADE
                    );
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table orderItem = result.Value!.Tables.Single(t => t.Name == "OrderItem");
        ForeignKey fk = orderItem.Constraints.OfType<ForeignKey>().Single();

        fk.Name.Should().Be("FK_OrderItem_Customer");
        fk.Columns.Should().Equal("CustomerId");
        fk.ReferencedSchema.Should().Be("dbo");
        fk.ReferencedTable.Should().Be("Customer");
        fk.ReferencedColumns.Should().Equal("Id");
        fk.OnDelete.Should().Be(ReferentialAction.Cascade);
        fk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }
```

- [ ] **Step 2: Run + commit**

```bash
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter "FullyQualifiedName~LiveDbSource_loads_FK_with_cascade_delete"
git add tests/DbDelta.Providers.LiveDb.IntegrationTests/ConstraintReaderTests.cs
git commit -m "test(providers/livedb): integration test for FK with ON DELETE CASCADE"
```

Expected: PASS, then commit.

---

### Task T2.22: `IndexReaderTests` — non-clustered + unique + included + filtered

**Files:**
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/IndexReaderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class IndexReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_nonclustered_unique_included_and_filtered_indexes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaIx') IS NULL CREATE DATABASE DbDeltaIx;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaIx"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Doc') IS NULL
                BEGIN
                    CREATE TABLE dbo.Doc (
                        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Doc PRIMARY KEY,
                        Title nvarchar(200) NOT NULL,
                        Author nvarchar(100) NOT NULL,
                        IsDeleted bit NOT NULL CONSTRAINT DF_Doc_IsDeleted DEFAULT (0),
                        Tags nvarchar(200) NULL
                    );
                    CREATE NONCLUSTERED INDEX IX_Doc_Title ON dbo.Doc (Title ASC) INCLUDE (Author);
                    CREATE UNIQUE NONCLUSTERED INDEX UX_Doc_Author ON dbo.Doc (Author DESC);
                    CREATE NONCLUSTERED INDEX IX_Doc_Active ON dbo.Doc (Id) WHERE IsDeleted = 0;
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table doc = result.Value!.Tables.Single(t => t.Name == "Doc");
        doc.Indexes.Should().HaveCount(3);

        Index ixTitle = doc.Indexes.Single(i => i.Name == "IX_Doc_Title");
        ixTitle.IsUnique.Should().BeFalse();
        ixTitle.IsClustered.Should().BeFalse();
        ixTitle.KeyColumns.Should().HaveCount(1);
        ixTitle.KeyColumns[0].Name.Should().Be("Title");
        ixTitle.KeyColumns[0].IsDescending.Should().BeFalse();
        ixTitle.IncludedColumns.Should().Equal("Author");

        Index uxAuthor = doc.Indexes.Single(i => i.Name == "UX_Doc_Author");
        uxAuthor.IsUnique.Should().BeTrue();
        uxAuthor.KeyColumns[0].IsDescending.Should().BeTrue();

        Index ixActive = doc.Indexes.Single(i => i.Name == "IX_Doc_Active");
        ixActive.FilterExpression.Should().Contain("IsDeleted");
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 2: Run + commit**

```bash
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter "FullyQualifiedName~IndexReaderTests"
git add tests/DbDelta.Providers.LiveDb.IntegrationTests/IndexReaderTests.cs
git commit -m "test(providers/livedb): integration tests for IndexReader (unique, included, filtered)"
```

Expected: PASS, then commit.

---

## Phase I — CLI acceptance + global hygiene

### Task T2.23: Acceptance test — `compare` flags a PK-only difference

**Files:**
- Modify: `tests/DbDelta.Cli.AcceptanceTests/CompareCommandTests.cs`

- [ ] **Step 1: Append a fact**

```csharp
    [Fact]
    public async Task Returns_exit_code_1_when_target_is_missing_a_primary_key()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaPkSrc";
        const string tgtDb = "DbDeltaPkTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);

        await CreateCustomerWithPk(srcDb, ct);
        await CreateCustomerWithoutPk(tgtDb, ct);

        string srcConn = ConnectionFor(srcDb);
        string tgtConn = ConnectionFor(tgtDb);

        int exit = await RunCli(["compare", "--source", srcConn, "--target", tgtConn, "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    private async Task CreateCustomerWithPk(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer (
                    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
                    Name nvarchar(100) NOT NULL
                );
            """, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateCustomerWithoutPk(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer (
                    Id int IDENTITY(1,1) NOT NULL,
                    Name nvarchar(100) NOT NULL
                );
            """, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
```

- [ ] **Step 2: Run + commit**

```bash
dotnet build src/DbDelta.Cli/DbDelta.Cli.csproj
dotnet test tests/DbDelta.Cli.AcceptanceTests --filter "FullyQualifiedName~Returns_exit_code_1_when_target_is_missing_a_primary_key"
git add tests/DbDelta.Cli.AcceptanceTests/CompareCommandTests.cs
git commit -m "test(cli): acceptance — PK present in source but missing on target is flagged as difference"
```

Expected: PASS, then commit.

---

### Task T2.24: Final format + full-suite verify + push

**Files:** none

- [ ] **Step 1: Run formatter end-to-end**

```bash
dotnet format
dotnet format --verify-no-changes
```

Expected: exit code 0 from the verify step. If any files changed, stage and commit:

```bash
git add -u
git commit -m "chore: dotnet format normalization after M2"
```

- [ ] **Step 2: Run the full build + test sweep**

```bash
dotnet build DbDelta.sln
dotnet test DbDelta.sln --no-build
```

Expected:
- Build: 0 errors, 0 warnings.
- All 7 test projects (M1's 6 + the new integration class) report green. Every assertion added by M2 passes. Every M1 assertion still passes.

- [ ] **Step 3: Push**

```bash
git push origin main
```

- [ ] **Step 4: Confirm CI is green on the latest commit**

Open the repo `Actions` tab. Confirm the most recent `ci / build-and-test` run on `main` is ✅. If red, address the failures with fix-up commits before declaring M2 complete.

---

## Acceptance Criteria for M2

All of the following must be true:

- [ ] `dotnet build DbDelta.sln` — 0 errors, 0 warnings.
- [ ] `dotnet format --verify-no-changes` — exit 0.
- [ ] `dotnet test DbDelta.sln` — every test passes, including the new M2 suites.
- [ ] `DbDelta.Architecture.Tests.LayeringTests` still green (Core has no I/O).
- [ ] A live SQL Server database whose tables carry PK / UQ / CK / DEFAULT / FK / Index / IDENTITY(seed,inc) / PERSISTED computed columns is loaded by `LiveDbSource` with all those shapes captured in the in-memory `Table` graph.
- [ ] `ComparisonEngine` reports `Different` for every kind of constraint or index change, and `Identical` when both sides match.
- [ ] `ScriptGenerator` produces a deployable T-SQL batch that creates the new tables (with PK/UQ/CK/DEFAULT/identity/computed inline), then `CREATE INDEX` for each index, then `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY` for each FK — in that order — wrapped in `SET XACT_ABORT ON; BEGIN TRANSACTION; … COMMIT TRANSACTION; GO`.
- [ ] CLI `compare` correctly returns exit code 1 when a PK / FK / UQ / CK / DEFAULT / Index is present on one side and missing on the other.
- [ ] The Verify snapshot suite under `tests/DbDelta.ScriptGen.GoldenTests/` covers at least: PK+UQ inline, CK+DEFAULT inline, computed column, ALTER ADD PK/UQ, non-clustered index with INCLUDE and filter, FK with CASCADE, FK disabled + NOT FOR REPLICATION.
- [ ] CI workflow on `windows-latest` is green on `main` after the M2 push.

If any criterion fails, do not declare M2 complete — open an issue or extend the plan with a follow-up task.

---

## Next Plan Preview

After M2 lands and is verified, the next plan (`docs/superpowers/plans/YYYY-MM-DD-m3-views-and-stored-procedures.md`) will extend the engine to handle:

- Views (definitions, `SCHEMABINDING`, indexes on views)
- Stored procedures (definitions, parameters, `WITH ENCRYPTION`)
- Module body diffing via ScriptDom token comparison (line-level T-SQL diff)
- `CREATE OR ALTER` emission for modules

The dependency resolver introduced informally in M2 (`tables → indexes → FKs`) gets formalized in M7 with a full graph + Kahn topological sort. M2 deliberately stops short of that — every diff scenario in M2 can be deployed without it because indexes attach to existing tables and FKs reference tables that exist by the time they are emitted.
