using DbDelta.Core.ObjectModel;

namespace DbDelta.Benchmarks.Fixtures;

/// <summary>
/// Deterministically builds an in-memory <see cref="Database"/> snapshot
/// of the requested object count in a fixed mix — tables (50%), views
/// (20%), stored procedures (15%), functions (10%), sequences (5%). Two
/// snapshots built with the same count but different
/// <c>isTarget</c> flag exercise the diff engine across the classic
/// spread of edits (added column, dropped column, body change, seed
/// bump). The §6.1 perf budget targets 1k and 10k objects — both are
/// driven by the <see cref="BenchmarkDotNet.Attributes.ParamsAttribute"/>
/// on the benchmark classes.
/// </summary>
internal static class SchemaFixtureBuilder
{
    private static readonly string[] _schemas = ["dbo", "stage", "audit", "report"];

    /// <summary>
    /// Builds the "source" Database for the benchmark. Identifiers use the
    /// supplied <paramref name="seed"/> so any consumer can build the
    /// matching target via <see cref="BuildTarget"/>.
    /// </summary>
    public static Database BuildSource(int objectCount, int seed = 0) =>
        Build(objectCount, seed, isTarget: false);

    /// <summary>
    /// Builds the "target" Database — the same shape as
    /// <see cref="BuildSource"/> but with a deterministic ~50% divergence
    /// (every other object is shifted: tables lose a column, views/procs
    /// flip a body marker, sequences lose 99 from the seed). Identical
    /// objects remain in the snapshot so the engine still has to walk them
    /// to classify them — that's part of the budget.
    /// </summary>
    public static Database BuildTarget(int objectCount, int seed = 0) =>
        Build(objectCount, seed, isTarget: true);

    private static Database Build(int objectCount, int seed, bool isTarget)
    {
        // Allocate-once buckets sized for the predominant object kind to
        // avoid per-add resizing in the benchmark hot path.
        int tableCount = (int)(objectCount * 0.50);
        int viewCount = (int)(objectCount * 0.20);
        int procCount = (int)(objectCount * 0.15);
        int funcCount = (int)(objectCount * 0.10);
        int seqCount = objectCount - tableCount - viewCount - procCount - funcCount;

        List<Table> tables = new(tableCount);
        List<View> views = new(viewCount);
        List<StoredProcedure> procs = new(procCount);
        List<Function> functions = new(funcCount);
        List<Sequence> sequences = new(seqCount);

        for (int i = 0; i < tableCount; i++)
        {
            tables.Add(MakeTable(i, seed, isTarget));
        }
        for (int i = 0; i < viewCount; i++)
        {
            views.Add(MakeView(i, seed, isTarget));
        }
        for (int i = 0; i < procCount; i++)
        {
            procs.Add(MakeProcedure(i, seed, isTarget));
        }
        for (int i = 0; i < funcCount; i++)
        {
            functions.Add(MakeFunction(i, seed, isTarget));
        }
        for (int i = 0; i < seqCount; i++)
        {
            sequences.Add(MakeSequence(i, seed, isTarget));
        }

        return new Database(
            Name: isTarget ? "BenchTarget" : "BenchSource",
            Schemas: [.. _schemas.Select(s => new Schema(s))],
            Tables: tables,
            Views: views,
            Procedures: procs,
            Functions: functions,
            Triggers: [])
        {
            Sequences = sequences,
            DefaultCollation = "Latin1_General_CI_AS",
        };
    }

    private static Table MakeTable(int i, int seed, bool isTarget)
    {
        string schema = _schemas[i % _schemas.Length];
        string name = $"T_{seed}_{i:D5}";
        // Even-indexed tables diverge: source has 4 columns, target has 3.
        bool diverge = isTarget && (i % 2 == 0);
        List<Column> cols =
        [
            new Column("Id", "int", isNullable: false, ordinal: 1, isIdentity: true,
                identitySeed: 1, identityIncrement: 1),
            new Column("Name", "nvarchar(100)", isNullable: false, ordinal: 2,
                collation: "Latin1_General_CI_AS"),
            new Column("CreatedUtc", "datetime2(3)", isNullable: false, ordinal: 3,
                defaultExpression: "(sysutcdatetime())"),
        ];
        if (!diverge)
        {
            cols.Add(new Column("Notes", "nvarchar(200)", isNullable: true, ordinal: 4,
                collation: "Latin1_General_CI_AS"));
        }
        return new Table(
            schema,
            name,
            cols,
            [new PrimaryKey($"PK_{name}", ["Id"], IsClustered: true)],
            []);
    }

    private static View MakeView(int i, int seed, bool isTarget)
    {
        string schema = _schemas[i % _schemas.Length];
        string name = $"v_{seed}_{i:D5}";
        // Half the views diverge in body text — same effect as a real-world
        // module edit.
        int marker = (isTarget && i % 2 == 0) ? 2 : 1;
        string body = $"CREATE VIEW [{schema}].[{name}] AS SELECT {marker} AS Marker;";
        return new View(schema, name, body, IsEncrypted: false);
    }

    private static StoredProcedure MakeProcedure(int i, int seed, bool isTarget)
    {
        string schema = _schemas[i % _schemas.Length];
        string name = $"usp_{seed}_{i:D5}";
        int top = (isTarget && i % 2 == 0) ? 5 : 10;
        string body =
            $"CREATE PROCEDURE [{schema}].[{name}] AS SELECT TOP ({top}) 1 AS X;";
        return new StoredProcedure(schema, name, body, IsEncrypted: false);
    }

    private static Function MakeFunction(int i, int seed, bool isTarget)
    {
        string schema = _schemas[i % _schemas.Length];
        string name = $"fn_{seed}_{i:D5}";
        int multiplier = (isTarget && i % 2 == 0) ? 3 : 2;
        string body =
            $"CREATE FUNCTION [{schema}].[{name}] (@x int) RETURNS int AS BEGIN RETURN @x * {multiplier}; END";
        return new Function(schema, name, body, IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
    }

    private static Sequence MakeSequence(int i, int seed, bool isTarget)
    {
        string schema = _schemas[i % _schemas.Length];
        string name = $"seq_{seed}_{i:D5}";
        long start = (isTarget && i % 2 == 0) ? 1 : 100;
        return new Sequence(
            Schema: schema,
            Name: name,
            DataType: "bigint",
            StartValue: start,
            Increment: 1,
            MinValue: long.MinValue,
            MaxValue: long.MaxValue,
            IsCycling: false,
            IsCached: true,
            CacheSize: 20);
    }
}
