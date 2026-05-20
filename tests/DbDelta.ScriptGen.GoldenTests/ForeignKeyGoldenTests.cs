using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class ForeignKeyGoldenTests
{
    [Fact]
    public Task Foreign_key_with_cascade_delete()
    {
        var fk = new ForeignKey(
            Name: "FK_Order_Customer",
            Columns: ["CustomerId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Customer",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.Cascade,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);

        string ddl = new ForeignKeyScriptEmitter().EmitAdd("dbo", "Order", fk);
        return Verify(ddl);
    }

    [Fact]
    public Task Foreign_key_disabled_and_not_for_replication()
    {
        var fk = new ForeignKey(
            Name: "FK_Order_Customer",
            Columns: ["CustomerId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Customer",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.SetNull,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: true,
            IsNotForReplication: true);

        string ddl = new ForeignKeyScriptEmitter().EmitAdd("dbo", "Order", fk);
        return Verify(ddl);
    }
}
