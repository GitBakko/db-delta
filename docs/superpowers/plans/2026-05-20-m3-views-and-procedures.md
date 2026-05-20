# DbDelta — M3 Views & Stored Procedures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the walking skeleton + M2 object graph with first-class support for **views** and **stored procedures**: read their definitions from live SQL Server (including the encrypted-module case), surface body-level diffs in the comparison result, and emit deployable `CREATE OR ALTER` / `DROP` DDL — wired end-to-end through CLI acceptance tests.

**Architecture:** A new `Module` abstract record in `DbDelta.Core.ObjectModel` becomes the common base for `View` and `StoredProcedure`; both carry the raw T-SQL `Body` plus an `IsEncrypted` flag. `Database` and `Schema` gain `Views` and `Procedures` collections. The `LiveDb` provider grows a single `ModuleReader` that reads `sys.sql_modules` joined with `sys.views` / `sys.procedures` and surfaces encrypted modules with a `null` body and `IsEncrypted = true`. `ComparisonEngine` pairs modules by identity and diffs the body via a normalized text comparison (configurable whitespace + casing rules). `ScriptGenerator` orders the new kinds into the existing emit pipeline: tables → indexes → **views** → **procedures** → FKs, using `CREATE OR ALTER` to keep generated scripts idempotent within the M3 scope (full dependency graph still deferred to M7).

**Tech Stack:** Same as M1/M2 — .NET 10, C# 14, xUnit v3, FluentAssertions, Verify.Xunit, Testcontainers.MsSql 4.x, Microsoft.Data.SqlClient 6.x. No new package versions; M3 is purely Core + Provider + ScriptGen growth.

---

## Reference: Spec Sections This Plan Implements

| Spec section | Plan task(s) |
|--------------|--------------|
| §1.2 Object kinds — View, Stored Procedure | T3.1 – T3.16 |
| §1.2 "line-level T-SQL diff for module bodies" (body comparison) | T3.3 – T3.5 |
| §3.1 Compare flow — `CREATE OR ALTER` for modules | T3.10 – T3.12 |
| §3.4 Encrypted module (`WITH ENCRYPTION`) → opaque + warning | T3.8, T3.13, T3.16 |
| §5.2 Provider integration tests per kind | T3.13 – T3.14 |
| §5.2 Script-gen golden tests per `(kind, change-type)` | T3.10 – T3.12 |
| §6.2 M3 milestone scope | All tasks |

Out of scope for M3 (per roadmap):
- View/procedure dependency resolution (schemabinding cascades, view-of-view) — **M7**
- Side-by-side line-level diff viewer in the GUI — **M10**
- Encrypted-module decryption workarounds — **never** (treated as opaque)

---

## File Structure Map

```
DbDelta/
├─ src/
│  ├─ DbDelta.Core/
│  │  ├─ ObjectModel/
│  │  │  ├─ Module.cs                                  T3.1   (NEW — abstract base record)
│  │  │  ├─ View.cs                                    T3.1   (NEW)
│  │  │  ├─ StoredProcedure.cs                         T3.1   (NEW)
│  │  │  ├─ Schema.cs                                  T3.2   (MODIFY — add Views + Procedures convenience)
│  │  │  └─ Database.cs                                T3.2   (MODIFY — add Views + Procedures collections)
│  │  ├─ Diff/
│  │  │  ├─ BodyNormalizer.cs                          T3.5   (NEW — whitespace + case normalization helper)
│  │  │  └─ ComparisonEngine.cs                        T3.3, T3.4   (MODIFY — pair + diff views and procs)
│  │  └─ ScriptGen/
│  │     ├─ ViewScriptEmitter.cs                       T3.10  (NEW)
│  │     ├─ ProcedureScriptEmitter.cs                  T3.11  (NEW)
│  │     └─ ScriptGenerator.cs                         T3.12  (MODIFY — orchestrate tables → indexes → views → procs → FKs)
│  ├─ DbDelta.Providers.LiveDb/
│  │  ├─ Readers/
│  │  │  └─ ModuleReader.cs                            T3.6, T3.7, T3.8   (NEW — reads sys.sql_modules + sys.views/procedures)
│  │  └─ LiveDbSource.cs                               T3.9   (MODIFY — compose views + procs into Database)
│  └─ DbDelta.Shared/
│     └─ Dtos/                                         (unchanged — existing DifferenceDto already accepts any ObjectIdentity)
└─ tests/
   ├─ DbDelta.Core.UnitTests/
   │  ├─ ObjectModel/
   │  │  └─ ModuleTests.cs                             T3.1   (NEW)
   │  └─ Diff/
   │     ├─ BodyNormalizerTests.cs                     T3.5   (NEW)
   │     └─ ModuleDiffTests.cs                         T3.3, T3.4   (NEW)
   ├─ DbDelta.ScriptGen.GoldenTests/
   │  ├─ ViewGoldenTests.cs                            T3.10  (NEW)
   │  ├─ ProcedureGoldenTests.cs                       T3.11  (NEW)
   │  └─ snapshots/                                    (Verify-managed)
   ├─ DbDelta.Providers.LiveDb.IntegrationTests/
   │  └─ ModuleReaderTests.cs                          T3.13  (NEW)
   └─ DbDelta.Cli.AcceptanceTests/
      └─ CompareCommandTests.cs                        T3.14  (MODIFY — add view + procedure scenarios)
```

Existing files **not touched** in M3:
- `src/DbDelta.Core/Abstractions/*` — `ISchemaSource` is generic over `Database`, which already has room to grow.
- `src/DbDelta.Core/Options/*` — `ComparisonOptions` flags extended via the body-normalizer choices already in scope (no flag changes needed; defaults are sufficient for M3).
- `src/DbDelta.Cli/Commands/*` — the existing `compare` command writes any `DifferenceDto` through the JSON / text formatter; new kinds flow through automatically.
- `src/DbDelta.App/Components/*` — `ResultsTree` groups differences by `Kind`; the new `View` / `Procedure` kinds just appear as extra groups.

---

## Conventions Used in This Plan

- Every step that adds code includes the full source — no "fill in".
- Every test has the actual assertion code.
- Conventional Commits; the M1 plan established the footer `Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>` and we keep it.
- `dotnet build` after every code change; if `TreatWarningsAsErrors` flags an analyzer rule the engineer fixes it in the same step before committing.
- `dotnet format` runs at the end (Task T3.16) — interim file-level formatting drift is fine, the global verification gate is the last step.
- All integration tests target the Linux MSSQL container via the existing `LiveDbFixture` / `CliFixture` (CI runs them on `ubuntu-latest` per the M2 ci-split fix).

---

## Phase A — Object model

### Task T3.1: Add `Module`, `View`, `StoredProcedure` records + unit tests

