using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ComparisonEngineTests
{
    private static Database DbWith(params Table[] tables) =>
        new("X", Schemas: [new Schema("dbo")], Tables: tables);

    [Fact]
    public void Identical_tables_produce_zero_differences()
    {
        Column[] cols = [new Column("Id", "int", false, 1)];
        Database a = DbWith(new Table("dbo", "Customer", cols));
        Database b = DbWith(new Table("dbo", "Customer", cols));

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1)
            .And.OnlyContain(d => d.Status == DifferenceStatus.Identical);
    }

    [Fact]
    public void Missing_table_on_target_yields_OnlyInA()
    {
        Column[] cols = [new Column("Id", "int", false, 1)];
        Database a = DbWith(new Table("dbo", "Customer", cols));
        Database b = DbWith();

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1);
        result.Differences[0].Status.Should().Be(DifferenceStatus.OnlyInA);
        result.Differences[0].Identity.ObjectName.Should().Be("Customer");
    }

    [Fact]
    public void Extra_table_on_target_yields_OnlyInB()
    {
        Column[] cols = [new Column("Id", "int", false, 1)];
        Database a = DbWith();
        Database b = DbWith(new Table("dbo", "Customer", cols));

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1);
        result.Differences[0].Status.Should().Be(DifferenceStatus.OnlyInB);
    }

    [Fact]
    public void Different_column_set_yields_Different()
    {
        Database a = DbWith(new Table("dbo", "Customer", [
            new Column("Id", "int", false, 1)
        ]));
        Database b = DbWith(new Table("dbo", "Customer", [
            new Column("Id", "int", false, 1),
            new Column("Email", "nvarchar(200)", true, 2)
        ]));

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().HaveCount(1);
        result.Differences[0].Status.Should().Be(DifferenceStatus.Different);
    }
}
