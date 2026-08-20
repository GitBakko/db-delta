using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// An index on a view was invisible, not merely unscriptable: two databases
/// differing only by one compared Identical, and a comparison that cannot see a
/// difference is worse than one that refuses to write it.
/// </summary>
/// <remarks>
/// The census declared it — <c>INDEX_ON_VIEW</c> — which is the only reason
/// this was medium and not high. Declaring a blind spot is not the same as not
/// having one.
/// </remarks>
public class IndexedViewTests
{
    private const string Body = "CREATE VIEW [dbo].[VwTotali] WITH SCHEMABINDING AS SELECT Id, COUNT_BIG(*) AS N FROM [dbo].[Ordini] GROUP BY Id";

    private static TableIndex Ix(string name, bool clustered = true, bool unique = true) =>
        new(name, IsUnique: unique, IsClustered: clustered,
            FilterExpression: null,
            KeyColumns: [new IndexColumn("Id", IsDescending: false)],
            IncludedColumns: [],
            DataCompression: null, TypeDesc: clustered ? "CLUSTERED" : "NONCLUSTERED");

    private static View Vw(params TableIndex[] indexes) =>
        new("dbo", "VwTotali", Body, IsEncrypted: false) { Indexes = indexes };

    private static ComparisonResult Compare(View a, View b) =>
        new ComparisonEngine().Compare(
            new Database("d", [], []) { Views = [a] },
            new Database("d", [], []) { Views = [b] },
            ComparisonOptions.Default);

    [Fact]
    public void A_view_that_gained_an_index_is_not_identical()
    {
        Compare(Vw(Ix("IX_VwTotali")), Vw())
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void A_view_whose_index_changed_is_not_identical()
    {
        Compare(Vw(Ix("IX_VwTotali", clustered: true)), Vw(Ix("IX_VwTotali", clustered: false)))
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Two_views_with_the_same_indexes_stay_identical()
    {
        // The negative control: seeing indexes must not make every view differ.
        Compare(Vw(Ix("IX_VwTotali")), Vw(Ix("IX_VwTotali")))
            .Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void A_new_view_brings_its_indexes_with_it()
    {
        ComparisonResult r = new ComparisonEngine().Compare(
            new Database("d", [], []) { Views = [Vw(Ix("IX_VwTotali"))] },
            new Database("d", [], []),
            ComparisonOptions.Default);

        string sql = new ScriptGenerator().Generate(r);

        // CREATE OR ALTER, not CREATE: idempotency, an alignment the owner kept
        // on purpose when the cosmetic Redgate parity was applied.
        sql.Should().Contain("VIEW [dbo].[VwTotali]");
        sql.Should().Contain("CREATE UNIQUE CLUSTERED INDEX [IX_VwTotali] ON [dbo].[VwTotali] ([Id] ASC)");
        sql.IndexOf("VIEW [dbo].[VwTotali]", StringComparison.Ordinal)
            .Should().BeLessThan(sql.IndexOf("CREATE UNIQUE CLUSTERED INDEX", StringComparison.Ordinal),
                "an index on a view cannot exist before the view does");
    }

    [Fact]
    public void A_dropped_view_needs_no_index_statements()
    {
        // The other negative control: DROP VIEW takes its indexes with it, and
        // an extra DROP INDEX against an object about to disappear is noise at
        // best and an error at worst.
        ComparisonResult r = new ComparisonEngine().Compare(
            new Database("d", [], []),
            new Database("d", [], []) { Views = [Vw(Ix("IX_VwTotali"))] },
            ComparisonOptions.Default);

        new ScriptGenerator().Generate(r).Should().NotContain("INDEX");
    }
}
