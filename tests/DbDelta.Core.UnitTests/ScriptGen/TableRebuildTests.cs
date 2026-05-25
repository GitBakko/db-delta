using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Spec §3.4: identity column changes force a temp-table rebuild that
/// preserves data via SET IDENTITY_INSERT + INSERT…SELECT + sp_rename.
/// DROP COLUMN + ADD COLUMN (the previous behaviour) silently lost data.
/// </summary>
public class TableRebuildTests
{
    private static readonly ScriptGenerator Sut = new();

    [Fact]
    public void Existing_column_flipping_to_identity_triggers_table_rebuild()
    {
        // Old: Id is plain int. New: Id is IDENTITY(1,1). Without rebuild,
        // DROP COLUMN would erase every existing Id.
        Table oldT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1),
             new Column("Name", "nvarchar(100)", isNullable: false, ordinal: 2)],
            [], []);
        Table newT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1),
             new Column("Name", "nvarchar(100)", isNullable: false, ordinal: 2)],
            [], []);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE TABLE [dbo].[Customer_tmp]");
        sql.Should().Contain("SET IDENTITY_INSERT [dbo].[Customer_tmp] ON");
        sql.Should().Contain("INSERT INTO [dbo].[Customer_tmp]");
        sql.Should().Contain("FROM [dbo].[Customer]");
        sql.Should().Contain("SET IDENTITY_INSERT [dbo].[Customer_tmp] OFF");
        sql.Should().Contain("DROP TABLE [dbo].[Customer];");
        sql.Should().Contain("sp_rename");
        sql.Should().NotContain("ALTER TABLE [dbo].[Customer] DROP COLUMN [Id]");
    }

    [Fact]
    public void Identity_seed_increment_change_on_existing_column_triggers_rebuild()
    {
        Table oldT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1)],
            [], []);
        Table newT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 100, identityIncrement: 1)],
            [], []);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE TABLE [dbo].[Customer_tmp]");
        sql.Should().Contain("IDENTITY(100,1)");
        sql.Should().Contain("sp_rename");
    }

    [Fact]
    public void Brand_new_identity_column_uses_ADD_COLUMN_not_full_rebuild()
    {
        // Adding a NEW identity column to an existing table doesn't lose
        // data — ADD COLUMN is the right T-SQL.
        Table oldT = new("dbo", "Customer",
            [new Column("Name", "nvarchar(100)", isNullable: false, ordinal: 1)],
            [], []);
        Table newT = new("dbo", "Customer",
            [new Column("Name", "nvarchar(100)", isNullable: false, ordinal: 1),
             new Column("Id", "int", isNullable: false, ordinal: 2,
                isIdentity: true, identitySeed: 1, identityIncrement: 1)],
            [], []);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER TABLE [dbo].[Customer] ADD [Id] int IDENTITY(1,1) NOT NULL");
        sql.Should().NotContain("Customer_tmp");
        sql.Should().NotContain("sp_rename");
    }

    [Fact]
    public void Computed_column_expression_change_uses_drop_and_recreate_not_full_rebuild()
    {
        // Computed columns are derived, so DROP COLUMN doesn't lose data —
        // recomputing the new expression rebuilds them.
        Table oldT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1),
             new Column("FullName", "nvarchar(200)", isNullable: true, ordinal: 2,
                isIdentity: false, identitySeed: null, identityIncrement: null,
                defaultExpression: null,
                computedExpression: "([First] + ' ' + [Last])", isPersistedComputed: false)],
            [], []);
        Table newT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1),
             new Column("FullName", "nvarchar(200)", isNullable: true, ordinal: 2,
                isIdentity: false, identitySeed: null, identityIncrement: null,
                defaultExpression: null,
                computedExpression: "([Last] + ', ' + [First])", isPersistedComputed: false)],
            [], []);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER TABLE [dbo].[Customer] DROP COLUMN [FullName]");
        sql.Should().Contain("ALTER TABLE [dbo].[Customer] ADD [FullName] AS ([Last] + ', ' + [First])");
        sql.Should().NotContain("Customer_tmp");
    }

    [Fact]
    public void Rebuild_omits_computed_columns_from_the_INSERT_SELECT_clause()
    {
        // Computed columns can't be inserted — they re-derive from base
        // columns. INSERT…SELECT must skip them.
        Table oldT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1),
             new Column("First", "nvarchar(50)", isNullable: false, ordinal: 2),
             new Column("FullName", "nvarchar(100)", isNullable: true, ordinal: 3,
                isIdentity: false, identitySeed: null, identityIncrement: null,
                defaultExpression: null,
                computedExpression: "([First])", isPersistedComputed: false)],
            [], []);
        Table newT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1),
             new Column("First", "nvarchar(50)", isNullable: false, ordinal: 2),
             new Column("FullName", "nvarchar(100)", isNullable: true, ordinal: 3,
                isIdentity: false, identitySeed: null, identityIncrement: null,
                defaultExpression: null,
                computedExpression: "([First])", isPersistedComputed: false)],
            [], []);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("INSERT INTO [dbo].[Customer_tmp] ([Id], [First])");
        sql.Should().NotContain("[FullName]" + "," + " [Id]"); // FullName not part of the column list
    }

    [Fact]
    public void Rebuild_does_not_emit_SET_IDENTITY_INSERT_when_new_table_has_no_identity_column()
    {
        // Existing IDENTITY removed (new col is plain). Still requires rebuild
        // (you cannot ALTER away an IDENTITY column's flag) but no
        // SET IDENTITY_INSERT block is needed.
        Table oldT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1)],
            [], []);
        Table newT = new("dbo", "Customer",
            [new Column("Id", "int", isNullable: false, ordinal: 1)],
            [], []);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE TABLE [dbo].[Customer_tmp]");
        sql.Should().NotContain("SET IDENTITY_INSERT");
        sql.Should().Contain("INSERT INTO [dbo].[Customer_tmp] ([Id])");
    }
}
