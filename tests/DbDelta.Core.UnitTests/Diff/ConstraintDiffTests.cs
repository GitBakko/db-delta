using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ConstraintDiffTests
{
    private static Database DbWithTable(Table t) =>
        new("X", [new Schema("dbo")], [t]);

    private static Table TableWith(params Constraint[] constraints) =>
        new("dbo", "Customer",
            Columns: [new Column("Id", "int", false, 1)],
            Constraints: constraints,
            Indexes: []);

    [Fact]
    public void Identical_PK_yields_Identical()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));
        Database b = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Should().ContainSingle().Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Different_PK_column_order_yields_Different()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id", "TenantId"], true)));
        Database b = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["TenantId", "Id"], true)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Missing_PK_on_target_yields_Different()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));
        Database b = DbWithTable(TableWith());

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Different_FK_referenced_table_yields_Different()
    {
        ForeignKey fkA = new("FK", ["CustomerId"], "dbo", "Customer", ["Id"],
            ReferentialAction.NoAction, ReferentialAction.NoAction, false, false);
        ForeignKey fkB = new("FK", ["CustomerId"], "dbo", "Client", ["Id"],
            ReferentialAction.NoAction, ReferentialAction.NoAction, false, false);

        Database a = DbWithTable(TableWith(fkA));
        Database b = DbWithTable(TableWith(fkB));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Check_constraint_expression_change_yields_Different()
    {
        Database a = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=0)", false, false)));
        Database b = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=1)", false, false)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void IgnoreKeys_option_skips_PK_diff()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK", ["Id"], true)));
        Database b = DbWithTable(TableWith());

        ComparisonResult r = new ComparisonEngine()
            .Compare(a, b, ComparisonOptions.Default | ComparisonOptions.IgnoreKeys);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }
}
