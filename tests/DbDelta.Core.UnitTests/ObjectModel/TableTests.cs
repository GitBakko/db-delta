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

    [Fact]
    public void Table_can_hold_a_primary_key_and_indexes()
    {
        Table t = new(
            Schema: "dbo",
            Name: "Customer",
            Columns: [new Column("Id", "int", false, 1, isIdentity: true)],
            Constraints: [new PrimaryKey("PK_Customer", ["Id"], IsClustered: true)],
            Indexes:
            [
                new TableIndex(
                    Name: "IX_Customer_Name",
                    IsUnique: false,
                    IsClustered: false,
                    FilterExpression: null,
                    KeyColumns: [new IndexColumn("Name", false)],
                    IncludedColumns: [])
            ]);

        t.Constraints.Should().ContainSingle(c => c.Kind == "PrimaryKey");
        t.Indexes.Should().ContainSingle(i => i.Name == "IX_Customer_Name");
    }

    [Fact]
    public void Tables_constructed_with_legacy_three_arg_constructor_have_empty_collections()
    {
        Table t = new("dbo", "Legacy", [new Column("Id", "int", false, 1)]);

        t.Constraints.Should().BeEmpty();
        t.Indexes.Should().BeEmpty();
    }
}
