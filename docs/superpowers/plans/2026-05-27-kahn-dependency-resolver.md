# Kahn Dependency Resolver Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Order generated deploy scripts by real object-level dependencies (Kahn topological sort) instead of fixed alphabetical-within-kind, fixing cross-kind ordering failures (computed-column→function, view→view, view→function, schemabound-TVF→table).

**Architecture:** A pure `DbDelta.Core/Dependency/` module builds an object-level dependency graph, topologically sorts it (Kahn, deterministic tiebreak), and detects cycles (DFS over the residual after Kahn). The `LiveDb` provider populates dependency edges from `sys.sql_expression_dependencies` onto a new `Database.Dependencies` field. `ScriptGenerator` is refactored to emit removals in reverse-topological order (DROP pass) and creations in topological order (CREATE pass). Foreign-key edges are excluded from the graph (already deferred to a final phase, which breaks all FK cycles). With an empty edge list the CREATE order reproduces the current emission order exactly, so create-only goldens stay green; the new ordering manifests when the live reader supplies edges, and drop-containing goldens re-baseline to the corrected reverse-topo order.

**Tech Stack:** C# / .NET 10, xunit.v3, FluentAssertions, Verify.Xunit (goldens), Testcontainers.MsSql (live integration), FsCheck 3.0 (property), Microsoft.Data.SqlClient.

---

## Background facts (verified in codebase)

- `ObjectIdentity` is `public readonly record struct ObjectIdentity(string SchemaName, string ObjectName, string Kind)` — `src/DbDelta.Core/ObjectModel/Table.cs:28`.
- Kinds in use: `"Table"`, `"View"`, `"Function"`, `"Procedure"`, `"Trigger"`, `"Sequence"`, `"Synonym"`, `"UserDefinedType"`, `"TableType"`, `"User"`, `"Role"`, `"Permission"`, `"Schema"`.
- `DifferencePair` is `record(ObjectIdentity Identity, DifferenceStatus Status, object? SideA, object? SideB)` — `src/DbDelta.Core/Diff/DifferencePair.cs`.
- `ComparisonResult` is `record(IReadOnlyList<DifferencePair> Differences)`.
- `ScriptGenerator.Generate(ComparisonResult result, IEnumerable<DifferencePair>? selection = null, ComparisonOptions options = ComparisonOptions.Default, string? targetDefaultCollation = null)` — `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:46`. It receives **no** `Database`, so edges must be passed in as a new optional parameter.
- `Database` is a record with `init`-only collection properties — `src/DbDelta.Core/ObjectModel/Database.cs`.
- `LiveDbSource.LoadAsync` composes readers and builds the `Database` at `src/DbDelta.Providers.LiveDb/LiveDbSource.cs:84`.
- Golden tests use `Verify(script)` — e.g. `tests/DbDelta.ScriptGen.GoldenTests/ScriptGeneratorOrderingGoldenTests.cs`.
- Live container fixture pattern: `tests/DbDelta.Providers.LiveDb.IntegrationTests/LiveDbFixture.cs` (`LiveDbCollection`).
- Round-trip apply helper: `SqlExecutor.ExecuteAsync(conn, script, ct)` returns `SqlBatchResult(bool Success, string? ErrorMessage, int BatchesExecuted, int TotalDurationMs)` — `src/DbDelta.Persistence/Sql/SqlExecutor.cs`.

## Edge direction convention (used everywhere below)

`DependencyEdge(Dependent, Referenced)` means **"Dependent depends on Referenced ⇒ Referenced must be created first."**
In the Kahn graph this becomes an arc `Referenced → Dependent` and increments `inDegree[Dependent]`. Kahn dequeues in-degree-0 nodes first, so Referenced emits before Dependent. ✔

## KindRank (tiebreak — preserves current order when no edge constrains two nodes)

```
Sequence = 0, UserDefinedType = 1, TableType = 2, Table = 3,
View = 4, Function = 5, Procedure = 6, Trigger = 7, Synonym = 8
```
Any kind not listed → `int.MaxValue` (sorted last; should not appear in the topo set). This ordering matches the current pipeline so empty-edge output is byte-identical.

---

## Task 1: `EdgeKind` enum + `DependencyEdge` record

**Files:**
- Create: `src/DbDelta.Core/Dependency/EdgeKind.cs`
- Create: `src/DbDelta.Core/Dependency/DependencyEdge.cs`
- Test: `tests/DbDelta.Core.UnitTests/Dependency/DependencyEdgeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Dependency;

public class DependencyEdgeTests
{
    [Fact]
    public void Edge_carries_dependent_referenced_and_kind()
    {
        ObjectIdentity table = new("dbo", "Customer", "Table");
        ObjectIdentity fn = new("dbo", "fnFullName", "Function");

        DependencyEdge edge = new(Dependent: table, Referenced: fn, Kind: EdgeKind.ComputedColumn);

        edge.Dependent.Should().Be(table);
        edge.Referenced.Should().Be(fn);
        edge.Kind.Should().Be(EdgeKind.ComputedColumn);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DependencyEdgeTests"`
Expected: FAIL — `EdgeKind` / `DependencyEdge` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

`src/DbDelta.Core/Dependency/EdgeKind.cs`:
```csharp
namespace DbDelta.Core.Dependency;

/// <summary>
/// Classifies why one object depends on another. <see cref="ForeignKey"/>
/// edges are recorded for completeness but excluded from the topological
/// graph: FKs are always emitted in a final phase, which breaks FK cycles.
/// </summary>
public enum EdgeKind
{
    ModuleReference,
    ComputedColumn,
    CheckConstraint,
    DefaultConstraint,
    FunctionOnTable,
    TriggerOnTable,
    ForeignKey,
}
```

`src/DbDelta.Core/Dependency/DependencyEdge.cs`:
```csharp
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// "<paramref name="Dependent"/> depends on <paramref name="Referenced"/>"
/// ⇒ Referenced must be created before Dependent.
/// </summary>
public readonly record struct DependencyEdge(
    ObjectIdentity Dependent,
    ObjectIdentity Referenced,
    EdgeKind Kind);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DependencyEdgeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Dependency/ tests/DbDelta.Core.UnitTests/Dependency/DependencyEdgeTests.cs
git commit -m "feat(dependency): EdgeKind + DependencyEdge value types"
```

