using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class IndexTests
{
    [Fact]
    public void Index_has_ordered_key_columns_and_optional_included_columns()
    {
        TableIndex ix = new(
            Name: "IX_Order_CustomerId_OrderDate",
            IsUnique: false,
            IsClustered: false,
            FilterExpression: null,
            KeyColumns: [
                new IndexColumn("CustomerId", IsDescending: false),
                new IndexColumn("OrderDate",  IsDescending: true),
            ],
            IncludedColumns: ["TotalAmount"]);

        ix.Name.Should().Be("IX_Order_CustomerId_OrderDate");
        ix.KeyColumns.Should().HaveCount(2);
        ix.KeyColumns[1].IsDescending.Should().BeTrue();
        ix.IncludedColumns.Should().Equal("TotalAmount");
        ix.FilterExpression.Should().BeNull();
    }

    [Fact]
    public void Filtered_index_carries_its_predicate()
    {
        TableIndex ix = new(
            Name: "IX_Order_Active",
            IsUnique: false,
            IsClustered: false,
            FilterExpression: "([IsDeleted]=(0))",
            KeyColumns: [new IndexColumn("Id", false)],
            IncludedColumns: []);

        ix.FilterExpression.Should().Be("([IsDeleted]=(0))");
    }
}