**Files:**
- Create: `src/DbDelta.Core/ObjectModel/Module.cs`
- Create: `src/DbDelta.Core/ObjectModel/View.cs`
- Create: `src/DbDelta.Core/ObjectModel/StoredProcedure.cs`
- Create: `tests/DbDelta.Core.UnitTests/ObjectModel/ModuleTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/DbDelta.Core.UnitTests/ObjectModel/ModuleTests.cs
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class ModuleTests
{
    [Fact]
    public void View_carries_identity_with_kind_View()
    {
        View view = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT 1 AS Id;", IsEncrypted: false);
        view.Identity.SchemaName.Should().Be("dbo");
        view.Identity.ObjectName.Should().Be("vCustomer");
        view.Identity.Kind.Should().Be("View");
    }

    [Fact]
    public void StoredProcedure_carries_identity_with_kind_Procedure()
    {
        StoredProcedure proc = new("dbo", "uspGetCustomer", "CREATE PROCEDURE dbo.uspGetCustomer AS RETURN 0;", IsEncrypted: false);
        proc.Identity.SchemaName.Should().Be("dbo");
        proc.Identity.ObjectName.Should().Be("uspGetCustomer");
        proc.Identity.Kind.Should().Be("Procedure");
    }

    [Fact]
    public void Encrypted_module_has_null_body_and_IsEncrypted_true()
    {
        View view = new("dbo", "vSecret", Body: null, IsEncrypted: true);
        view.Body.Should().BeNull();
        view.IsEncrypted.Should().BeTrue();
    }

    [Fact]
    public void Modules_are_records_with_value_equality()
    {
        View a = new("dbo", "vA", "BODY", IsEncrypted: false);
        View b = new("dbo", "vA", "BODY", IsEncrypted: false);
        a.Should().Be(b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter ModuleTests`
Expected: FAIL — types `Module`, `View`, `StoredProcedure` do not exist.

- [ ] **Step 3: Create `Module.cs`**

```csharp
// src/DbDelta.Core/ObjectModel/Module.cs
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Common base for code-bearing objects (views, procedures, functions, triggers).
/// </summary>
/// <param name="Schema">Owning schema (e.g. "dbo").</param>
/// <param name="Name">Object name.</param>
/// <param name="Body">
/// Full T-SQL definition as returned by <c>sys.sql_modules.definition</c>, or <c>null</c>
/// when the module is encrypted (<see cref="IsEncrypted"/> is <c>true</c>).
/// </param>
/// <param name="IsEncrypted">
/// <c>true</c> when the module was created <c>WITH ENCRYPTION</c>. Encrypted modules have
/// an opaque definition: DbDelta surfaces presence/absence diffs and emits a warning but
/// cannot diff bodies.
/// </param>
public abstract record Module(string Schema, string Name, string? Body, bool IsEncrypted)
{
    /// <summary>The discriminator used in <see cref="ObjectIdentity"/> for this module kind.</summary>
    public abstract string Kind { get; }

    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: Kind);
}
```

- [ ] **Step 4: Create `View.cs`**

```csharp
// src/DbDelta.Core/ObjectModel/View.cs
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server view. Body holds the full <c>CREATE VIEW …</c> text as stored in
/// <c>sys.sql_modules.definition</c>.
/// </summary>
public sealed record View(string Schema, string Name, string? Body, bool IsEncrypted)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "View";
}
```

- [ ] **Step 5: Create `StoredProcedure.cs`**

```csharp
// src/DbDelta.Core/ObjectModel/StoredProcedure.cs
namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server stored procedure. Body holds the full <c>CREATE PROCEDURE …</c> text
/// as stored in <c>sys.sql_modules.definition</c>.
/// </summary>
public sealed record StoredProcedure(string Schema, string Name, string? Body, bool IsEncrypted)
    : Module(Schema, Name, Body, IsEncrypted)
{
    public override string Kind => "Procedure";
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet build src/DbDelta.Core && dotnet test tests/DbDelta.Core.UnitTests --filter ModuleTests`
Expected: PASS — all four tests.

- [ ] **Step 7: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Module.cs \
        src/DbDelta.Core/ObjectModel/View.cs \
        src/DbDelta.Core/ObjectModel/StoredProcedure.cs \
        tests/DbDelta.Core.UnitTests/ObjectModel/ModuleTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add Module/View/StoredProcedure records

- Module: abstract base for code-bearing objects, exposes Body + IsEncrypted
- View / StoredProcedure: concrete records with Kind discriminator wired
  into ObjectIdentity for downstream pairing.
- Encrypted modules carry Body = null + IsEncrypted = true so the comparison
  engine can flag them without attempting a body diff.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.2: Extend `Database` with `Views` + `Procedures` collections

**Files:**
- Modify: `src/DbDelta.Core/ObjectModel/Database.cs`

- [ ] **Step 1: Write the failing test (extend ModuleTests)**

Append to `tests/DbDelta.Core.UnitTests/ObjectModel/ModuleTests.cs`:

```csharp
    [Fact]
    public void Database_carries_views_and_procedures_collections()
    {
        Schema dbo = new("dbo");
        View v = new("dbo", "vA", "BODY", IsEncrypted: false);
        StoredProcedure p = new("dbo", "uspA", "BODY", IsEncrypted: false);
        Database db = new("MyDb", Schemas: [dbo], Tables: [], Views: [v], Procedures: [p]);
        db.Views.Should().ContainSingle().Which.Should().Be(v);
        db.Procedures.Should().ContainSingle().Which.Should().Be(p);
    }

    [Fact]
    public void Database_defaults_views_and_procedures_to_empty()
    {
        Schema dbo = new("dbo");
        Database db = new("MyDb", Schemas: [dbo], Tables: []);
        db.Views.Should().BeEmpty();
        db.Procedures.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "Database_carries_views_and_procedures_collections|Database_defaults_views_and_procedures_to_empty"`
Expected: FAIL — `Database` ctor does not accept `Views` / `Procedures`.

- [ ] **Step 3: Extend `Database`**

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

    /// <summary>
    /// Convenience ctor accepting modules. Provided as a named ctor instead of a positional
    /// parameter so existing call sites (Database(name, schemas, tables)) continue to compile.
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
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build src/DbDelta.Core && dotnet test tests/DbDelta.Core.UnitTests`
Expected: PASS — all tests, including the previously written M1/M2 ones.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Database.cs \
        tests/DbDelta.Core.UnitTests/ObjectModel/ModuleTests.cs
git commit -m "$(cat <<'EOF'
feat(core): extend Database with Views + Procedures collections

Adds init-only Views and Procedures collections plus a positional ctor
overload taking both, so providers can populate them without forcing
every existing Database(name, schemas, tables) call site to change.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase B — Diff

### Task T3.5: Add `BodyNormalizer` helper

**Files:**
- Create: `src/DbDelta.Core/Diff/BodyNormalizer.cs`
- Create: `tests/DbDelta.Core.UnitTests/Diff/BodyNormalizerTests.cs`

> **Why first:** the diff engine in T3.3 / T3.4 depends on this helper, so it ships first.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/DbDelta.Core.UnitTests/Diff/BodyNormalizerTests.cs
using DbDelta.Core.Diff;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class BodyNormalizerTests
{
    [Fact]
    public void Null_input_returns_null()
    {
        BodyNormalizer.Normalize(null).Should().BeNull();
    }

    [Fact]
    public void Trims_outer_whitespace()
    {
        BodyNormalizer.Normalize("   SELECT 1   ").Should().Be("SELECT 1");
    }

    [Fact]
    public void Collapses_runs_of_whitespace_to_single_space()
    {
        BodyNormalizer.Normalize("SELECT     1\t\t  ,\n\n2").Should().Be("SELECT 1 , 2");
    }

    [Fact]
    public void Normalizes_CRLF_to_LF_before_collapsing()
    {
        BodyNormalizer.Normalize("a\r\nb\r\nc").Should().Be("a b c");
    }

    [Fact]
    public void Preserves_case_by_default()
    {
        BodyNormalizer.Normalize("Select x From dbo.T").Should().Be("Select x From dbo.T");
    }

    [Fact]
    public void Two_bodies_only_differing_in_whitespace_compare_equal_after_normalize()
    {
        string a = "CREATE VIEW dbo.v AS\r\nSELECT 1 AS Id;";
        string b = "CREATE  VIEW   dbo.v  AS  SELECT 1 AS Id;";
        BodyNormalizer.Normalize(a).Should().Be(BodyNormalizer.Normalize(b));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter BodyNormalizerTests`
