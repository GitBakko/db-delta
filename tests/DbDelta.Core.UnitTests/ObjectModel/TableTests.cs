using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class TableTests
{
    [Fact]
    public void Identity_combines_schema_and_name_case_sensitively_by_default()
    {
        var t1 = new Table("dbo", "Customer", []);
        var t2 = new Table("dbo", "Customer", []);
        var t3 = new Table("dbo", "customer", []);

        t1.Identity.Should().Be(t2.Identity);
        t1.Identity.Should().NotBe(t3.Identity);
    }

    [Fact]
    public void Table_holds_ordered_columns()
    {
        Column[] cols = [
            new Column("Id", "int", isNullable: false, ordinal: 1),
            new Column("Name", "nvarchar(100)", isNullable: false, ordinal: 2)
        ];

        var table = new Table("dbo", "Customer", cols);

        table.Columns.Should().HaveCount(2);
        table.Columns[0].Name.Should().Be("Id");
        table.Columns[1].Name.Should().Be("Name");
    }
}
