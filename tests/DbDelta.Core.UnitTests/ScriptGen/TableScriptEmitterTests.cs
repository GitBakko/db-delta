using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

public class TableScriptEmitterTests
{
    [Fact]
    public void Emit_create_for_OnlyInA_table()
    {
        var table = new Table("dbo", "Customer",
        [
            new Column("Id",    "int",           false, 1, isIdentity: true),
            new Column("Name",  "nvarchar(100)", false, 2),
            new Column("Email", "nvarchar(200)", true,  3),
        ]);
        var pair = new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null);

        string sql = new TableScriptEmitter().Emit(pair).Trim();

        sql.Should().StartWith("CREATE TABLE [dbo].[Customer]");
        sql.Should().Contain("[Id] int IDENTITY NOT NULL");
        sql.Should().Contain("[Name] nvarchar(100) NOT NULL");
        sql.Should().Contain("[Email] nvarchar(200) NULL");
    }

    [Fact]
    public void Emit_drop_for_OnlyInB_table()
    {
        var table = new Table("dbo", "Legacy",
        [
            new Column("Id", "int", false, 1),
        ]);
        var pair = new DifferencePair(table.Identity, DifferenceStatus.OnlyInB, null, table);

        string sql = new TableScriptEmitter().Emit(pair).Trim();

        sql.Should().Be("DROP TABLE [dbo].[Legacy];");
    }

    [Fact]
    public void Emit_alter_add_column_when_only_columns_added()
    {
        var oldT = new Table("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
        ]);
        var newT = new Table("dbo", "Customer",
        [
            new Column("Id",    "int",           false, 1),
            new Column("Email", "nvarchar(200)", true,  2),
        ]);
        var pair = new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT);

        string sql = new TableScriptEmitter().Emit(pair).Trim();

        sql.Should().Contain("ALTER TABLE [dbo].[Customer] ADD [Email] nvarchar(200) NULL;");
        sql.Should().NotContain("DROP TABLE");
    }
}