Expected: FAIL — `BodyNormalizer` does not exist.

- [ ] **Step 3: Create `BodyNormalizer`**

```csharp
// src/DbDelta.Core/Diff/BodyNormalizer.cs
using System.Text.RegularExpressions;

namespace DbDelta.Core.Diff;

/// <summary>
/// Normalizes T-SQL module bodies for comparison. v1 strategy:
/// 1. Replace CRLF + CR with LF.
/// 2. Collapse any run of whitespace (spaces, tabs, newlines) into a single space.
/// 3. Trim outer whitespace.
/// Case is preserved — case-insensitive diffing is a future option.
/// </summary>
public static partial class BodyNormalizer
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    public static string? Normalize(string? body)
    {
        if (body is null)
        {
            return null;
        }
        string lf = body.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n');
        string collapsed = WhitespaceRun().Replace(lf, " ");
        return collapsed.Trim();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build src/DbDelta.Core && dotnet test tests/DbDelta.Core.UnitTests --filter BodyNormalizerTests`
Expected: PASS — all six tests.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Diff/BodyNormalizer.cs tests/DbDelta.Core.UnitTests/Diff/BodyNormalizerTests.cs
git commit -m "$(cat <<'EOF'
feat(core/diff): BodyNormalizer for whitespace-insensitive module diff

Normalizes line endings then collapses runs of whitespace to a single
space so that gratuitous reformatting of view/procedure bodies does
not register as a difference. Case is preserved (M3 default).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.3: Diff views inside `ComparisonEngine`

**Files:**
- Modify: `src/DbDelta.Core/Diff/ComparisonEngine.cs`
- Create: `tests/DbDelta.Core.UnitTests/Diff/ModuleDiffTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/DbDelta.Core.UnitTests/Diff/ModuleDiffTests.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ModuleDiffTests
{
    private static Database Db(params View[] views) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: views, Procedures: []);

    [Fact]
    public void View_only_in_A_is_OnlyInA()
    {
        Database a = Db(new View("dbo", "vCustomer", "SELECT 1", IsEncrypted: false));
        Database b = Db();
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        DifferencePair pair = result.Differences.Single(p => p.Identity.Kind == "View");
        pair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void View_only_in_B_is_OnlyInB()
    {
        Database a = Db();
        Database b = Db(new View("dbo", "vCustomer", "SELECT 1", IsEncrypted: false));
        DifferencePair pair = new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View");
        pair.Status.Should().Be(DifferenceStatus.OnlyInB);
    }

    [Fact]
    public void Identical_view_bodies_are_Identical()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences
            .Where(p => p.Identity.Kind == "View")
            .Should().OnlyContain(p => p.Status == DifferenceStatus.Identical);
    }

    [Fact]
    public void View_body_differing_only_in_whitespace_is_Identical()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1\r\nAS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT  1 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void View_body_differing_substantively_is_Different()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT 2 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Encrypted_view_compared_to_encrypted_view_is_Different_because_bodies_are_opaque()
    {
        // We cannot prove equality of opaque bodies; the safe default is Different.
        Database a = Db(new View("dbo", "v", Body: null, IsEncrypted: true));
        Database b = Db(new View("dbo", "v", Body: null, IsEncrypted: true));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter ModuleDiffTests`
Expected: FAIL — the engine ignores `Views` today.

- [ ] **Step 3: Extend `ComparisonEngine.Compare` to pair + classify views**

In `src/DbDelta.Core/Diff/ComparisonEngine.cs`, after the existing table-pairing loop, add a sibling block that pairs views by identity and classifies via `ClassifyModule`. Add the helpers below the existing helpers.

Full edited file (replace contents):

