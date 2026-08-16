using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// The generator refuses rather than writing SQL that would destroy an index it
/// cannot write back. The worst case is the temp-table rebuild: it DROPs the
/// original table, and <see cref="ScriptGenerator"/> restores the indexes by
/// re-creating the full source-side set — so an index with no CREATE is an
/// index the rebuild deletes and nothing restores, under a success banner.
/// </summary>
/// <remarks>
/// Refusing happens during generation, which runs to completion before a single
/// batch is sent, so nothing has touched the server when it throws.
/// </remarks>
public class UnscriptableIndexRefusalTests
{
    private static readonly ScriptGenerator Sut = new();

    private static TableIndex Columnstore(string name, params string[] columns) => new(
        Name: name,
        IsUnique: false,
        IsClustered: false,
        FilterExpression: null,
        KeyColumns: [.. columns.Select(c => new IndexColumn(c, false))],
        IncludedColumns: [],
        DataCompression: "COLUMNSTORE",
        TypeDesc: "NONCLUSTERED COLUMNSTORE");

    private static TableIndex Rowstore(string name, string column) => new(
        Name: name,
        IsUnique: false,
        IsClustered: false,
        FilterExpression: null,
        KeyColumns: [new IndexColumn(column, false)],
        IncludedColumns: [],
        TypeDesc: "NONCLUSTERED");

    /// <summary>Plain int Id on the target, IDENTITY on the source: a rebuild.</summary>
    private static ComparisonResult RebuildPair(
        IReadOnlyList<TableIndex> sourceIndexes, IReadOnlyList<TableIndex> targetIndexes)
    {
        Column plainId = new("Id", "int", isNullable: false, ordinal: 1);
        Column identityId = new("Id", "int", isNullable: false, ordinal: 1,
            isIdentity: true, identitySeed: 1, identityIncrement: 1);
        Column importo = new("Importo", "decimal(18,2)", isNullable: false, ordinal: 2);

        Table oldT = new("dbo", "Fatti", [plainId, importo], [], targetIndexes);
        Table newT = new("dbo", "Fatti", [identityId, importo], [], sourceIndexes);
        return new([new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT)]);
    }

    [Fact]
    public void A_rebuild_of_a_table_carrying_a_columnstore_index_is_refused()
    {
        ComparisonResult result = RebuildPair(
            sourceIndexes: [Columnstore("NCCI_Fatti", "Importo")],
            targetIndexes: [Columnstore("NCCI_Fatti", "Importo")]);

        Action act = () => Sut.Generate(result);

        UnscriptableIndexException ex = act.Should().Throw<UnscriptableIndexException>().Which;
        ex.IndexName.Should().Be("NCCI_Fatti");
        ex.TypeDesc.Should().Be("NONCLUSTERED COLUMNSTORE");
        ex.Message.Should().Contain("[dbo].[Fatti]").And.Contain("NCCI_Fatti");
    }

    /// <summary>
    /// The emitter on its own, with no index pass behind it. The guard in
    /// <c>IndexScriptEmitter</c> catches the same case when the whole generator
    /// runs, which is exactly why this test exists: it is the only one that
    /// fails if the rebuild stops checking for itself, and the rebuild is the
    /// statement that does the destroying.
    /// </summary>
    [Fact]
    public void The_table_emitter_alone_refuses_the_rebuild_before_writing_the_DROP()
    {
        ComparisonResult result = RebuildPair(
            sourceIndexes: [Columnstore("NCCI_Fatti", "Importo")],
            targetIndexes: [Columnstore("NCCI_Fatti", "Importo")]);

        Action act = () => new TableScriptEmitter().Emit(result.Differences[0]);

        act.Should().Throw<UnscriptableIndexException>();
    }

    /// <summary>
    /// The same shape with an ordinary index still deploys. Without this the
    /// refusal could be a blanket "rebuilds are off" and the suite would agree.
    /// </summary>
    [Fact]
    public void The_same_rebuild_with_a_rowstore_index_still_generates()
    {
        ComparisonResult result = RebuildPair(
            sourceIndexes: [Rowstore("IX_Fatti_Importo", "Importo")],
            targetIndexes: [Rowstore("IX_Fatti_Importo", "Importo")]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP TABLE [dbo].[Fatti];");
        sql.Should().Contain("CREATE NONCLUSTERED INDEX [IX_Fatti_Importo] ON [dbo].[Fatti]",
            "the rebuild drops the table, so this pass is what puts the index back");
    }

    /// <summary>
    /// Not only the rebuild path: any CREATE the generator would have to write
    /// for a non-rowstore index stops the run.
    /// </summary>
    [Fact]
    public void A_brand_new_table_carrying_a_columnstore_index_is_refused()
    {
        Table t = new("dbo", "Fatti",
            [new Column("Importo", "decimal(18,2)", isNullable: false, ordinal: 1)],
            [], [Columnstore("NCCI_Fatti", "Importo")]);
        ComparisonResult result = new(
            [new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null)]);

        Action act = () => Sut.Generate(result);

        act.Should().Throw<UnscriptableIndexException>();
    }

    [Fact]
    public void An_index_delta_that_would_create_a_columnstore_is_refused()
    {
        Column importo = new("Importo", "decimal(18,2)", isNullable: false, ordinal: 1);
        Table src = new("dbo", "Fatti", [importo], [], [Columnstore("NCCI_Fatti", "Importo")]);
        Table tgt = new("dbo", "Fatti", [importo], [], []);
        ComparisonResult result = new(
            [new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        Action act = () => Sut.Generate(result);

        act.Should().Throw<UnscriptableIndexException>();
    }

    /// <summary>
    /// The deliberate exemption: <c>DROP INDEX</c> is valid for every index
    /// type, and the source no longer has this one. Refusing here would block a
    /// convergence the target can complete perfectly well.
    /// </summary>
    [Fact]
    public void Dropping_a_columnstore_the_source_no_longer_has_is_allowed()
    {
        Column importo = new("Importo", "decimal(18,2)", isNullable: false, ordinal: 1);
        Table src = new("dbo", "Fatti", [importo], [], []);
        Table tgt = new("dbo", "Fatti", [importo], [], [Columnstore("NCCI_Fatti", "Importo")]);
        ComparisonResult result = new(
            [new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt)]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP INDEX [NCCI_Fatti] ON [dbo].[Fatti];");
    }

    /// <summary>
    /// The diff viewer reads this body, it never runs. An index with no CREATE
    /// still has to appear or the pane shows two identical texts for a table the
    /// grid calls Different — the round-16 empty-diff bug, from the other end.
    /// </summary>
    [Fact]
    public void The_diff_body_names_a_columnstore_instead_of_throwing_or_hiding_it()
    {
        Table t = new("dbo", "Fatti",
            [new Column("Importo", "decimal(18,2)", isNullable: false, ordinal: 1)],
            [], [Columnstore("NCCI_Fatti", "Importo")]);

        string body = TableScriptEmitter.GenerateFullTableBody(t);

        body.Should().Contain("-- NONCLUSTERED COLUMNSTORE INDEX [NCCI_Fatti] ON [dbo].[Fatti] ([Importo])");
        body.Should().NotContain("CREATE NONCLUSTERED INDEX [NCCI_Fatti]",
            "writing that would be valid SQL for a different index");
    }
}
