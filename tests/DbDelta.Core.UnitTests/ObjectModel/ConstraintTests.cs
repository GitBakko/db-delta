using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class ConstraintTests
{
    [Fact]
    public void PrimaryKey_carries_ordered_columns_and_clustered_flag()
    {
        PrimaryKey pk = new(
            Name: "PK_Customer",
            Columns: ["Id", "TenantId"],
            IsClustered: true);

        pk.Name.Should().Be("PK_Customer");
        pk.Columns.Should().Equal("Id", "TenantId");
        pk.IsClustered.Should().BeTrue();
        pk.Kind.Should().Be("PrimaryKey");
    }

    [Fact]
    public void UniqueConstraint_records_ordered_columns()
    {
        UniqueConstraint uq = new(
            Name: "UQ_Customer_Email",
            Columns: ["Email"],
            IsClustered: false);

        uq.Name.Should().Be("UQ_Customer_Email");
        uq.Columns.Should().Equal("Email");
        uq.IsClustered.Should().BeFalse();
        uq.Kind.Should().Be("UniqueConstraint");
    }

    [Fact]
    public void ForeignKey_pairs_local_columns_to_referenced_columns_with_actions()
    {
        ForeignKey fk = new(
            Name: "FK_Order_Customer",
            Columns: ["CustomerId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Customer",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.Cascade,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);

        fk.Kind.Should().Be("ForeignKey");
        fk.Columns.Should().Equal("CustomerId");
        fk.ReferencedTable.Should().Be("Customer");
        fk.ReferencedColumns.Should().Equal("Id");
        fk.OnDelete.Should().Be(ReferentialAction.Cascade);
    }

    [Fact]
    public void CheckConstraint_carries_expression_and_disabled_flag()
    {
        CheckConstraint ck = new(
            Name: "CK_Customer_Age",
            Expression: "([Age] >= 0)",
            IsDisabled: false,
            IsNotForReplication: false);

        ck.Kind.Should().Be("CheckConstraint");
        ck.Expression.Should().Be("([Age] >= 0)");
    }

    [Fact]
    public void DefaultConstraint_binds_an_expression_to_a_single_column()
    {
        DefaultConstraint df = new(
            Name: "DF_Customer_CreatedAt",
            ColumnName: "CreatedAt",
            Expression: "(sysutcdatetime())");

        df.Kind.Should().Be("DefaultConstraint");
        df.ColumnName.Should().Be("CreatedAt");
        df.Expression.Should().Be("(sysutcdatetime())");
    }
}