```csharp
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;

namespace DbDelta.Core.Diff;

/// <summary>
/// Pure comparison engine: pair tables + modules by identity, then within each pair
/// compare per options. Pure → no I/O.
/// </summary>
public sealed class ComparisonEngine
{
    public ComparisonResult Compare(Database a, Database b, ComparisonOptions options)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        List<DifferencePair> pairs = [];
        pairs.AddRange(CompareTables(a, b, options));
        pairs.AddRange(CompareModules(a.Views, b.Views));
        pairs.AddRange(CompareModules(a.Procedures, b.Procedures));

        return new ComparisonResult(pairs);
    }

    private static IEnumerable<DifferencePair> CompareTables(Database a, Database b, ComparisonOptions options)
    {
        var aByIdentity = a.Tables.ToDictionary(t => t.Identity);
        var bByIdentity = b.Tables.ToDictionary(t => t.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out Table? sideA);
            bByIdentity.TryGetValue(id, out Table? sideB);
            DifferenceStatus status = ClassifyTable(sideA, sideB, options);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static IEnumerable<DifferencePair> CompareModules<TModule>(
        IReadOnlyList<TModule> ax,
        IReadOnlyList<TModule> bx)
        where TModule : Module
    {
        var aByIdentity = ax.ToDictionary(m => m.Identity);
        var bByIdentity = bx.ToDictionary(m => m.Identity);
        HashSet<ObjectIdentity> allIdentities = [.. aByIdentity.Keys];
        allIdentities.UnionWith(bByIdentity.Keys);

        foreach (ObjectIdentity id in allIdentities.OrderBy(i => i.SchemaName).ThenBy(i => i.ObjectName))
        {
            aByIdentity.TryGetValue(id, out TModule? sideA);
            bByIdentity.TryGetValue(id, out TModule? sideB);
            DifferenceStatus status = ClassifyModule(sideA, sideB);
            yield return new DifferencePair(id, status, sideA, sideB);
        }
    }

    private static DifferenceStatus ClassifyModule(Module? a, Module? b)
    {
        if (a is null && b is not null)
        {
            return DifferenceStatus.OnlyInB;
        }
        if (a is not null && b is null)
        {
            return DifferenceStatus.OnlyInA;
        }
        if (a is null || b is null)
        {
            return DifferenceStatus.Identical;
        }

        // Encrypted bodies are opaque — we cannot prove equality, so we err on the side
        // of Different. Same when only one side is encrypted.
        if (a.IsEncrypted || b.IsEncrypted)
        {
            return DifferenceStatus.Different;
        }

        string? na = BodyNormalizer.Normalize(a.Body);
        string? nb = BodyNormalizer.Normalize(b.Body);
        return string.Equals(na, nb, StringComparison.Ordinal)
            ? DifferenceStatus.Identical
            : DifferenceStatus.Different;
    }

    private static DifferenceStatus ClassifyTable(Table? a, Table? b, ComparisonOptions options)
    {
        if (a is null && b is not null)
        {
            return DifferenceStatus.OnlyInB;
        }

        if (a is not null && b is null)
        {
            return DifferenceStatus.OnlyInA;
        }

        if (a is null || b is null)
        {
            return DifferenceStatus.Identical;
        }

        bool columnsDiffer = !ColumnsEqual(a.Columns, b.Columns, options);
        bool constraintsDiffer = !options.HasFlag(ComparisonOptions.IgnoreKeys)
            && !ConstraintsEqual(a.Constraints, b.Constraints);
        bool indexesDiffer = !options.HasFlag(ComparisonOptions.IgnoreIndexes)
            && !IndexesEqual(a.Indexes, b.Indexes);

        return columnsDiffer || constraintsDiffer || indexesDiffer
            ? DifferenceStatus.Different
            : DifferenceStatus.Identical;
    }

    private static bool ColumnsEqual(
        IReadOnlyList<Column> ax,
        IReadOnlyList<Column> bx,
        ComparisonOptions options)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(c => c.Name);
        foreach (Column col in ax)
        {
            if (!bByName.TryGetValue(col.Name, out Column? other))
            {
                return false;
            }

            if (col.DataType != other.DataType)
            {
                return false;
            }

            if (col.IsNullable != other.IsNullable)
            {
                return false;
            }

            if (col.IsIdentity != other.IsIdentity)
            {
                return false;
            }

            if (col.IsIdentity && (col.IdentitySeed != other.IdentitySeed
                || col.IdentityIncrement != other.IdentityIncrement))
            {
                return false;
            }

            if ((col.DefaultExpression ?? string.Empty) != (other.DefaultExpression ?? string.Empty))
            {
                return false;
            }

            if ((col.ComputedExpression ?? string.Empty) != (other.ComputedExpression ?? string.Empty))
            {
                return false;
            }

            if (col.IsPersistedComputed != other.IsPersistedComputed)
            {
                return false;
            }

            if (options.HasFlag(ComparisonOptions.ForceColumnOrder) && col.Ordinal != other.Ordinal)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConstraintsEqual(
        IReadOnlyList<Constraint> ax,
        IReadOnlyList<Constraint> bx)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(c => c.Name);
        foreach (Constraint left in ax)
        {
            if (!bByName.TryGetValue(left.Name, out Constraint? right))
            {
                return false;
            }

            if (left.Kind != right.Kind)
            {
                return false;
            }

            if (!ConstraintShapeEqual(left, right))
            {
                return false;
            }
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
        IReadOnlyList<TableIndex> ax,
        IReadOnlyList<TableIndex> bx)
    {
        if (ax.Count != bx.Count)
        {
            return false;
        }

        var bByName = bx.ToDictionary(i => i.Name);
        foreach (TableIndex left in ax)
        {
            if (!bByName.TryGetValue(left.Name, out TableIndex? right))
            {
                return false;
            }

            if (left.IsUnique != right.IsUnique)
            {
                return false;
            }

            if (left.IsClustered != right.IsClustered)
            {
                return false;
            }

            if ((left.FilterExpression ?? string.Empty) != (right.FilterExpression ?? string.Empty))
            {
                return false;
            }

            if (left.KeyColumns.Count != right.KeyColumns.Count)
            {
                return false;
            }

            for (int i = 0; i < left.KeyColumns.Count; i++)
            {
                if (left.KeyColumns[i].Name != right.KeyColumns[i].Name)
                {
                    return false;
                }
                if (left.KeyColumns[i].IsDescending != right.KeyColumns[i].IsDescending)
                {
                    return false;
                }
            }

            if (!left.IncludedColumns.SequenceEqual(right.IncludedColumns))
            {
                return false;
            }
        }

        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build src/DbDelta.Core && dotnet test tests/DbDelta.Core.UnitTests --filter "ModuleDiffTests|TableTests"`
Expected: PASS — view diff tests + all existing table tests.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Diff/ComparisonEngine.cs tests/DbDelta.Core.UnitTests/Diff/ModuleDiffTests.cs
git commit -m "$(cat <<'EOF'
feat(core/diff): pair + diff views using BodyNormalizer

ComparisonEngine.Compare now emits DifferencePairs for views in addition
to tables. Bodies are normalized (whitespace collapsed) before string
comparison; encrypted-on-either-side pairs are treated as Different
because opaque bodies cannot be proven equal.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.4: Diff stored procedures inside `ComparisonEngine`

**Files:**
- Modify: `tests/DbDelta.Core.UnitTests/Diff/ModuleDiffTests.cs`

> The engine work was already implemented generically in T3.3 (`CompareModules<TModule>` is invoked for both views and procedures). T3.4 verifies the procedure path with explicit tests.

- [ ] **Step 1: Append procedure tests**

Append to `tests/DbDelta.Core.UnitTests/Diff/ModuleDiffTests.cs`:

```csharp
    private static Database DbWithProcs(params StoredProcedure[] procs) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: procs);

    [Fact]
    public void Procedure_only_in_A_is_OnlyInA()
    {
        Database a = DbWithProcs(new StoredProcedure("dbo", "uspGet", "BODY", IsEncrypted: false));
        Database b = DbWithProcs();
        DifferencePair pair = new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure");
        pair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Procedure_with_identical_bodies_is_Identical()
    {
        Database a = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 1", IsEncrypted: false));
        Database b = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 1", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Procedure_with_substantively_different_bodies_is_Different()
    {
        Database a = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 1", IsEncrypted: false));
        Database b = DbWithProcs(new StoredProcedure("dbo", "u", "SELECT 2", IsEncrypted: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Procedure")
            .Status.Should().Be(DifferenceStatus.Different);
    }
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter ModuleDiffTests`
Expected: PASS — both the view tests from T3.3 and the new procedure tests.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Core.UnitTests/Diff/ModuleDiffTests.cs
git commit -m "$(cat <<'EOF'
test(core/diff): assert stored-procedure pairing + body diff path

The engine in T3.3 uses CompareModules<TModule>() generically; these
tests lock in the procedure side so a regression in either kind fails
loudly.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase C — Provider

### Task T3.6: `ModuleReader` reads views

**Files:**
- Create: `src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs`

The reader runs **one** query that joins `sys.sql_modules` with `sys.views` (or `sys.procedures` in T3.7) and `sys.schemas`. Encrypted modules surface as rows with `definition = NULL` and `is_encrypted = 1`.

- [ ] **Step 1: Create the reader (views only for now)**