---

## Task 2: `Database.Dependencies` field

**Files:**
- Modify: `src/DbDelta.Core/ObjectModel/Database.cs`
- Test: `tests/DbDelta.Core.UnitTests/ObjectModel/DatabaseDependenciesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class DatabaseDependenciesTests
{
    [Fact]
    public void Dependencies_defaults_to_empty()
    {
        Database db = new("Db", Schemas: [], Tables: []);
        db.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void Dependencies_round_trips_via_init()
    {
        DependencyEdge e = new(
            new("dbo", "Customer", "Table"),
            new("dbo", "fnX", "Function"),
            EdgeKind.ComputedColumn);

        Database db = new("Db", Schemas: [], Tables: []) { Dependencies = [e] };

        db.Dependencies.Should().ContainSingle().Which.Should().Be(e);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DatabaseDependenciesTests"`
Expected: FAIL — `Database.Dependencies` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `src/DbDelta.Core/ObjectModel/Database.cs`, add a `using DbDelta.Core.Dependency;` at the top and this property alongside the other `init` collections (after `Permissions`, before `DefaultCollation`):

```csharp
    /// <summary>
    /// Object-level dependency edges (#24). Populated by the provider from
    /// catalog metadata; consumed by the script generator to topologically
    /// order CREATE emission. Empty ⇒ the generator falls back to its stable
    /// kind-then-alphabetical order (current behaviour). Foreign-key edges may
    /// be present but are ignored by the topological sort.
    /// </summary>
    public IReadOnlyList<DependencyEdge> Dependencies { get; init; } = [];
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DatabaseDependenciesTests"`
Expected: PASS.

- [ ] **Step 5: Verify nothing else broke**

Run: `dotnet build DbDelta.sln -c Debug`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/DbDelta.Core/ObjectModel/Database.cs tests/DbDelta.Core.UnitTests/ObjectModel/DatabaseDependenciesTests.cs
git commit -m "feat(model): Database.Dependencies edge list (default empty)"
```

---

## Task 3: `DependencyCycleException`

**Files:**
- Create: `src/DbDelta.Core/Dependency/DependencyCycleException.cs`
- Test: `tests/DbDelta.Core.UnitTests/Dependency/DependencyCycleExceptionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Dependency;

