using DbDelta.Core.ObjectModel;
using FsCheck;
using FsCheck.Fluent;

namespace DbDelta.PropertyTests.Arbitraries;

/// <summary>
/// FsCheck arbitraries that generate bounded, well-formed
/// <see cref="Database"/> snapshots so the property tests stay fast and
/// deterministic. The generators stay intentionally narrow — small
/// schemas (≤ 12 tables, ≤ 4 columns each, ≤ 4 views / procs / functions /
/// sequences) — because the goal is to find invariant violations, not
/// stress-test the engine (that's `DbDelta.Benchmarks`).
/// </summary>
public static class SchemaArbitraries
{
    private static readonly string[] _schemas = ["dbo", "stage", "audit"];
    private static readonly string[] _dataTypes =
    [
        "int", "bigint", "nvarchar(50)", "nvarchar(200)", "decimal(18,2)",
        "datetime2(3)", "bit",
    ];

    public static Arbitrary<Database> Database() => Gen.Sized(size =>
    {
        // Keep the per-database object count modest; we want hundreds of
        // properties to run, not a handful of long ones.
        int cap = Math.Min(12, Math.Max(1, size));
        return
            from tables in TableGen().ListOf(cap).Select(MakeUnique)
            from views in ViewGen().ListOf(Math.Min(4, cap)).Select(MakeUnique)
            from procs in ProcedureGen().ListOf(Math.Min(4, cap)).Select(MakeUnique)
            from funcs in FunctionGen().ListOf(Math.Min(4, cap)).Select(MakeUnique)
            from seqs in SequenceGen().ListOf(Math.Min(4, cap)).Select(MakeUnique)
            select new Database(
                Name: "BenchDb",
                Schemas: [.. _schemas.Select(s => new Schema(s))],
                Tables: tables,
                Views: views,
                Procedures: procs,
                Functions: funcs,
                Triggers: [])
            {
                Sequences = seqs,
                DefaultCollation = "Latin1_General_CI_AS",
            };
    }).ToArbitrary();

    private static Gen<Table> TableGen() =>
        from schema in Gen.Elements(_schemas)
        from suffix in Gen.Choose(0, 9999).Select(i => i.ToString("D4"))
        from cols in Gen.NonEmptyListOf(ColumnGen()).Select(list => DedupByName(list).Take(4).ToArray())
        let name = $"T_{suffix}"
        select new Table(
            schema,
            name,
            [.. cols.Select((c, idx) => c with { Ordinal = idx + 1 })],
            [new PrimaryKey($"PK_{name}", [cols[0].Name], IsClustered: true)],
            []);

    private static Gen<Column> ColumnGen() =>
        from name in Gen.Choose(0, 999).Select(i => $"C_{i:D3}")
        from dt in Gen.Elements(_dataTypes)
        from nullable in Gen.Elements(true, false)
        select new Column(name, dt, nullable, ordinal: 0);

    private static Gen<View> ViewGen() =>
        from schema in Gen.Elements(_schemas)
        from suffix in Gen.Choose(0, 9999).Select(i => i.ToString("D4"))
        from marker in Gen.Choose(1, 100)
        let name = $"v_{suffix}"
        select new View(schema, name,
            $"CREATE VIEW [{schema}].[{name}] AS SELECT {marker} AS Marker;",
            IsEncrypted: false);

    private static Gen<StoredProcedure> ProcedureGen() =>
        from schema in Gen.Elements(_schemas)
        from suffix in Gen.Choose(0, 9999).Select(i => i.ToString("D4"))
        from top in Gen.Choose(1, 100)
        let name = $"usp_{suffix}"
        select new StoredProcedure(schema, name,
            $"CREATE PROCEDURE [{schema}].[{name}] AS SELECT TOP ({top}) 1 AS X;",
            IsEncrypted: false);

    private static Gen<Function> FunctionGen() =>
        from schema in Gen.Elements(_schemas)
        from suffix in Gen.Choose(0, 9999).Select(i => i.ToString("D4"))
        from mul in Gen.Choose(1, 50)
        let name = $"fn_{suffix}"
        select new Function(schema, name,
            $"CREATE FUNCTION [{schema}].[{name}] (@x int) RETURNS int AS BEGIN RETURN @x * {mul}; END",
            IsEncrypted: false,
            FunctionKind: FunctionKind.Scalar);

    private static Gen<Sequence> SequenceGen() =>
        from schema in Gen.Elements(_schemas)
        from suffix in Gen.Choose(0, 9999).Select(i => i.ToString("D4"))
        from start in Gen.Choose(1, 10_000)
        select new Sequence(
            Schema: schema,
            Name: $"seq_{suffix}",
            DataType: "bigint",
            StartValue: start,
            Increment: 1,
            MinValue: long.MinValue,
            MaxValue: long.MaxValue,
            IsCycling: false,
            IsCached: true,
            CacheSize: 20);

    // ── Helpers ─────────────────────────────────────────────────────────

    private static IReadOnlyList<Table> MakeUnique(IEnumerable<Table> tables) =>
        [.. tables.GroupBy(t => (t.Schema, t.Name)).Select(g => g.First())];

    private static IReadOnlyList<View> MakeUnique(IEnumerable<View> views) =>
        [.. views.GroupBy(v => (v.Schema, v.Name)).Select(g => g.First())];

    private static IReadOnlyList<StoredProcedure> MakeUnique(IEnumerable<StoredProcedure> procs) =>
        [.. procs.GroupBy(p => (p.Schema, p.Name)).Select(g => g.First())];

    private static IReadOnlyList<Function> MakeUnique(IEnumerable<Function> funcs) =>
        [.. funcs.GroupBy(f => (f.Schema, f.Name)).Select(g => g.First())];

    private static IReadOnlyList<Sequence> MakeUnique(IEnumerable<Sequence> seqs) =>
        [.. seqs.GroupBy(s => (s.Schema, s.Name)).Select(g => g.First())];

    private static IEnumerable<Column> DedupByName(IEnumerable<Column> cols) =>
        cols.GroupBy(c => c.Name).Select(g => g.First());
}