```csharp
// src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs
using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads code modules (views and stored procedures) via <c>sys.sql_modules</c>.
/// Encrypted modules surface with <c>Body = null</c> and <c>IsEncrypted = true</c>.
/// </summary>
public sealed class ModuleReader
{
    private const string ViewSql = """
        SELECT s.name AS SchemaName,
               v.name AS Name,
               sm.definition AS Body,
               CAST(sm.is_encrypted AS BIT) AS IsEncrypted
        FROM sys.views AS v
        INNER JOIN sys.schemas AS s ON s.schema_id = v.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = v.object_id
        WHERE v.is_ms_shipped = 0
        ORDER BY s.name, v.name;
        """;

    public async Task<IReadOnlyList<View>> ReadViewsAsync(SqlConnection connection, CancellationToken ct)
    {
        List<View> views = [];
        await using SqlCommand cmd = new(ViewSql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = !r.IsDBNull(3) && r.GetBoolean(3);
            views.Add(new View(schema, name, body, encrypted));
        }
        return views;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/DbDelta.Providers.LiveDb`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs
git commit -m "$(cat <<'EOF'
feat(providers/livedb): ModuleReader.ReadViewsAsync via sys.sql_modules

Reads views with their bodies + encryption flag in a single query.
Encrypted modules surface with Body = null + IsEncrypted = true.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.7: Extend `ModuleReader` with `ReadProceduresAsync`

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs`

- [ ] **Step 1: Add procedure SQL + method**

Append to `ModuleReader`:

```csharp
    private const string ProcSql = """
        SELECT s.name AS SchemaName,
               p.name AS Name,
               sm.definition AS Body,
               CAST(sm.is_encrypted AS BIT) AS IsEncrypted
        FROM sys.procedures AS p
        INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = p.object_id
        WHERE p.is_ms_shipped = 0
        ORDER BY s.name, p.name;
        """;

    public async Task<IReadOnlyList<StoredProcedure>> ReadProceduresAsync(SqlConnection connection, CancellationToken ct)
    {
        List<StoredProcedure> procs = [];
        await using SqlCommand cmd = new(ProcSql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = !r.IsDBNull(3) && r.GetBoolean(3);
            procs.Add(new StoredProcedure(schema, name, body, encrypted));
        }
        return procs;
    }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/DbDelta.Providers.LiveDb`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs
git commit -m "$(cat <<'EOF'
feat(providers/livedb): ModuleReader.ReadProceduresAsync mirror

Same shape as ReadViewsAsync against sys.procedures. Encrypted procedures
return Body = null + IsEncrypted = true so the engine can flag them
without attempting a body diff.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.8: (defensive) Treat NULL body + non-encrypted as opaque

T-SQL allows `sys.sql_modules` to be missing for some object types we don't read in M3 (e.g. older system stuff), but for `sys.views` / `sys.procedures` we always expect a row in `sys.sql_modules` unless `is_encrypted = 1`. We still defend against the unexpected combination `definition IS NULL AND is_encrypted = 0` by treating it as encrypted/opaque so downstream diff doesn't get confused.

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs`

- [ ] **Step 1: Edit `ReadViewsAsync` to upgrade NULL-body to encrypted**

Replace the body-assembly inside `ReadViewsAsync`:

```csharp
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = (!r.IsDBNull(3) && r.GetBoolean(3)) || body is null;
            views.Add(new View(schema, name, body, encrypted));
```

- [ ] **Step 2: Repeat for `ReadProceduresAsync`**

```csharp
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = (!r.IsDBNull(3) && r.GetBoolean(3)) || body is null;
            procs.Add(new StoredProcedure(schema, name, body, encrypted));
```

- [ ] **Step 3: Build**

Run: `dotnet build src/DbDelta.Providers.LiveDb`
Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/ModuleReader.cs
git commit -m "$(cat <<'EOF'
fix(providers/livedb): coerce NULL-body modules to IsEncrypted=true

A NULL definition with is_encrypted=0 is theoretically possible (rare
permission edge case where the caller can see the object but not the
module text). Promote it to IsEncrypted=true so the diff engine never
attempts to compare a null body.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.9: Compose modules into `LiveDbSource.LoadAsync`

**Files:**
- Modify: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs`

- [ ] **Step 1: Edit `LoadAsync` to read + attach modules**

Replace the body of `LoadAsync` (only the `try` block changes — keep the catch blocks intact). Full replacement of `LoadAsync`:

```csharp
    public async Task<Result<Database>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using SqlConnection connection = await ConnectionFactory.OpenAsync(_connectionString, cancellationToken);
            IReadOnlyList<Schema> schemas = await new SchemaReader().ReadAsync(connection, cancellationToken);

            // Tables with their columns (M1)
            IReadOnlyList<Table> bareTables = await new TableReader().ReadAsync(connection, cancellationToken);

            // M2: constraints + indexes, keyed by sys.objects.object_id
            IReadOnlyDictionary<int, List<Constraint>> constraintsByObject =
                await new ConstraintReader().ReadAsync(connection, cancellationToken);
            IReadOnlyDictionary<int, List<TableIndex>> indexesByObject =
                await new IndexReader().ReadAsync(connection, cancellationToken);

            IReadOnlyDictionary<(string Schema, string Name), int> objectIdByName =
                await ReadTableObjectIdsAsync(connection, cancellationToken);

            var tables = new List<Table>(bareTables.Count);
            foreach (Table t in bareTables)
            {
                int? objectId = objectIdByName.TryGetValue((t.Schema, t.Name), out int id) ? id : null;
                IReadOnlyList<Constraint> cons = objectId is int cid
                    && constraintsByObject.TryGetValue(cid, out List<Constraint>? cl)
                        ? cl
                        : [];
                IReadOnlyList<TableIndex> idx = objectId is int iid
                    && indexesByObject.TryGetValue(iid, out List<TableIndex>? il)
                        ? il
                        : [];
                tables.Add(t with { Constraints = cons, Indexes = idx });
            }

            // M3: views + stored procedures
            ModuleReader moduleReader = new();
            IReadOnlyList<View> views = await moduleReader.ReadViewsAsync(connection, cancellationToken);
            IReadOnlyList<StoredProcedure> procs = await moduleReader.ReadProceduresAsync(connection, cancellationToken);

            string dbName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            return Result<Database>.Success(new Database(dbName, schemas, tables, views, procs));
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
```

- [ ] **Step 2: Build whole solution**

Run: `dotnet build`
Expected: 0 errors. (Any existing call site that constructs `Database` with the 3-arg ctor keeps working — Views/Procedures default to empty.)

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/LiveDbSource.cs
git commit -m "$(cat <<'EOF'
feat(providers/livedb): LoadAsync reads views + procedures via ModuleReader

LiveDbSource now populates Database.Views and Database.Procedures so the
core ComparisonEngine sees them. ModuleReader runs two queries; the cost
is two extra round trips per side, dominated by the live network latency
not the row volume.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase D — ScriptGen

### Task T3.10: `ViewScriptEmitter` + golden tests

**Files:**
- Create: `src/DbDelta.Core/ScriptGen/ViewScriptEmitter.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/ViewGoldenTests.cs`

- [ ] **Step 1: Write failing golden tests**

```csharp
// tests/DbDelta.ScriptGen.GoldenTests/ViewGoldenTests.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

[UsesVerify]
public class ViewGoldenTests
{
    private readonly ViewScriptEmitter _emitter = new();