public class DependencyCycleExceptionTests
{
    [Fact]
    public void Message_lists_the_cycle_path()
    {
        ObjectIdentity a = new("dbo", "A", "View");
        ObjectIdentity b = new("dbo", "B", "View");

        DependencyCycleException ex = new([a, b, a]);

        ex.Cycle.Should().Equal(a, b, a);
        ex.Message.Should().Contain("dbo.A").And.Contain("dbo.B");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DependencyCycleExceptionTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Write minimal implementation**

`src/DbDelta.Core/Dependency/DependencyCycleException.cs`:
```csharp
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// Thrown when a dependency cycle is detected among create-validated objects.
/// Such a cycle is uncreatable in a valid source database (SQL Server validates
/// referenced objects at CREATE of views/functions), so this signals a reader
/// bug rather than user error.
/// </summary>
public sealed class DependencyCycleException : Exception
{
    public IReadOnlyList<ObjectIdentity> Cycle { get; }

    public DependencyCycleException(IReadOnlyList<ObjectIdentity> cycle)
        : base("Dependency cycle among create-validated objects: "
               + string.Join(" → ", cycle.Select(o => $"{o.SchemaName}.{o.ObjectName}")))
    {
        Cycle = cycle;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DependencyCycleExceptionTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Dependency/DependencyCycleException.cs tests/DbDelta.Core.UnitTests/Dependency/DependencyCycleExceptionTests.cs
git commit -m "feat(dependency): DependencyCycleException with cycle path"
```

---

## Task 4: `DependencyResolver` — Kahn sort + SCC cycle handling

This is the pure heart of the feature. It takes the set of object identities to order plus the edge list, and returns a deterministic topological order. FK edges are filtered out. Cycles among deferred-resolution kinds (Procedure, Trigger) are tolerated (members ordered alphabetically); cycles touching any create-validated kind throw.

**Files:**
- Create: `src/DbDelta.Core/Dependency/DependencyResolver.cs`
- Test: `tests/DbDelta.Core.UnitTests/Dependency/DependencyResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Dependency;

public class DependencyResolverTests
{
    private static ObjectIdentity Id(string name, string kind) => new("dbo", name, kind);

    [Fact]
    public void Empty_edges_orders_by_kindrank_then_schema_then_name()
    {
        // Deliberately unsorted input; expect Table(3) before View(4) before
        // Function(5), alphabetical within a kind.
        var nodes = new[]
        {
            Id("vB", "View"), Id("fnA", "Function"), Id("tB", "Table"),
            Id("tA", "Table"), Id("vA", "View"),
        };

        var order = new DependencyResolver().Order(nodes, edges: []);

        order.Should().Equal(
            Id("tA", "Table"), Id("tB", "Table"),
            Id("vA", "View"), Id("vB", "View"),
            Id("fnA", "Function"));
    }

    [Fact]
    public void Referenced_object_is_ordered_before_dependent_across_kinds()
    {
        // Table has a computed column referencing a function ⇒ function first,
        // even though KindRank would otherwise put the table first.
        ObjectIdentity table = Id("Customer", "Table");
        ObjectIdentity fn = Id("fnFullName", "Function");
        var edges = new[] { new DependencyEdge(table, fn, EdgeKind.ComputedColumn) };

        var order = new DependencyResolver().Order([table, fn], edges);

        order.Should().Equal(fn, table);
    }

    [Fact]
    public void View_on_view_is_ordered_base_first()
    {
        ObjectIdentity baseV = Id("vBase", "View");
        ObjectIdentity topV = Id("vTop", "View");
        // vAlpha selects from vZeta ⇒ vZeta first, even though vAlpha < vZeta
        // alphabetically — proves the edge wins over the tiebreak.
        ObjectIdentity aOnZ = Id("vAlpha", "View");
        ObjectIdentity zBase = Id("vZeta", "View");
        var edges = new[]
        {
            new DependencyEdge(topV, baseV, EdgeKind.ModuleReference),
            new DependencyEdge(aOnZ, zBase, EdgeKind.ModuleReference),
        };

        // Materialize to List so IndexOf is available (IReadOnlyList has none).
        List<ObjectIdentity> order = [.. new DependencyResolver().Order([topV, baseV, aOnZ, zBase], edges)];

        order.IndexOf(baseV).Should().BeLessThan(order.IndexOf(topV));
        order.IndexOf(zBase).Should().BeLessThan(order.IndexOf(aOnZ)); // edge beats alphabet
    }

    [Fact]
    public void Foreign_key_edges_are_ignored()
    {
        ObjectIdentity a = Id("A", "Table");
        ObjectIdentity b = Id("B", "Table");
        // An FK edge claiming A depends on B must NOT reorder — FK edges are excluded.
        var edges = new[] { new DependencyEdge(a, b, EdgeKind.ForeignKey) };

        var order = new DependencyResolver().Order([a, b], edges);

        order.Should().Equal(a, b); // pure alphabetical, edge ignored
    }

    [Fact]
    public void Deterministic_across_input_permutations()
    {
        ObjectIdentity t = Id("T", "Table");
        ObjectIdentity v = Id("V", "View");
        ObjectIdentity f = Id("F", "Function");
        var resolver = new DependencyResolver();

        var o1 = resolver.Order([t, v, f], []);
        var o2 = resolver.Order([f, t, v], []);
        var o3 = resolver.Order([v, f, t], []);

        o1.Should().Equal(o2);
        o2.Should().Equal(o3);
    }

    [Fact]
    public void Cycle_among_procedures_is_tolerated_alphabetically()
    {
        ObjectIdentity pA = Id("uspA", "Procedure");
        ObjectIdentity pB = Id("uspB", "Procedure");
        var edges = new[]
        {
            new DependencyEdge(pA, pB, EdgeKind.ModuleReference),
            new DependencyEdge(pB, pA, EdgeKind.ModuleReference),
        };

        var order = new DependencyResolver().Order([pA, pB], edges);

        order.Should().Equal(pA, pB); // deferred-resolution kinds: alphabetical, no throw
    }

    [Fact]
    public void Cycle_touching_a_view_throws()
    {
        ObjectIdentity vA = Id("vA", "View");
        ObjectIdentity vB = Id("vB", "View");
        var edges = new[]
        {
            new DependencyEdge(vA, vB, EdgeKind.ModuleReference),
            new DependencyEdge(vB, vA, EdgeKind.ModuleReference),
        };

        Action act = () => new DependencyResolver().Order([vA, vB], edges);

        act.Should().Throw<DependencyCycleException>();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DependencyResolverTests"`
Expected: FAIL — `DependencyResolver` does not exist.

- [ ] **Step 3: Write the implementation**

`src/DbDelta.Core/Dependency/DependencyResolver.cs`:
```csharp
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// Produces a deterministic topological order of object identities from a
/// dependency edge list, for CREATE emission. Foreign-key edges are ignored
/// (FKs are emitted in a final phase). Cycles among deferred-resolution kinds
/// (Procedure, Trigger) are tolerated and ordered alphabetically; cycles that
/// touch any create-validated kind throw <see cref="DependencyCycleException"/>.
/// </summary>
public sealed class DependencyResolver
{
    private static readonly IReadOnlyDictionary<string, int> KindRank =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Sequence"] = 0,
            ["UserDefinedType"] = 1,
            ["TableType"] = 2,
            ["Table"] = 3,
            ["View"] = 4,
            ["Function"] = 5,
            ["Procedure"] = 6,
            ["Trigger"] = 7,
            ["Synonym"] = 8,
        };

    private static readonly HashSet<string> DeferredKinds =
        new(StringComparer.Ordinal) { "Procedure", "Trigger" };

    private static int Rank(ObjectIdentity id) =>
        KindRank.TryGetValue(id.Kind, out int r) ? r : int.MaxValue;

    private static int CompareNodes(ObjectIdentity a, ObjectIdentity b)
    {
        int byKind = Rank(a).CompareTo(Rank(b));
        if (byKind != 0) { return byKind; }
        int bySchema = string.CompareOrdinal(a.SchemaName, b.SchemaName);
        if (bySchema != 0) { return bySchema; }
        return string.CompareOrdinal(a.ObjectName, b.ObjectName);
    }

    public IReadOnlyList<ObjectIdentity> Order(
        IReadOnlyCollection<ObjectIdentity> nodes,
        IReadOnlyCollection<DependencyEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        HashSet<ObjectIdentity> nodeSet = [.. nodes];

        // Adjacency Referenced → {Dependents}; in-degree counts dependencies.
        // FK edges excluded. Self-edges and edges to/from unknown nodes skipped.
        Dictionary<ObjectIdentity, List<ObjectIdentity>> adj = [];
        Dictionary<ObjectIdentity, int> inDegree = [];
        foreach (ObjectIdentity n in nodeSet)
        {
            adj[n] = [];
            inDegree[n] = 0;
        }

        HashSet<(ObjectIdentity, ObjectIdentity)> seen = [];
        foreach (DependencyEdge e in edges)
        {
            if (e.Kind == EdgeKind.ForeignKey) { continue; }
            if (e.Dependent.Equals(e.Referenced)) { continue; }
            if (!nodeSet.Contains(e.Dependent) || !nodeSet.Contains(e.Referenced)) { continue; }
            if (!seen.Add((e.Referenced, e.Dependent))) { continue; }
            adj[e.Referenced].Add(e.Dependent);
            inDegree[e.Dependent]++;
        }

        // Kahn with a deterministic ready-set ordered by CompareNodes.
        // SortedSet with a comparer that never returns 0 for distinct nodes
        // (CompareNodes is a total order over distinct identities here).
        SortedSet<ObjectIdentity> ready = new(Comparer<ObjectIdentity>.Create(CompareNodes));
        foreach (ObjectIdentity n in nodeSet)
        {
            if (inDegree[n] == 0) { ready.Add(n); }
        }

        List<ObjectIdentity> order = new(nodeSet.Count);
        while (ready.Count > 0)
        {
            ObjectIdentity n = ready.Min;
            ready.Remove(n);
            order.Add(n);
            foreach (ObjectIdentity dep in adj[n])
            {
                if (--inDegree[dep] == 0) { ready.Add(dep); }
            }
        }

        if (order.Count == nodeSet.Count) { return order; }

        // Remaining nodes form one or more cycles. Tolerate cycles confined to
        // deferred kinds (emit them in node order); throw on anything else.
        List<ObjectIdentity> remaining =
            [.. nodeSet.Where(n => !order.Contains(n))];
        if (remaining.Any(n => !DeferredKinds.Contains(n.Kind)))
        {
            // Build a representative cycle path for the message via DFS.
            throw new DependencyCycleException(FindCycle(remaining, adj));
        }
        remaining.Sort(CompareNodes);
        order.AddRange(remaining);
        return order;
    }

    private static IReadOnlyList<ObjectIdentity> FindCycle(
        List<ObjectIdentity> remaining,
        Dictionary<ObjectIdentity, List<ObjectIdentity>> adj)
    {
        HashSet<ObjectIdentity> inScope = [.. remaining];
        HashSet<ObjectIdentity> onStack = [];
        List<ObjectIdentity> stack = [];

        ObjectIdentity start = remaining.OrderBy(n => n.Kind, StringComparer.Ordinal)
                                        .ThenBy(n => n.SchemaName, StringComparer.Ordinal)
                                        .ThenBy(n => n.ObjectName, StringComparer.Ordinal)
                                        .First();

        IReadOnlyList<ObjectIdentity>? found = null;
        void Dfs(ObjectIdentity node)
        {
            if (found is not null) { return; }
            stack.Add(node);
            onStack.Add(node);
            foreach (ObjectIdentity next in adj[node].Where(inScope.Contains))
            {
                if (onStack.Contains(next))
                {
                    int from = stack.IndexOf(next);
                    found = [.. stack.Skip(from), next];
                    return;
                }
                if (found is null) { Dfs(next); }
            }
            stack.RemoveAt(stack.Count - 1);
            onStack.Remove(node);
        }

        Dfs(start);
        return found ?? remaining;
    }
}
```

> Note on `SortedSet<ObjectIdentity>`: `CompareNodes` is a strict total order over the distinct identities here (kind, then schema, then name), so no two distinct nodes compare equal and none are silently dropped.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/DbDelta.Core.UnitTests --filter "FullyQualifiedName~DependencyResolverTests"`
Expected: PASS (all 7).

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/Dependency/DependencyResolver.cs tests/DbDelta.Core.UnitTests/Dependency/DependencyResolverTests.cs
git commit -m "feat(dependency): Kahn topological resolver with SCC cycle handling"
```

---

## Task 5: Extract per-kind emit helpers in `ScriptGenerator` (behaviour-preserving)

Pure refactor: pull each kind's per-pair emit body out of its loop into a private helper `EmitX(StringBuilder sb, DifferencePair pair, string? targetDefaultCollation)`. The `Generate` method still calls them from the same 7 sequential kind loops, so **output is unchanged and all goldens stay green.** This isolates the risky reordering (Task 6) from the mechanical extraction.

**Files:**
- Modify: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`

- [ ] **Step 1: Run the golden suite to capture the green baseline**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests`
Expected: PASS (28).

- [ ] **Step 2: Extract helpers**

For each create-validated/deferred kind currently emitted inline or in a `.Where(Kind==X)` loop (Sequence, UserDefinedType, TableType, Table, View, Function, Procedure, Trigger, Synonym), create a helper that emits exactly one pair. Example for View (replaces the body inside the section-3 loop):

```csharp
private void EmitOneView(StringBuilder sb, DifferencePair pair)
{
    string ddl = _viewEmitter.Emit(pair);
    if (!string.IsNullOrWhiteSpace(ddl))
    {
        sb.AppendLine(ddl);
        sb.AppendLine("GO");
    }
}
```

Do the equivalent for Table (`EmitOneTable` — the section-1 body, **table DDL + GO only**, NOT indexes), Function, Procedure, Trigger, Synonym (move the per-pair switch out of `EmitSynonyms`), Sequence (per-pair switch out of `EmitSequences`), UserDefinedType, TableType. Keep `EmitSequences`/`EmitUserDefinedTypes`/`EmitTableTypeUdts`/`EmitSynonyms` as thin wrappers that loop + call the new per-pair helper, so existing call sites still compile. Index emission stays in its own section-2 loop unchanged. Users/Roles/Permissions/FK logic untouched.

- [ ] **Step 3: Build**

Run: `dotnet build src/DbDelta.Core -c Debug`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Run goldens — must still be green (proves behaviour preserved)**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests`
Expected: PASS (28), no `.received.txt` files produced.

- [ ] **Step 5: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/ScriptGenerator.cs
git commit -m "refactor(scriptgen): extract per-kind per-pair emit helpers (no behaviour change)"
```

---

## Task 6: Drive the CREATE block from the topo order + add `dependencies` parameter

Replace the seven sequential create-validated/deferred kind loops (sections 1, 3, 4, 5, 6, 6.5 and the sequence/UDT/tabletype prologue emitters) with **one** loop over the resolver's order, dispatching by `Identity.Kind` to the Task-5 helpers. Indexes (section 2), Users/Roles prologue, the #33 inbound-FK sections (0.9 / 7.9), FK epilogue (section 7) and Permissions (section 8) are **unchanged** and keep their relative positions. Add a `dependencies` parameter (default empty) so existing callers/goldens get the current order.

**Files:**
- Modify: `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs`
- Test: `tests/DbDelta.ScriptGen.GoldenTests/DependencyOrderingGoldenTests.cs` (new — proves reordering with edges)

- [ ] **Step 1: Change the `Generate` signature**

```csharp
public string Generate(
    ComparisonResult result,
    IEnumerable<DifferencePair>? selection = null,
    ComparisonOptions options = ComparisonOptions.Default,
    string? targetDefaultCollation = null,
    IReadOnlyList<DependencyEdge>? dependencies = null)
```
Add `using DbDelta.Core.Dependency;` to the file. At the top of the method, `dependencies ??= [];`.

- [ ] **Step 2: Build the create order**

After the `pairs` list is computed and the #33 rebuild bookkeeping runs, before emitting, compute:

```csharp
// Topo order over create-validated + deferred kinds present in this diff.
// Users/Roles/Permissions/Schema are not part of the object dependency graph.
HashSet<string> topoKinds = new(StringComparer.Ordinal)
{
    "Sequence", "UserDefinedType", "TableType", "Table",
    "View", "Function", "Procedure", "Trigger", "Synonym",
};
List<DifferencePair> topoPairs = [.. pairs.Where(p => topoKinds.Contains(p.Identity.Kind))];
IReadOnlyList<ObjectIdentity> createOrder = new DependencyResolver()
    .Order([.. topoPairs.Select(p => p.Identity)], dependencies);
Dictionary<ObjectIdentity, DifferencePair> pairById =
    topoPairs.ToDictionary(p => p.Identity);
```

- [ ] **Step 3: Replace the create loops with a reverse-topo DROP pass + a single topo CREATE loop**

Delete the section-1 Tables loop, section-3 Views, section-4 Functions, section-5 Procedures, section-6 Triggers, section-6.5 `EmitSynonyms(...)`, and the prologue `EmitSequences/EmitUserDefinedTypes/EmitTableTypeUdts` calls. Keep `EmitUsers`/`EmitRoles` in the prologue.

Each `EmitOneX` helper already branches on `pair.Status` internally: an `OnlyInB` pair emits **only** a drop; `OnlyInA`/`Different` emit create/alter. So gating which pass calls the helper (by status) means each pair is emitted exactly once, in the right phase.

First, a **DROP pass in reverse topological order** so a dependent object is dropped before the object it references (spec §3 phase 1):

```csharp
// Removed objects (OnlyInB) drop dependent-first ⇒ reverse topo order.
foreach (ObjectIdentity id in createOrder.Reverse())
{
    DifferencePair pair = pairById[id];
    if (pair.Status != DifferenceStatus.OnlyInB) { continue; }
    DispatchEmit(sb, id.Kind, pair, targetDefaultCollation);
}
```

Then, after the 0.9 inbound-FK drop, the **CREATE/ALTER pass in topological order** (skipping the drops already emitted above):

```csharp
foreach (ObjectIdentity id in createOrder)
{
    DifferencePair pair = pairById[id];
    if (pair.Status == DifferenceStatus.OnlyInB) { continue; }
    DispatchEmit(sb, id.Kind, pair, targetDefaultCollation);
}
```

Add the shared dispatch helper:

```csharp
private void DispatchEmit(StringBuilder sb, string kind, DifferencePair pair, string? targetDefaultCollation)
{
    switch (kind)
    {
        case "Sequence": EmitOneSequence(sb, pair); break;
        case "UserDefinedType": EmitOneUserDefinedType(sb, pair); break;
        case "TableType": EmitOneTableTypeUdt(sb, pair, targetDefaultCollation); break;
        case "Table": EmitOneTable(sb, pair, targetDefaultCollation); break;
        case "View": EmitOneView(sb, pair); break;
        case "Function": EmitOneFunction(sb, pair); break;
        case "Procedure": EmitOneProcedure(sb, pair); break;
        case "Trigger": EmitOneTrigger(sb, pair); break;
        case "Synonym": EmitOneSynonym(sb, pair); break;
        default: break;
    }
}
```

Ordering of the regions in `Generate`: prologue (header, Users, Roles) → **DROP pass (reverse topo)** → 0.9 inbound-FK drop → **CREATE pass (topo)** → section-2 indexes → section-7 FKs → 7.9 re-add → section-8 permissions.

Leave the section-2 index loop after the CREATE pass. The index loop still iterates table pairs; to keep its order aligned with the new table order, change its ordering to follow `createOrder`:

```csharp
foreach (ObjectIdentity id in createOrder.Where(i => i.Kind == "Table"))
{
    DifferencePair pair = pairById[id];
    // ... existing index switch body, unchanged ...
}
```

Apply the same `createOrder`-driven iteration to the section-7 FK loop (`createOrder.Where(i => i.Kind == "Table")`) so FK emission order tracks table order deterministically.

- [ ] **Step 4: Build**

Run: `dotnet build src/DbDelta.Core -c Debug`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Run existing goldens — review and re-baseline only the drop-ordering changes**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests`

Expected for **create-only** fixtures (OnlyInA / Different): PASS unchanged. They pass **no** dependencies ⇒ empty edge list ⇒ the topo CREATE order equals the old KindRank/alphabetical order ⇒ byte-identical output.

Expected for fixtures that emit **drops** (any `OnlyInB` pair): the drop order changes from the old forward kind order to the new **reverse-topological** order — this is the intended correctness fix (dependents dropped first). For each such `.received.txt`, open the diff and confirm it is a pure reordering of `DROP` statements (no statement added/removed, no semantic change), then accept it:

Run (review first, then accept): `dotnet verify accept -y` (or delete the stale `.verified.txt` and re-run).

Record in the commit message exactly which fixtures changed and that the change is reverse-topo drop ordering. If a *create-only* fixture changes, STOP — that means KindRank or the loop order drifted from the original pipeline; reconcile before accepting.

- [ ] **Step 6: Write the failing new golden (proves edges reorder)**

```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using Xunit;

namespace DbDelta.ScriptGen.GoldenTests;

public class DependencyOrderingGoldenTests
{
    [Fact]
    public Task Function_referenced_by_computed_column_is_emitted_before_its_table()
    {
        Schema dbo = new("dbo");
        // Table whose name sorts BEFORE the function, but depends on it.
        Table customer = new("dbo", "Customer",
            Columns: [new Column("Id", "int", isNullable: false, ordinal: 1)],
            Constraints: [], Indexes: []);
        Function fn = new("dbo", "fnZ",
            "CREATE FUNCTION dbo.fnZ() RETURNS int AS BEGIN RETURN 1 END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);

        Database source = new("Db", Schemas: [dbo], Tables: [customer],
            Views: [], Procedures: [], Functions: [fn], Triggers: [])
        {
            Dependencies =
            [
                new DependencyEdge(
                    new("dbo", "Customer", "Table"),
                    new("dbo", "fnZ", "Function"),
                    EdgeKind.ComputedColumn),
            ],
        };
        Database target = new("Db", Schemas: [dbo], Tables: [],
            Views: [], Procedures: [], Functions: [], Triggers: []);

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            result, selection: null, options: ComparisonOptions.Default,
            targetDefaultCollation: null, dependencies: source.Dependencies);
        return Verify(script);
    }
}
```

- [ ] **Step 7: Run it, review the `.received`, accept as the verified baseline**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests --filter "FullyQualifiedName~DependencyOrderingGoldenTests"`
Expected: first run FAILs (no `.verified.txt`). Open `DependencyOrderingGoldenTests.Function_referenced_by_computed_column_is_emitted_before_its_table.received.txt` and confirm `CREATE FUNCTION dbo.fnZ` appears **before** `CREATE TABLE [dbo].[Customer]`. Accept:
Run: `dotnet verify accept -y`
Re-run the filter → PASS.

- [ ] **Step 8: Full golden suite green**

Run: `dotnet test tests/DbDelta.ScriptGen.GoldenTests`
Expected: PASS (29).

- [ ] **Step 9: Commit**

```bash
git add src/DbDelta.Core/ScriptGen/ScriptGenerator.cs tests/DbDelta.ScriptGen.GoldenTests/DependencyOrderingGoldenTests.cs tests/DbDelta.ScriptGen.GoldenTests/*.verified.txt
git commit -m "feat(scriptgen): topo-order CREATE emission via DependencyResolver (#24)"
```

---

## Task 7: Thread dependencies through the CLI `script` verb

**Files:**
- Modify: `src/DbDelta.Cli/Commands/ScriptCommand.cs` (the `Generate` call near line 81)

- [ ] **Step 1: Pass the source database's edges**

Change the existing call:
```csharp
string script = new ScriptGenerator().Generate(
    comparison,
    selection: null,
    options: opts,
    targetDefaultCollation: tgtResult.Value!.DefaultCollation,
    dependencies: srcResult.Value!.Dependencies);
```

- [ ] **Step 2: Build**

Run: `dotnet build src/DbDelta.Cli -c Debug`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Run CLI acceptance tests (no regression)**

Run: `dotnet test tests/DbDelta.Cli.AcceptanceTests`
Expected: PASS (16).

- [ ] **Step 4: Commit**

```bash
git add src/DbDelta.Cli/Commands/ScriptCommand.cs
git commit -m "feat(cli): feed source dependency edges into script generation"
```

---

## Task 8: `DependencyReader` — populate edges from `sys.sql_expression_dependencies`

Reads module-body references plus derives FK/trigger/computed/check/default edges, returning a `IReadOnlyList<DependencyEdge>`. Wired into `LiveDbSource.LoadAsync`.

**Files:**
- Create: `src/DbDelta.Providers.LiveDb/Readers/DependencyReader.cs`
- Modify: `src/DbDelta.Providers.LiveDb/LiveDbSource.cs`
- Test: `tests/DbDelta.Providers.LiveDb.IntegrationTests/DependencyReaderTests.cs`

- [ ] **Step 1: Write the failing integration test**

```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class DependencyReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task Loads_view_on_view_and_computed_column_on_function_edges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection b = new(fixture.ConnectionString))
        {
            await b.OpenAsync(ct);
            await Exec(b, "IF DB_ID('DepEdges') IS NULL CREATE DATABASE DepEdges;", ct);
        }
        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
            { InitialCatalog = "DepEdges" }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await Exec(c, "CREATE FUNCTION dbo.fnTax(@x money) RETURNS money AS BEGIN RETURN @x * 0.2 END", ct);
            await Exec(c, """
                CREATE TABLE dbo.Sale (
                    Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    Net money NOT NULL,
                    Tax AS (dbo.fnTax(Net))
                );
                """, ct);
            await Exec(c, "CREATE VIEW dbo.vBase AS SELECT Id, Net FROM dbo.Sale;", ct);
            await Exec(c, "CREATE VIEW dbo.vTop AS SELECT Id FROM dbo.vBase;", ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        IReadOnlyList<DependencyEdge> edges = result.Value!.Dependencies;

        edges.Should().Contain(e =>
            e.Dependent.ObjectName == "vTop" && e.Referenced.ObjectName == "vBase");
        edges.Should().Contain(e =>
            e.Dependent.ObjectName == "Sale" && e.Referenced.ObjectName == "fnTax");
    }

    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter "FullyQualifiedName~DependencyReaderTests"`
Expected: FAIL — `Dependencies` is empty (reader not wired). (Requires Docker; if absent, the assertion fails rather than skips — that is fine for local dev, CI has Docker.)

- [ ] **Step 3: Implement the reader**

`src/DbDelta.Providers.LiveDb/Readers/DependencyReader.cs`:
```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Builds object-level dependency edges (#24). Module references come from
/// sys.sql_expression_dependencies; computed/check/default → function/sequence,
/// trigger → base table, and FK edges are derived from the already-loaded
/// object model so the resolver has a complete (if FK-ignoring) picture.
/// Unresolved references (cross-db, dynamic SQL, NULL referenced_id) yield no
/// edge — the affected node falls back to its stable kind/alphabetical slot.
/// </summary>
internal sealed class DependencyReader
{
    // Maps sys object "type" buckets onto the ObjectIdentity Kind strings the
    // resolver/generator use.
    private const string Sql = """
        SELECT
            referencing_schema = OBJECT_SCHEMA_NAME(d.referencing_id),
            referencing_name   = OBJECT_NAME(d.referencing_id),
            referencing_type   = ro.type,
            referenced_schema  = ISNULL(d.referenced_schema_name, OBJECT_SCHEMA_NAME(d.referenced_id)),
            referenced_name    = ISNULL(d.referenced_entity_name, OBJECT_NAME(d.referenced_id)),
            referenced_type    = eo.type
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS ro ON ro.object_id = d.referencing_id AND ro.is_ms_shipped = 0
        LEFT  JOIN sys.objects AS eo ON eo.object_id = d.referenced_id
        WHERE d.referenced_id IS NOT NULL;
        """;

    public async Task<IReadOnlyList<DependencyEdge>> ReadAsync(
        SqlConnection connection, CancellationToken ct)
    {
        List<DependencyEdge> edges = [];
        await using SqlCommand cmd = new(Sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? depSchema = r.IsDBNull(0) ? null : r.GetString(0);
            string? depName = r.IsDBNull(1) ? null : r.GetString(1);
            string depType = r.GetString(2).Trim();
            string? refSchema = r.IsDBNull(3) ? null : r.GetString(3);
            string? refName = r.IsDBNull(4) ? null : r.GetString(4);
            string? refType = r.IsDBNull(5) ? null : r.GetString(5).Trim();

            if (depSchema is null || depName is null || refSchema is null || refName is null || refType is null)
            {
                continue; // unresolved → no edge
            }

            string? depKind = MapKind(depType);
            string? refKind = MapKind(refType);
            if (depKind is null || refKind is null) { continue; }

            edges.Add(new DependencyEdge(
                new ObjectIdentity(depSchema, depName, depKind),
                new ObjectIdentity(refSchema, refName, refKind),
                EdgeKind.ModuleReference));
        }
        return edges;
    }

    // sys.objects.type → resolver Kind. Returns null for kinds outside the
    // topological set (the resolver ignores edges to/from unknown nodes anyway,
    // but filtering here keeps the edge list tight).
    private static string? MapKind(string type) => type switch
    {
        "U" => "Table",
        "V" => "View",
        "P" => "Procedure",
        "FN" or "IF" or "TF" or "FS" or "FT" => "Function",
        "TR" => "Trigger",
        "SN" => "Synonym",
        "SO" => "Sequence",
        _ => null,
    };
}
```

> Computed-column references to a scalar function (the `Sale → fnTax` case) **are** recorded in `sys.sql_expression_dependencies` with the table as `referencing_id`, so the module-reference query above already captures them — no separate computed-column query is needed for the common case. Trigger→table edges arrive the same way (trigger body references its table). FK-derived edges are not required by the resolver (FK edges are ignored) and are omitted to keep the reader minimal — YAGNI.

- [ ] **Step 4: Wire into `LiveDbSource.LoadAsync`**

In `src/DbDelta.Providers.LiveDb/LiveDbSource.cs`, after the permissions read (~line 81) and before constructing `Database`:
```csharp
IReadOnlyList<DependencyEdge> dependencies =
    await new DependencyReader().ReadAsync(connection, cancellationToken);
```
Add `using DbDelta.Core.Dependency;` at the top, and add `Dependencies = dependencies,` to the `Database` initializer block (alongside `Permissions = permissions,`).

- [ ] **Step 5: Build**

Run: `dotnet build src/DbDelta.Providers.LiveDb -c Debug`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Run the integration test**

Run: `dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter "FullyQualifiedName~DependencyReaderTests"`
Expected: PASS (Docker required).

- [ ] **Step 7: Commit**

```bash
git add src/DbDelta.Providers.LiveDb/Readers/DependencyReader.cs src/DbDelta.Providers.LiveDb/LiveDbSource.cs tests/DbDelta.Providers.LiveDb.IntegrationTests/DependencyReaderTests.cs
git commit -m "feat(livedb): DependencyReader from sys.sql_expression_dependencies (#24)"
```

---

## Task 9: Live cross-kind round-trip integration test

Proves the whole chain: read a schema with real cross-kind dependencies, generate a topo-ordered script, apply it to an empty target container, and assert it applies clean. Reuses the Task-8 fixture pattern + `SqlExecutor`.

**Files:**
- Create: `tests/DbDelta.Providers.LiveDb.IntegrationTests/DependencyRoundTripTests.cs`
- Modify: `tests/DbDelta.Providers.LiveDb.IntegrationTests/DbDelta.Providers.LiveDb.IntegrationTests.csproj` (add `DbDelta.Persistence` + `DbDelta.Core` project refs if not present)

- [ ] **Step 1: Ensure project references**

Open the csproj. It references `DbDelta.Providers.LiveDb`. Add (if missing):
```xml
<ProjectReference Include="..\..\src\DbDelta.Persistence\DbDelta.Persistence.csproj" />
<ProjectReference Include="..\..\src\DbDelta.Core\DbDelta.Core.csproj" />
```
(`DbDelta.Core` is transitively available, but reference it explicitly for `ComparisonEngine`/`ScriptGenerator`.)

- [ ] **Step 2: Write the failing test**

```csharp
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class DependencyRoundTripTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task Cross_kind_dependencies_apply_clean_on_empty_target()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "DepSrc", ct);
        await Create(fixture.ConnectionString, "DepTgt", ct);
        string src = Cat(fixture.ConnectionString, "DepSrc");
        string tgt = Cat(fixture.ConnectionString, "DepTgt");

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await Exec(c, "CREATE FUNCTION dbo.fnTax(@x money) RETURNS money AS BEGIN RETURN @x*0.2 END", ct);
            await Exec(c, "CREATE TABLE dbo.Sale (Id int IDENTITY PRIMARY KEY, Net money NOT NULL, Tax AS (dbo.fnTax(Net)));", ct);
            await Exec(c, "CREATE VIEW dbo.vBase AS SELECT Id, Net FROM dbo.Sale;", ct);
            await Exec(c, "CREATE VIEW dbo.vTop AS SELECT Id FROM dbo.vBase;", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default,
            targetDefaultCollation: target.DefaultCollation, dependencies: source.Dependencies);

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "ordered script failed to apply");

        // Converged: re-read target, no create-validated drift remains.
        Database after = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        ComparisonResult re = new ComparisonEngine().Compare(source, after, ComparisonOptions.Default);
        re.Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Where(d => d.Identity.Kind is "Table" or "View" or "Function")
            .Should().BeEmpty();
    }

    private static async Task Create(string conn, string db, CancellationToken ct)
    {
        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await Exec(c, $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", ct);
    }
    private static string Cat(string conn, string db) =>
        new SqlConnectionStringBuilder(conn) { InitialCatalog = db }.ConnectionString;
    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests --filter "FullyQualifiedName~DependencyRoundTripTests"`
Expected: PASS (Docker required). If it FAILS with an apply error like "Invalid object name 'dbo.fnTax'", the topo order is wrong — revisit Task 6.

- [ ] **Step 4: Commit**

```bash
git add tests/DbDelta.Providers.LiveDb.IntegrationTests/DependencyRoundTripTests.cs tests/DbDelta.Providers.LiveDb.IntegrationTests/DbDelta.Providers.LiveDb.IntegrationTests.csproj
git commit -m "test(livedb): cross-kind dependency round-trip applies clean (#24)"
```

---

## Task 10: Property test — output is a valid topological linearization + deterministic

**Files:**
- Create: `tests/DbDelta.Property.Tests/DependencyResolverProperties.cs`

- [ ] **Step 1: Inspect the existing property-test style**

Run: `dotnet test tests/DbDelta.Property.Tests --list-tests`
Note the `[Fact]` + `Gen.Sample` pattern used by `ComparisonEngineProperties` (FsCheck 3.0 core via samples, per the #20 commit). Mirror it.

- [ ] **Step 2: Write the property test**

```csharp
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using Xunit;

namespace DbDelta.Property.Tests;

public class DependencyResolverProperties
{
    private static ObjectIdentity Id(int n, string kind) => new("dbo", $"o{n:D3}", kind);

    [Fact]
    public void Order_is_a_valid_linearization_and_deterministic()
    {
        string[] kinds = ["Table", "View", "Function", "Sequence", "Synonym"];
        var resolver = new DependencyResolver();

        foreach (int seedCount in Gen.Choose(2, 12).Sample(1, 40))
        {
            var nodes = Enumerable.Range(0, seedCount)
                .Select(i => Id(i, kinds[i % kinds.Length])).ToArray();

            // Acyclic edges only: always point from a higher index to a lower
            // index node (i depends on j where j < i) ⇒ no cycle by construction.
            var edges = new List<DependencyEdge>();
            foreach (int i in Enumerable.Range(1, seedCount - 1))
            {
                foreach (int j in Gen.Choose(0, i - 1).Sample(1, 2).Distinct())
                {
                    edges.Add(new DependencyEdge(nodes[i], nodes[j], EdgeKind.ModuleReference));
                }
            }

            // Materialize to List so IndexOf is available (IReadOnlyList has none).
            List<ObjectIdentity> order = [.. resolver.Order(nodes, edges)];

            // 1. Every node present exactly once.
            order.Should().BeEquivalentTo(nodes);
            // 2. Every edge respected: Referenced precedes Dependent.
            foreach (DependencyEdge e in edges)
            {
                order.IndexOf(e.Referenced).Should().BeLessThan(order.IndexOf(e.Dependent));
            }
            // 3. Deterministic: same inputs (reshuffled) → same order.
            var reshuffled = nodes.Reverse().ToArray();
            resolver.Order(reshuffled, edges).Should().Equal(order);
        }
    }
}
```

- [ ] **Step 3: Run it**

Run: `dotnet test tests/DbDelta.Property.Tests --filter "FullyQualifiedName~DependencyResolverProperties"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/DbDelta.Property.Tests/DependencyResolverProperties.cs
git commit -m "test(property): dependency resolver linearization + determinism (#24)"
```

---

## Task 11: Full suite + spec close-out

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/specs/2026-05-27-kahn-dependency-resolver-design.md` (mark Done)

- [ ] **Step 1: Build everything**

Run: `dotnet build DbDelta.sln -c Debug`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Run the non-DB suites**

Run:
```bash
dotnet test tests/DbDelta.Core.UnitTests
dotnet test tests/DbDelta.ScriptGen.GoldenTests
dotnet test tests/DbDelta.Property.Tests
dotnet test tests/DbDelta.Architecture.Tests
```
Expected: all PASS. Core gains the Task 1–4 tests; goldens are 29; property gains one.

- [ ] **Step 3: Run the live suites (Docker)**

Run:
```bash
dotnet test tests/DbDelta.Providers.LiveDb.IntegrationTests
dotnet test tests/DbDelta.Cli.AcceptanceTests
```
Expected: PASS (LiveDb gains DependencyReaderTests + DependencyRoundTripTests).

- [ ] **Step 4: Verify formatting**

Run: `dotnet format DbDelta.sln --verify-no-changes`
Expected: exit 0. If it fails, run `dotnet format DbDelta.sln` and re-verify.

- [ ] **Step 5: Update CHANGELOG**

Replace the `[Unreleased]` body so it no longer lists "formal Kahn dependency resolver", and add a `## [0.15.0] — <date> — #24 dependency resolver` section summarizing: object-level Kahn topo sort, `sys.sql_expression_dependencies`-sourced edges, cross-kind correctness (computed-col→function, view→view/function, schemabound-TVF→table), FK cycles still broken by the final FK phase, empty-edge fallback preserves prior order, new tests.

- [ ] **Step 6: Mark the spec Done**

Edit the spec header `**Status:**` line to `implemented <date> · tag v0.15.0 (pending)`.

- [ ] **Step 7: Commit**

```bash
git add CHANGELOG.md docs/superpowers/specs/2026-05-27-kahn-dependency-resolver-design.md
git commit -m "docs: close out #24 dependency resolver (CHANGELOG + spec status)"
```

> Tagging `v0.15.0` and pushing are **manual** (per collaboration pattern) — do not tag or push automatically.

---

## Notes for the implementer

- **TDD throughout.** Every behaviour task writes the failing test first.
- **The two-step restructure (Task 5 then 6) is deliberate.** Task 5 must leave goldens byte-identical; if a golden changes in Task 5, the extraction was not behaviour-preserving — fix it before proceeding.
- **Empty-edge invariance is the safety net — for CREATE order.** Existing fixtures pass no `dependencies`, so their **create/alter** output must not change in Task 6 (KindRank reproduces the old order). A changed create-only golden means KindRank or the loop order drifted — reconcile before accepting. The one expected change is **drop ordering**: fixtures with `OnlyInB` pairs now drop in reverse-topological order; review those diffs and re-baseline.
- **#33 inbound-FK logic is untouched.** Sections 0.9 and 7.9 keep their positions and bookkeeping; only the create-block loops between them are reorganized. The scenario-12 parity fixture + #33 unit tests guard this.
- **Docker** is required for Tasks 8–9; CI's `linux-integration-tests` job already provides it.
