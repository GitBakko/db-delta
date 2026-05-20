using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class TableGoldenTests
{
    [Fact]
    public Task Create_full_customer_table()
    {
        var table = new Table("dbo", "Customer",
        [
            new Column("Id",    "int",           false, 1, isIdentity: true),
            new Column("Name",  "nvarchar(100)", false, 2),
            new Column("Email", "nvarchar(200)", true,  3, defaultExpression: "('unknown')"),
        ]);
        var pair = new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null);
        string sql = new TableScriptEmitter().Emit(pair);
        return Verifier.Verify(sql);
    }
}
