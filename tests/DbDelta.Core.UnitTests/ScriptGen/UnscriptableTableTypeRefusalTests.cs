using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// The generator refuses rather than writing a disk-based table type where the
/// source has a memory-optimized one. The failure it prevents is not invalid
/// SQL: the rewrite RUNS, and leaves a type of the same name backed by the
/// wrong storage engine, under a success banner.
/// </summary>
/// <remarks>
/// <para>
/// Every fact about the shape below was measured on
/// <c>mssql/server:2022-latest</c> against a database with a
/// MEMORY_OPTIMIZED_DATA filegroup, not taken from documentation. The two that
/// decide the design: a memory-optimized type may key itself on a plain range
/// index, whose <c>sys.indexes</c> row is byte-for-byte a disk-based type's
/// (<c>type = 2</c>, <c>type_desc = 'NONCLUSTERED'</c>), so the index shape
/// cannot discriminate; and a HASH index on a disk-based type is rejected
/// outright (Msg 1750), so <c>is_memory_optimized</c> is both necessary and
/// sufficient.
/// </para>
/// <para>
/// Refusing happens during generation, which runs to completion before a single
/// batch is sent, so nothing has touched the server when it throws.
/// </para>
/// </remarks>
public class UnscriptableTableTypeRefusalTests
{
    private static readonly ScriptGenerator Sut = new();

    /// <summary>
    /// A type whose columns and keys are identical whichever engine backs it —
    /// the point being that nothing but the flag tells the two apart.
    /// </summary>
    private static TableTypeUdt Tvp(bool memoryOptimized) => new("dbo", "OrderTvp",
    [
        new Column("Id", "int", isNullable: false, ordinal: 1),
        new Column("Code", "nvarchar(50)", isNullable: false, ordinal: 2),
    ])
    {
        // A range PK, exactly as the probe's MoRange carried: reading it back
        // gives NONCLUSTERED on both engines.
        Keys =
        [
            new TableIndex("PK__TT_OrderTvp__A1", IsUnique: true, IsClustered: false,
                FilterExpression: null,
                KeyColumns: [new IndexColumn("Id", IsDescending: false)],
                IncludedColumns: [])
            {
                IsPrimaryKey = true,
            },
        ],
        IsMemoryOptimized = memoryOptimized,
    };

    [Fact]
    public void A_memory_optimized_table_type_that_only_the_source_has_is_refused()
    {
        TableTypeUdt src = Tvp(memoryOptimized: true);
        ComparisonResult result = new(
            [new DifferencePair(src.Identity, DifferenceStatus.OnlyInA, src, null)]);

        Action act = () => Sut.Generate(result);

        UnscriptableTableTypeException ex =
            act.Should().Throw<UnscriptableTableTypeException>().Which;
        ex.Schema.Should().Be("dbo");
        ex.Name.Should().Be("OrderTvp");
        ex.Message.Should().Contain("memory-optimized").And.Contain("[dbo].[OrderTvp]");
    }

    [Fact]
    public void The_DROP_plus_CREATE_of_a_changed_memory_optimized_table_type_is_refused()
    {
        // The worse half of the same defect: a table type has no ALTER, so a
        // Different verdict DROPs the target and re-creates it. Left unrefused,
        // the run deletes a memory-optimized type and puts a disk-based one
        // back — a loss no second run repairs, because the re-read then agrees.
        TableTypeUdt src = Tvp(memoryOptimized: true);
        TableTypeUdt tgt = Tvp(memoryOptimized: false);
        ComparisonResult result = new(
            [new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        Action act = () => Sut.Generate(result);

        act.Should().Throw<UnscriptableTableTypeException>();
    }

    [Fact]
    public void The_emitter_refuses_on_its_own_before_any_generator_is_involved()
    {
        // The guard belongs to the emitter, not to the generator's dispatch, so
        // the diff pane and any future caller inherit it. Proven at both levels
        // because a guard only the generator calls is a guard the next caller
        // forgets.
        Action act = () => new TableTypeUdtScriptEmitter().EmitCreate(Tvp(memoryOptimized: true));

        act.Should().Throw<UnscriptableTableTypeException>();
    }

    [Fact]
    public void Dropping_a_memory_optimized_table_type_is_still_allowed()
    {
        // EmitDrop is exempt on purpose, exactly as it is for a non-rowstore
        // index: a target that only has to LOSE the object converges fine, and
        // refusing would strand it.
        TableTypeUdt tgt = Tvp(memoryOptimized: true);
        ComparisonResult result = new(
            [new DifferencePair(tgt.Identity, DifferenceStatus.OnlyInB, null, tgt)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP TYPE [dbo].[OrderTvp]");
    }

    [Fact]
    public void A_disk_based_table_type_of_the_same_shape_still_emits()
    {
        // The control in the negative. Without it the refusal could be firing
        // on everything and every other test here would still pass.
        TableTypeUdt src = Tvp(memoryOptimized: false);
        ComparisonResult result = new(
            [new DifferencePair(src.Identity, DifferenceStatus.OnlyInA, src, null)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE TYPE [dbo].[OrderTvp] AS TABLE");
        sql.Should().Contain("PRIMARY KEY NONCLUSTERED ([Id] ASC)");
    }

    [Fact]
    public void A_memory_optimized_source_against_a_disk_based_target_is_Different_not_Identical()
    {
        // THE test. Columns, keys and checks are identical on both sides, so
        // every other term of the comparison says equal; only the storage
        // engine differs. Were the flag left out of equality the pair would be
        // Identical, nothing would be emitted, and the refusal above would
        // never fire — the deploy would report success over a target still
        // holding the wrong kind of type. Silence, not a wrong script.
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [Tvp(true)] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [Tvp(false)] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Two_memory_optimized_table_types_that_match_are_still_Identical()
    {
        // The second control in the negative: the flag must separate the two
        // engines, not make every memory-optimized type report Different
        // against its own twin.
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [Tvp(true)] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [Tvp(true)] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void The_census_declares_what_the_comparison_does_not_examine_about_one()
    {
        // The refusal covers the deploy; it does not cover the report. Two
        // memory-optimized types differing only in which keys are HASH, or in a
        // BUCKET_COUNT, still read Identical — neither is in the model. That is
        // a declared gap rather than a hidden one.
        UnexaminedCensus.LabelFor("MEMORY_OPTIMIZED_TABLE_TYPE")
            .Should().Be("tipi tabella memory-optimized (non scrivibili; hash e bucket count non confrontati)");
    }
}
