using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A PRIMARY KEY or UNIQUE constraint carries a direction per key column, and
/// losing it is silent all the way down.
/// </summary>
/// <remarks>
/// Measured on <c>mssql/server:2022-latest</c>: <c>PRIMARY KEY CLUSTERED
/// (A ASC, B DESC)</c> and <c>UNIQUE (C DESC, A ASC)</c> are both accepted, and
/// <c>sys.index_columns.is_descending_key</c> reports 1 for the descending
/// halves. Re-emitting the constraints the way DbDelta wrote them — names only
/// — brought every <c>is_descending_key</c> back as 0, with no error at any
/// point. The model held <c>IReadOnlyList&lt;string&gt;</c>, so the two keys
/// also compared Identical and no difference was ever reported.
/// </remarks>
public class KeyColumnDirectionTests
{
    private static readonly ScriptGenerator Sut = new();

    private static Column Col(string n, int o) => new(n, "int", isNullable: false, ordinal: o);

    private static Table WithPk(params IndexColumn[] key) =>
        new("dbo", "T", [Col("A", 1), Col("B", 2)],
            [new PrimaryKey("PK_T", key, IsClustered: true)], []);

    private static Table WithUq(params IndexColumn[] key) =>
        new("dbo", "T", [Col("A", 1), Col("B", 2)],
            [new UniqueConstraint("UQ_T", key, IsClustered: false)], []);

    private static Database Db(Table t) => new("X", [new Schema("dbo")], [t]);

    private static DifferenceStatus StatusOf(Table a, Table b) =>
        new ComparisonEngine().Compare(Db(a), Db(b), ComparisonOptions.Default)
            .Differences.Single(d => d.Identity.Kind == "Table").Status;

    private static string Create(Table t) =>
        Sut.Generate(new ComparisonResult(
            [new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null)]));

    // ── the direction reaches the script ─────────────────────────────────

    [Fact]
    public void A_descending_key_column_is_written_DESC()
    {
        Create(WithPk("A", new IndexColumn("B", IsDescending: true)))
            .Should().Contain("PRIMARY KEY CLUSTERED ([A], [B] DESC)");
    }

    [Fact]
    public void A_UNIQUE_constraint_carries_the_direction_too()
    {
        Create(WithUq(new IndexColumn("B", IsDescending: true), "A"))
            .Should().Contain("UNIQUE NONCLUSTERED ([B] DESC, [A])");
    }

    [Fact]
    public void A_rebuild_re_adds_the_key_with_its_direction()
    {
        // The loss the entry is about: the rebuild drops the named constraints
        // and writes them back after sp_rename. Written back from a name list
        // they came back all-ascending, and the index was flattened for good.
        Table oldT = new("dbo", "T",
            [Col("A", 1), Col("B", 2)],
            [new PrimaryKey("PK_T", ["A", new IndexColumn("B", IsDescending: true)], IsClustered: true)], []);
        Table newT = new("dbo", "T",
            [new Column("A", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1),
             Col("B", 2)],
            [new PrimaryKey("PK_T", ["A", new IndexColumn("B", IsDescending: true)], IsClustered: true)], []);

        string sql = Sut.Generate(new ComparisonResult(
            [new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT)]));

        sql.Should().Contain("DROP TABLE [dbo].[T];", "this is the rebuild path");
        sql.Should().Contain("[B] DESC", "the key must come back the way it went in");
    }

    // ── the silence guard: it has to reach the COMPARISON ────────────────

    [Fact]
    public void Two_primary_keys_differing_only_by_direction_are_Different()
    {
        StatusOf(WithPk("A", new IndexColumn("B", IsDescending: true)), WithPk("A", "B"))
            .Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Two_unique_constraints_differing_only_by_direction_are_Different()
    {
        StatusOf(WithUq("A", new IndexColumn("B", IsDescending: true)), WithUq("A", "B"))
            .Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void The_direction_is_compared_under_the_comparer_it_is_handed()
    {
        // Names fold the way the target's collation folds; direction is a bool
        // and never folds. Both halves in one seam, so the three call sites
        // cannot drift.
        IndexColumn[] a = ["A", new IndexColumn("B", IsDescending: true)];
        IndexColumn[] b = ["a", new IndexColumn("B", IsDescending: true)];

        IndexColumn.KeysMatch(a, b, StringComparer.OrdinalIgnoreCase).Should().BeTrue();
        IndexColumn.KeysMatch(a, b, StringComparer.Ordinal).Should().BeFalse();
        IndexColumn.KeysMatch(a, [.. a.Select(k => k with { IsDescending = false })],
            StringComparer.OrdinalIgnoreCase).Should().BeFalse();
    }

    // ── negative controls ────────────────────────────────────────────────

    [Fact]
    public void An_all_ascending_key_is_written_exactly_as_before()
    {
        // Why no golden file and no assertion moves: ASC is never spelled, so an
        // ascending key is byte-identical to what shipped. [A] and [A] ASC are
        // the same key to the server, always.
        Create(WithPk("A", "B"))
            .Should().Contain("PRIMARY KEY CLUSTERED ([A], [B])").And.NotContain("ASC");
    }

    [Fact]
    public void A_bare_column_name_still_means_ascending()
    {
        IndexColumn k = "A";

        k.Name.Should().Be("A");
        k.IsDescending.Should().BeFalse();
    }

    [Fact]
    public void Two_identical_keys_stay_Identical()
    {
        StatusOf(WithPk("A", new IndexColumn("B", IsDescending: true)),
                 WithPk("A", new IndexColumn("B", IsDescending: true)))
            .Should().Be(DifferenceStatus.Identical);
    }
}
