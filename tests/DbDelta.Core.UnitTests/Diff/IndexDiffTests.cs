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

    private static Table TableWith(params TableIndex[] indexes) =>
        new("dbo", "Customer",
            Columns: [new Column("Id", "int", false, 1)],
            Constraints: [],
            Indexes: indexes);

    private static TableIndex Ix(string name, params string[] keys) =>
        new(name, false, false, null,
            [.. keys.Select(k => new IndexColumn(k, false))],
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
        TableIndex a1 = new("IX1", false, false, null,
            [new IndexColumn("Name", false)], ["Email"]);
        TableIndex b1 = new("IX1", false, false, null,
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