    [Fact]
    public Task Add_view_emits_CREATE_OR_ALTER()
    {
        View v = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer;", IsEncrypted: false);
        DifferencePair pair = new(v.Identity, DifferenceStatus.OnlyInA, v, null);
        string ddl = _emitter.Emit(pair);
        return Verifier.Verify(ddl);
    }

    [Fact]
    public Task Drop_view_emits_DROP_VIEW_IF_EXISTS()
    {
        View v = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT 1 AS X;", IsEncrypted: false);
        DifferencePair pair = new(v.Identity, DifferenceStatus.OnlyInB, null, v);
        string ddl = _emitter.Emit(pair);
        return Verifier.Verify(ddl);
    }

    [Fact]
    public Task Different_view_emits_CREATE_OR_ALTER_with_new_body()
    {
        View a = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer;", IsEncrypted: false);
        View b = new("dbo", "vCustomer", "CREATE VIEW dbo.vCustomer AS SELECT Id FROM dbo.Customer;", IsEncrypted: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        string ddl = _emitter.Emit(pair);
        return Verifier.Verify(ddl);
    }

    [Fact]
    public Task Encrypted_view_emits_a_comment_warning_and_no_DDL()
    {
        View v = new("dbo", "vSecret", Body: null, IsEncrypted: true);
        DifferencePair pair = new(v.Identity, DifferenceStatus.OnlyInA, v, null);
        string ddl = _emitter.Emit(pair);
        return Verifier.Verify(ddl);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail (compile error: `ViewScriptEmitter` missing)**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter ViewGoldenTests`
Expected: compile error.

- [ ] **Step 3: Create `ViewScriptEmitter`**

```csharp
// src/DbDelta.Core/ScriptGen/ViewScriptEmitter.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for view differences:
/// <list type="bullet">
///   <item><c>OnlyInA</c> (add): the side-A body rewritten as <c>CREATE OR ALTER VIEW</c>.</item>
///   <item><c>OnlyInB</c> (drop): <c>DROP VIEW IF EXISTS [schema].[name];</c></item>
///   <item><c>Different</c> (modify): the side-A body rewritten as <c>CREATE OR ALTER VIEW</c>.</item>
///   <item>Encrypted on side A: a <c>-- WARNING</c> comment, no DDL (cannot script an opaque body).</item>
/// </list>
/// </summary>
public sealed class ViewScriptEmitter
{
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is View v => EmitCreateOrAlter(v),
            DifferenceStatus.OnlyInB when pair.SideB is View v => EmitDrop(v),
            DifferenceStatus.Different when pair.SideA is View v => EmitCreateOrAlter(v),
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(View v)
    {
        if (v.IsEncrypted || v.Body is null)
        {
            return $"-- WARNING: view [{v.Schema}].[{v.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }

        // Rewrite the leading CREATE VIEW (case-insensitive, allowing optional whitespace) to CREATE OR ALTER VIEW.
        string body = v.Body.TrimStart();
        const string createView = "CREATE VIEW";
        const string createOrAlterView = "CREATE OR ALTER VIEW";
        if (body.StartsWith(createView, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterView, body.AsSpan(createView.Length));
        }
        // If the catalog returned `CREATE OR ALTER VIEW` (or some other shape) leave it untouched.
        return body.EndsWith(';') ? body : body + ";";
    }

    private static string EmitDrop(View v) =>
        $"DROP VIEW IF EXISTS [{v.Schema}].[{v.Name}];";
}
```

- [ ] **Step 4: Run tests + accept golden snapshots**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter ViewGoldenTests`
Expected: first run FAIL (no `*.verified.txt` files yet). Inspect each `*.received.txt` under `tests/DbDelta.ScriptGen.GoldenTests/snapshots/`, then accept by renaming `received` → `verified` (or run the IDE accept-all if available). Re-run and confirm PASS.

Expected snapshot for `Add_view_emits_CREATE_OR_ALTER`:
```
CREATE OR ALTER VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer;
```

Expected snapshot for `Drop_view_emits_DROP_VIEW_IF_EXISTS`:
```
DROP VIEW IF EXISTS [dbo].[vCustomer];
```

Expected snapshot for `Different_view_emits_CREATE_OR_ALTER_with_new_body`:
```
CREATE OR ALTER VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer;
```

Expected snapshot for `Encrypted_view_emits_a_comment_warning_and_no_DDL`:
```
-- WARNING: view [dbo].[vSecret] is encrypted (WITH ENCRYPTION); body cannot be scripted.
```

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/ViewScriptEmitter.cs tests/DbDelta.ScriptGen.GoldenTests/ViewGoldenTests.cs tests/DbDelta.ScriptGen.GoldenTests/snapshots/*View*
git commit -m "$(cat <<'EOF'
feat(core/scriptgen): ViewScriptEmitter (CREATE OR ALTER / DROP IF EXISTS)

- Rewrites the captured CREATE VIEW prefix to CREATE OR ALTER VIEW so
  the generated script is idempotent.
- Drops use DROP VIEW IF EXISTS.
- Encrypted views surface a -- WARNING comment instead of unscriptable
  DDL, matching spec §3.4 (treat as opaque, flag in warning).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.11: `ProcedureScriptEmitter` + golden tests

**Files:**
- Create: `src/DbDelta.Core/ScriptGen/ProcedureScriptEmitter.cs`
- Create: `tests/DbDelta.ScriptGen.GoldenTests/ProcedureGoldenTests.cs`

- [ ] **Step 1: Write failing golden tests**

```csharp
// tests/DbDelta.ScriptGen.GoldenTests/ProcedureGoldenTests.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using VerifyXunit;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

[UsesVerify]
public class ProcedureGoldenTests
{
    private readonly ProcedureScriptEmitter _emitter = new();

    [Fact]
    public Task Add_procedure_emits_CREATE_OR_ALTER()
    {
        StoredProcedure p = new("dbo", "uspGetCustomer",
            "CREATE PROCEDURE dbo.uspGetCustomer @Id int AS SELECT * FROM dbo.Customer WHERE Id = @Id;",
            IsEncrypted: false);
        DifferencePair pair = new(p.Identity, DifferenceStatus.OnlyInA, p, null);
        return Verifier.Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Drop_procedure_emits_DROP_PROCEDURE_IF_EXISTS()
    {
        StoredProcedure p = new("dbo", "uspGetCustomer", "CREATE PROCEDURE dbo.uspGetCustomer AS RETURN 0;", IsEncrypted: false);
        DifferencePair pair = new(p.Identity, DifferenceStatus.OnlyInB, null, p);
        return Verifier.Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Different_procedure_emits_CREATE_OR_ALTER_with_new_body()
    {
        StoredProcedure a = new("dbo", "u", "CREATE PROCEDURE dbo.u AS SELECT 1;", IsEncrypted: false);
        StoredProcedure b = new("dbo", "u", "CREATE PROCEDURE dbo.u AS SELECT 2;", IsEncrypted: false);
        DifferencePair pair = new(a.Identity, DifferenceStatus.Different, a, b);
        return Verifier.Verify(_emitter.Emit(pair));
    }

    [Fact]
    public Task Encrypted_procedure_emits_warning_comment()
    {
        StoredProcedure p = new("dbo", "uspSecret", Body: null, IsEncrypted: true);
        DifferencePair pair = new(p.Identity, DifferenceStatus.OnlyInA, p, null);
        return Verifier.Verify(_emitter.Emit(pair));
    }
}
```

- [ ] **Step 2: Run to confirm compile failure**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter ProcedureGoldenTests`
Expected: compile error.

- [ ] **Step 3: Create `ProcedureScriptEmitter`**

```csharp
// src/DbDelta.Core/ScriptGen/ProcedureScriptEmitter.cs
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits DDL for stored-procedure differences. Same shape as
/// <see cref="ViewScriptEmitter"/> but using <c>CREATE OR ALTER PROCEDURE</c>
/// and <c>DROP PROCEDURE IF EXISTS</c>.
/// </summary>
public sealed class ProcedureScriptEmitter
{
    public string Emit(DifferencePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return pair.Status switch
        {
            DifferenceStatus.OnlyInA when pair.SideA is StoredProcedure p => EmitCreateOrAlter(p),
            DifferenceStatus.OnlyInB when pair.SideB is StoredProcedure p => EmitDrop(p),
            DifferenceStatus.Different when pair.SideA is StoredProcedure p => EmitCreateOrAlter(p),
            _ => string.Empty,
        };
    }

    private static string EmitCreateOrAlter(StoredProcedure p)
    {
        if (p.IsEncrypted || p.Body is null)
        {
            return $"-- WARNING: procedure [{p.Schema}].[{p.Name}] is encrypted (WITH ENCRYPTION); body cannot be scripted.";
        }
        string body = p.Body.TrimStart();
        const string createProc = "CREATE PROCEDURE";
        const string createOrAlterProc = "CREATE OR ALTER PROCEDURE";
        const string createProcShort = "CREATE PROC";
        const string createOrAlterProcShort = "CREATE OR ALTER PROC";

        if (body.StartsWith(createProc, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterProc, body.AsSpan(createProc.Length));
        }
        else if (body.StartsWith(createProcShort, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat(createOrAlterProcShort, body.AsSpan(createProcShort.Length));
        }
        return body.EndsWith(';') ? body : body + ";";
    }

    private static string EmitDrop(StoredProcedure p) =>
        $"DROP PROCEDURE IF EXISTS [{p.Schema}].[{p.Name}];";
}
```

- [ ] **Step 4: Run tests + accept goldens**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter ProcedureGoldenTests`
Inspect `received` snapshots, accept, re-run.

Expected snapshots:
- `Add_procedure_emits_CREATE_OR_ALTER`:
  ```
  CREATE OR ALTER PROCEDURE dbo.uspGetCustomer @Id int AS SELECT * FROM dbo.Customer WHERE Id = @Id;
  ```
- `Drop_procedure_emits_DROP_PROCEDURE_IF_EXISTS`:
  ```
  DROP PROCEDURE IF EXISTS [dbo].[uspGetCustomer];
  ```
- `Different_procedure_emits_CREATE_OR_ALTER_with_new_body`:
  ```
  CREATE OR ALTER PROCEDURE dbo.u AS SELECT 1;
  ```
- `Encrypted_procedure_emits_warning_comment`:
  ```
  -- WARNING: procedure [dbo].[uspSecret] is encrypted (WITH ENCRYPTION); body cannot be scripted.
  ```

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/ProcedureScriptEmitter.cs tests/DbDelta.ScriptGen.GoldenTests/ProcedureGoldenTests.cs tests/DbDelta.ScriptGen.GoldenTests/snapshots/*Procedure*
git commit -m "$(cat <<'EOF'
feat(core/scriptgen): ProcedureScriptEmitter (CREATE OR ALTER PROC / DROP IF EXISTS)

Mirrors ViewScriptEmitter but recognises both PROCEDURE and PROC keywords
in the captured body so the emitted CREATE OR ALTER stays well-formed
regardless of which spelling the source DB used.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.12: Wire emitters into `ScriptGenerator` — tables → indexes → views → procs → FKs

**Files:**
- Modify: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`

- [ ] **Step 1: Replace `Generate` to slot views + procedures between indexes and FKs**

```csharp
// src/DbDelta.Core/ScriptGen/ScriptGenerator.cs
using System.Text;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Orchestrates per-object emitters and wraps the output in a deployment-ready
/// batch. Order: tables (with PK/UQ/CK/DF/identity/computed inline) → standalone
/// CREATE INDEX → views (CREATE OR ALTER) → procedures (CREATE OR ALTER) → ALTER
/// TABLE ADD CONSTRAINT … FOREIGN KEY.
/// </summary>
public sealed class ScriptGenerator
{
    private readonly TableScriptEmitter _tableEmitter = new();
    private readonly IndexScriptEmitter _indexEmitter = new();
    private readonly ForeignKeyScriptEmitter _fkEmitter = new();
    private readonly ViewScriptEmitter _viewEmitter = new();
    private readonly ProcedureScriptEmitter _procEmitter = new();

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

        // 2. Indexes — only for newly created tables (full diff is M8)
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

        // 4. Procedures
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

        // 5. Foreign keys — emitted last so referenced tables already exist.
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

- [ ] **Step 2: Run all golden tests to verify no regression**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests`
Expected: all golden tests (M1, M2, M3) pass. Any new `received` snapshots from existing tests need investigation — the table/index/FK ordering is unchanged so none should differ.

- [ ] **Step 3: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/ScriptGenerator.cs
git commit -m "$(cat <<'EOF'
feat(core/scriptgen): ScriptGenerator orders tables → indexes → views → procs → FKs

Views + procedures slot between standalone indexes and FK creation:
modules can reference tables that already exist by that point, and FKs
remain last so a single top-to-bottom run still works without a full
dependency resolver (M7).

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase E — End-to-end (provider + CLI)

### Task T3.13: Provider integration test — round-trips a view + a procedure (+ encrypted)

**Files:**
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/ModuleReaderTests.cs`

- [ ] **Step 1: Write the test**

```csharp
// tests/DbDelta.Providers.LiveDb.IntegrationTests/ModuleReaderTests.cs
using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using DbDelta.Providers.LiveDb;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class ModuleReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_views_with_their_bodies()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaModulesView";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.vCustomer') IS NULL EXEC('CREATE VIEW dbo.vCustomer AS SELECT 1 AS Id;');
            """, ct);

        LiveDbSource source = new(conn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue();

        Database db = result.Value;
        View v = db.Views.Should().ContainSingle(x => x.Name == "vCustomer").Subject;
        v.Schema.Should().Be("dbo");
        v.IsEncrypted.Should().BeFalse();
        v.Body.Should().NotBeNullOrWhiteSpace();
        v.Body!.Should().Contain("SELECT 1 AS Id");
    }

    [Fact]
    public async Task LiveDbSource_loads_procedures_with_their_bodies()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaModulesProc";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.uspGet') IS NULL
                EXEC('CREATE PROCEDURE dbo.uspGet AS SELECT 1 AS Id;');
            """, ct);

        LiveDbSource source = new(conn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue();

        StoredProcedure p = result.Value.Procedures.Should()
            .ContainSingle(x => x.Name == "uspGet").Subject;
        p.IsEncrypted.Should().BeFalse();
        p.Body.Should().NotBeNullOrWhiteSpace();
        p.Body!.Should().Contain("SELECT 1 AS Id");
    }

    [Fact]
    public async Task LiveDbSource_surfaces_encrypted_modules_with_null_body()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaModulesEncrypted";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.uspSecret') IS NULL
                EXEC('CREATE PROCEDURE dbo.uspSecret WITH ENCRYPTION AS SELECT 1;');
            """, ct);

        LiveDbSource source = new(conn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue();

        StoredProcedure p = result.Value.Procedures.Should()
            .ContainSingle(x => x.Name == "uspSecret").Subject;
        p.IsEncrypted.Should().BeTrue();
        p.Body.Should().BeNull();
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

- [ ] **Step 2: Build + run only the new tests**

Run:
```
dotnet build tests/DbDelta.Providers.LiveDb.IntegrationTests
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter ModuleReaderTests
```
Expected: PASS — three integration tests against the Testcontainers MSSQL instance.

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Providers.LiveDb.IntegrationTests/ModuleReaderTests.cs
git commit -m "$(cat <<'EOF'
test(providers/livedb): integration tests for views + procedures + encrypted

Verifies LiveDbSource.LoadAsync round-trips view + procedure bodies and
that WITH ENCRYPTION procedures surface as IsEncrypted=true + Body=null.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task T3.14: CLI acceptance test — view added and procedure changed yield exit-code 1

**Files:**
- Modify: `tests/DbDelta.Cli.AcceptanceTests/CompareCommandTests.cs`

- [ ] **Step 1: Append two acceptance tests**

Append two new `[Fact]` methods to `CompareCommandTests`:

```csharp
    [Fact]
    public async Task Returns_exit_code_1_when_source_has_an_extra_view()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaViewSrc";
        const string tgtDb = "DbDeltaViewTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateViewSrcOnly(srcDb, ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    [Fact]
    public async Task Returns_exit_code_1_when_a_procedure_body_differs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaProcSrc";
        const string tgtDb = "DbDeltaProcTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateProcWithBody(srcDb, "SELECT 1 AS Id;", ct);
        await CreateProcWithBody(tgtDb, "SELECT 2 AS Id;", ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    private async Task CreateViewSrcOnly(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            "IF OBJECT_ID('dbo.vReport') IS NULL EXEC('CREATE VIEW dbo.vReport AS SELECT 1 AS Id;');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateProcWithBody(string db, string innerSql, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            $"IF OBJECT_ID('dbo.uspGet') IS NULL EXEC('CREATE PROCEDURE dbo.uspGet AS {innerSql}');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
```

- [ ] **Step 2: Run the acceptance tests**

Run: `dotnet test tests/DbDelta.Cli.AcceptanceTests --filter "Returns_exit_code_1_when_source_has_an_extra_view|Returns_exit_code_1_when_a_procedure_body_differs"`
Expected: PASS — both scenarios produce exit code 1 (differences found).

- [ ] **Step 3: Commit**

```bash
git add tests/DbDelta.Cli.AcceptanceTests/CompareCommandTests.cs
git commit -m "$(cat <<'EOF'
test(cli): acceptance scenarios for view-added + procedure-body-changed

Adds two end-to-end CLI scenarios that drive a live MSSQL container
through the compare command and assert exit-code 1 — the diff path
includes module pairing all the way from sys.sql_modules to the
exit code.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

## Phase F — Verification & wrap-up

### Task T3.15: Run the full local matrix

- [ ] **Step 1: Build the whole solution Release**

Run: `dotnet build --configuration Release`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Run all test projects**

Run (one at a time so failures are obvious):
```
dotnet test tests/DbDelta.Core.UnitTests --configuration Release
dotnet test tests/DbDelta.Architecture.Tests --configuration Release
dotnet test tests/DbDelta.ScriptGen.GoldenTests --configuration Release
dotnet test tests/DbDelta.App.ComponentTests --configuration Release
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --configuration Release
dotnet test tests/DbDelta.Cli.AcceptanceTests --configuration Release
```
Expected: all green.

- [ ] **Step 3: Architecture gate**

The `Architecture.Tests` (NetArchTest) project enforces that `DbDelta.Core` references no infrastructure. The new `Module`/`View`/`StoredProcedure` records are pure data — confirm the gate stays green; if it fails, the violation is likely an accidental `using Microsoft.Data.SqlClient;` left in a Core file.

If the existing arch tests don't yet cover modules, no new assertion is required for M3 — the global "Core has no I/O deps" rule already covers any new file added to `DbDelta.Core`.

---

### Task T3.16: `dotnet format` + commit

- [ ] **Step 1: Run formatter**

Run: `dotnet format`
Expected: a handful of whitespace / using-sort tweaks across the new files.

- [ ] **Step 2: Inspect, then commit if anything changed**

Run: `git status --short`
If files changed:
```bash
git add -A
git commit -m "$(cat <<'EOF'
chore: dotnet format (M3 wrap-up)

Whitespace + using-sort normalization after the M3 series. No
behavioural change.

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 3: Push + confirm CI green**

```bash
git push origin main
gh run list --limit 1
gh run watch <run-id> --exit-status
```
Expected: both `windows-build` and `linux-integration-tests` jobs green.

---

## Self-Review Checklist

After completing all tasks, verify:

1. **Spec coverage**
   - §1.2 View + Stored Procedure object kinds → T3.1, T3.2 (model), T3.6–T3.9 (read), T3.10–T3.12 (emit) ✓
   - §1.2 "line-level T-SQL diff for module bodies" → T3.5 BodyNormalizer + T3.3 / T3.4 body diff ✓ (line-level visual diff is M10)
   - §3.4 Encrypted module → opaque + warning → T3.8 coercion, T3.10/T3.11 warning comment, T3.13 integration assertion ✓
   - §5.2 Provider integration tests per kind → T3.13 ✓
   - §5.2 Script-gen golden tests per kind/change-type → T3.10, T3.11 (4 snapshots each) ✓
   - §6.2 M3 milestone scope → All tasks ✓

2. **Placeholder scan** — no "TBD", no "implement later", no "similar to Task N", no "fill in".

3. **Type consistency**
   - `Module.Body` (nullable) is consistent across `View`, `StoredProcedure`, all readers, both emitters, and the comparison engine.
   - `IsEncrypted` semantics: `true` ⇒ `Body` may be null and downstream MUST NOT diff bodies. Enforced in T3.8 (coercion) + T3.3 (engine) + T3.10/T3.11 (emit comment instead of DDL).
   - `Kind` discriminator strings: `"View"`, `"Procedure"` — used in `Module.Identity` and in `ScriptGenerator` filter (`p.Identity.Kind == "View"`, `== "Procedure"`).
   - `Database` ctor: 3-arg overload still exists for back-compat; 5-arg overload adds Views + Procedures.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-20-m3-views-and-procedures.md`. Two execution options:

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
