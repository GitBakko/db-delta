using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;

namespace DbDelta.ScriptGen.GoldenTests;

public class TableWithConstraintsGoldenTests
{
    [Fact]
    public Task Create_table_with_clustered_PK_and_UQ()
    {
        var t = new Table(
            Schema: "dbo",
            Name: "Customer",
            Columns:
            [
                new Column("Id",    "int",            false, 1, isIdentity: true, identitySeed: 1, identityIncrement: 1),
                new Column("Email", "nvarchar(200)",  false, 2),
                new Column("Name",  "nvarchar(100)",  false, 3),
            ],
            Constraints:
            [
                new PrimaryKey("PK_Customer", ["Id"], IsClustered: true),
                new UniqueConstraint("UQ_Customer_Email", ["Email"], IsClustered: false),
            ],
            Indexes: []);
        var pair = new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null);

        string ddl = new TableScriptEmitter().Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Create_table_with_check_and_default_constraints()
    {
        var t = new Table(
            Schema: "dbo",
            Name: "Person",
            Columns:
            [
                new Column("Id", "int", false, 1, isIdentity: true, identitySeed: 1, identityIncrement: 1),
                new Column("Age", "int", false, 2),
                new Column("CreatedAt", "datetime2(7)", false, 3, defaultExpression: "(sysutcdatetime())"),
            ],
            Constraints:
            [
                new PrimaryKey("PK_Person", ["Id"], IsClustered: true),
                new CheckConstraint("CK_Person_Age", "([Age] >= 0)", IsDisabled: false, IsNotForReplication: false),
                new DefaultConstraint("DF_Person_CreatedAt", "CreatedAt", "(sysutcdatetime())"),
            ],
            Indexes: []);
        var pair = new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null);

        string ddl = new TableScriptEmitter().Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Create_table_with_persisted_computed_column()
    {
        var t = new Table(
            Schema: "dbo",
            Name: "Person",
            Columns:
            [
                new Column("Id", "int", false, 1, isIdentity: true, identitySeed: 1, identityIncrement: 1),
                new Column("FirstName", "nvarchar(50)", false, 2),
                new Column("LastName",  "nvarchar(50)", false, 3),
                new Column(
                    name: "FullName",
                    dataType: "nvarchar(101)",
                    isNullable: false,
                    ordinal: 4,
                    computedExpression: "([FirstName]+N' '+[LastName])",
                    isPersistedComputed: true),
            ],
            Constraints: [new PrimaryKey("PK_Person", ["Id"], true)],
            Indexes: []);
        var pair = new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null);

        string ddl = new TableScriptEmitter().Emit(pair);
        return Verify(ddl);
    }

    [Fact]
    public Task Alter_table_add_new_PK_and_UQ_constraints()
    {
        Column[] cols =
        [
            new("Id",    "int",           false, 1),
            new("Email", "nvarchar(200)", false, 2),
        ];

        var newT = new Table(
            Schema: "dbo",
            Name: "Customer",
            Columns: cols,
            Constraints:
            [
                new PrimaryKey("PK_Customer", ["Id"], true),
                new UniqueConstraint("UQ_Customer_Email", ["Email"], false),
            ],
            Indexes: []);

        var oldT = new Table("dbo", "Customer", cols, [], []);

        var pair = new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT);
        string ddl = new TableScriptEmitter().Emit(pair);
        return Verify(ddl);
    }
}
