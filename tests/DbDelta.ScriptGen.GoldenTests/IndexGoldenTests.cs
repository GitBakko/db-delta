using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class IndexGoldenTests
{
    [Fact]
    public Task Create_nonclustered_index_with_included_columns_and_filter()
    {
        var ix = new TableIndex(
            Name: "IX_Order_Active",
            IsUnique: false,
            IsClustered: false,
            FilterExpression: "([IsDeleted]=(0))",
            KeyColumns:
            [
                new IndexColumn("CustomerId", false),
                new IndexColumn("OrderDate",  true),
            ],
            IncludedColumns: ["TotalAmount"]);

        string ddl = new IndexScriptEmitter().EmitCreate("dbo", "Order", ix);
        return Verify(ddl);
    }

    [Fact]
    public Task Drop_index_emits_drop_statement()
    {
        var ix = new TableIndex("IX_Foo", false, false, null,
            [new IndexColumn("Bar", false)], []);

        string ddl = new IndexScriptEmitter().EmitDrop("dbo", "Order", ix);
        return Verify(ddl);
    }
}
