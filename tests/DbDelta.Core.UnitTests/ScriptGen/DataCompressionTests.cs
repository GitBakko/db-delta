using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// DATA_COMPRESSION was not in the model at all, confirmed missing on
/// WebhookDeliveries in the 243 parity run: a PAGE-compressed table deployed as
/// an uncompressed one, and the next comparison said the two databases matched.
/// </summary>
public class DataCompressionTests
{
    private static Table TableWith(string? compression, params TableIndex[] indexes) =>
        new("dbo", "WebhookDeliveries", [new Column("Id", "int", false, 1)], [], indexes,
            ModifyDate: null, DataCompression: compression);

    private static TableIndex Index(string? compression) =>
        new("IX_Sent", false, false, null, [new IndexColumn("Id", false)], [], compression);

    private static Database Db(Table t) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [t], Views: [], Procedures: []);

    private static DifferenceStatus Compare(Table a, Table b) =>
        new ComparisonEngine().Compare(Db(a), Db(b), ComparisonOptions.Default)
            .Differences.Single(p => p.Identity.Kind == "Table").Status;

    // ── Diff ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_table_compressed_on_one_side_only_is_different() =>
        Compare(TableWith("PAGE"), TableWith("NONE")).Should().Be(DifferenceStatus.Different);

    [Fact]
    public void An_index_compressed_on_one_side_only_is_different() =>
        Compare(TableWith("NONE", Index("PAGE")), TableWith("NONE", Index("NONE")))
            .Should().Be(DifferenceStatus.Different);

    /// <summary>
    /// A source that never reported compression and a server that reports NONE
    /// mean the same thing. Treating them as different would report a difference
    /// on every table, and one that no script can ever make go away.
    /// </summary>
    [Theory]
    [InlineData(null, "NONE")]
    [InlineData("NONE", null)]
    [InlineData(null, null)]
    [InlineData("page", "PAGE")]
    public void Absent_and_none_mean_the_same_thing(string? left, string? right) =>
        Compare(TableWith(left), TableWith(right)).Should().Be(DifferenceStatus.Identical);

    // ── Emission ────────────────────────────────────────────────────────────

    [Fact]
    public void A_created_table_carries_its_compression()
    {
        Table t = TableWith("PAGE");
        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));

        sql.Should().Contain(") WITH (DATA_COMPRESSION = PAGE);");
    }

    [Fact]
    public void An_uncompressed_table_is_emitted_exactly_as_before()
    {
        Table t = TableWith(null);
        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));

        sql.Should().Contain(");").And.NotContain("DATA_COMPRESSION");
    }

    /// <summary>
    /// REBUILD, not a table rebuild through the copy path: the columns have not
    /// moved, only the storage.
    /// </summary>
    [Fact]
    public void Changing_a_tables_compression_rebuilds_it_in_place()
    {
        Table src = TableWith("PAGE");
        Table tgt = TableWith("NONE");

        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt));

        sql.Should().Contain(
            "ALTER TABLE [dbo].[WebhookDeliveries] REBUILD WITH (DATA_COMPRESSION = PAGE);");
    }

    [Fact]
    public void A_created_index_carries_its_compression()
    {
        string sql = new IndexScriptEmitter().EmitCreate("dbo", "WebhookDeliveries", Index("ROW"));

        sql.Should().EndWith(" WITH (DATA_COMPRESSION = ROW);");
    }

    [Fact]
    public void An_uncompressed_index_is_emitted_exactly_as_before()
    {
        string sql = new IndexScriptEmitter().EmitCreate("dbo", "WebhookDeliveries", Index(null));

        sql.Should().NotContain("DATA_COMPRESSION").And.EndWith("([Id] ASC);");
    }

    /// <summary>
    /// The index shape is unchanged, so dropping it would cost the same minutes
    /// as the rebuild AND leave the table unindexed in between.
    /// </summary>
    [Fact]
    public void Changing_only_an_indexs_compression_rebuilds_it_instead_of_dropping_it()
    {
        Table src = TableWith("NONE", Index("PAGE"));
        Table tgt = TableWith("NONE", Index("NONE"));

        string sql = new ScriptGenerator().Generate(
            new ComparisonResult([new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt)]));

        sql.Should().Contain(
            "ALTER INDEX [IX_Sent] ON [dbo].[WebhookDeliveries] REBUILD WITH (DATA_COMPRESSION = PAGE);");
        sql.Should().NotContain("DROP INDEX", "the shape did not change");
    }

    /// <summary>
    /// When the shape changed too, the index is dropped and re-created — and the
    /// CREATE has to carry the compression, or the rebuild path silently
    /// decompresses it.
    /// </summary>
    [Fact]
    public void A_reshaped_index_is_recreated_with_its_compression()
    {
        Table src = TableWith("NONE", Index("PAGE"));
        Table tgt = TableWith("NONE", new TableIndex(
            "IX_Sent", false, false, null, [new IndexColumn("Id", true)], [], "PAGE"));

        string sql = new ScriptGenerator().Generate(
            new ComparisonResult([new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt)]));

        sql.Should().Contain("DROP INDEX [IX_Sent]");
        sql.Should().Contain("WITH (DATA_COMPRESSION = PAGE);");
        sql.Should().NotContain("ALTER INDEX", "a reshaped index is recreated, not rebuilt");
    }
}
