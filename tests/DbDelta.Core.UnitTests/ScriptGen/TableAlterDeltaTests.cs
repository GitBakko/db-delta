using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// M8 polish — verifies TableScriptEmitter.EmitAlter (Different status)
/// + ScriptGenerator index / FK delta on Different tables.
/// </summary>
public class TableAlterDeltaTests
{
    private static Table T(string name, params Column[] cols) =>
        new("dbo", name, cols);

    private static DifferencePair Diff(Table src, Table tgt) =>
        new(src.Identity, DifferenceStatus.Different, src, tgt);

    // ── EmitAlter — columns ────────────────────────────────────────────────

    [Fact]
    public void EmitAlter_drops_columns_present_only_on_target()
    {
        Table src = T("X", new Column("Id", "int", false, 1));
        Table tgt = T("X",
            new Column("Id", "int", false, 1),
            new Column("Obsolete", "nvarchar(50)", true, 2));

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));
        sql.Should().Contain("DROP COLUMN [Obsolete]");
    }

    [Fact]
    public void EmitAlter_adds_columns_present_only_on_source()
    {
        Table src = T("X",
            new Column("Id", "int", false, 1),
            new Column("Email", "nvarchar(200)", true, 2));
        Table tgt = T("X", new Column("Id", "int", false, 1));

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));
        sql.Should().Contain("ADD [Email] [nvarchar] (200) NULL");
    }

    [Fact]
    public void EmitAlter_alters_column_when_datatype_or_nullability_changed()
    {
        Table src = T("X", new Column("Name", "nvarchar(200)", false, 1));
        Table tgt = T("X", new Column("Name", "nvarchar(100)", true, 1));

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));
        sql.Should().Contain("ALTER COLUMN [Name] [nvarchar] (200) NOT NULL");
    }

    [Fact]
    public void EmitAlter_rebuilds_table_when_identity_flag_changes_on_existing_column()
    {
        // Spec §3.4: an identity-flag flip on an existing column cannot be
        // expressed via ALTER COLUMN, and DROP+ADD COLUMN would lose every
        // row's value. The emitter produces a temp-table rebuild instead.
        Table src = T("X", new Column("Id", "int", false, 1, isIdentity: true));
        Table tgt = T("X", new Column("Id", "int", false, 1, isIdentity: false));

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));
        sql.Should().Contain("CREATE TABLE [dbo].[X_tmp]")
           .And.Contain("INSERT INTO [dbo].[X_tmp] ([Id]) SELECT [Id] FROM [dbo].[X];")
           .And.Contain("EXEC sp_rename '[dbo].[X_tmp]', 'X';")
           .And.NotContain("DROP COLUMN [Id]");
    }

    // ── EmitAlter — constraints ────────────────────────────────────────────

    [Fact]
    public void EmitAlter_drops_constraint_removed_from_source()
    {
        Table tgt = T("X", new Column("Id", "int", false, 1)) with
        {
            Constraints = [new CheckConstraint("CK_X_Id", "([Id] > 0)", IsDisabled: false, IsNotForReplication: false)],
        };
        Table src = T("X", new Column("Id", "int", false, 1));

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));
        sql.Should().Contain("DROP CONSTRAINT [CK_X_Id]");
    }

    [Fact]
    public void EmitAlter_drop_and_readd_when_constraint_shape_changed()
    {
        Table tgt = T("X", new Column("Id", "int", false, 1)) with
        {
            Constraints = [new CheckConstraint("CK_X_Id", "([Id] > 0)", IsDisabled: false, IsNotForReplication: false)],
        };
        Table src = T("X", new Column("Id", "int", false, 1)) with
        {
            Constraints = [new CheckConstraint("CK_X_Id", "([Id] >= 0)", IsDisabled: false, IsNotForReplication: false)],
        };

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));
        sql.Should().Contain("DROP CONSTRAINT [CK_X_Id]")
           .And.Contain("ADD CONSTRAINT [CK_X_Id] CHECK ([Id] >= 0)");
    }

    // ── ScriptGenerator — index delta on Different tables ──────────────────

    [Fact]
    public void Generator_drops_indexes_missing_on_source_for_different_table()
    {
        TableIndex ixOld = new("IX_X_Old", false, false, null,
            [new IndexColumn("Id", false)], []);
        Table src = T("X", new Column("Id", "int", false, 1));
        Table tgt = T("X", new Column("Id", "int", false, 1)) with { Indexes = [ixOld] };

        ComparisonResult result = new([Diff(src, tgt)]);
        string sql = new ScriptGenerator().Generate(result);
        sql.Should().Contain("DROP INDEX [IX_X_Old] ON [dbo].[X];");
    }

    [Fact]
    public void Generator_creates_indexes_only_on_source_for_different_table()
    {
        TableIndex ixNew = new("IX_X_New", false, false, null,
            [new IndexColumn("Id", false)], []);
        Table src = T("X", new Column("Id", "int", false, 1)) with { Indexes = [ixNew] };
        Table tgt = T("X", new Column("Id", "int", false, 1));

        ComparisonResult result = new([Diff(src, tgt)]);
        string sql = new ScriptGenerator().Generate(result);
        sql.Should().Contain("CREATE NONCLUSTERED INDEX [IX_X_New] ON [dbo].[X]");
    }

    [Fact]
    public void Generator_drops_and_recreates_index_when_shape_changed()
    {
        TableIndex ixOld = new("IX_X", false, false, null,
            [new IndexColumn("Id", false)], []);
        TableIndex ixNew = new("IX_X", true, false, null,           // becomes UNIQUE
            [new IndexColumn("Id", false)], []);
        Table src = T("X", new Column("Id", "int", false, 1)) with { Indexes = [ixNew] };
        Table tgt = T("X", new Column("Id", "int", false, 1)) with { Indexes = [ixOld] };

        ComparisonResult result = new([Diff(src, tgt)]);
        string sql = new ScriptGenerator().Generate(result);
        sql.Should().Contain("DROP INDEX [IX_X] ON [dbo].[X];")
           .And.Contain("CREATE UNIQUE NONCLUSTERED INDEX [IX_X]");
    }

    // ── ScriptGenerator — FK delta on Different tables ─────────────────────

    [Fact]
    public void Generator_drops_fk_removed_from_source_for_different_table()
    {
        ForeignKey fk = new("FK_X_Y", ["YId"], "dbo", "Y", ["Id"],
            OnDelete: ReferentialAction.NoAction, OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false, IsNotForReplication: false);
        Table src = T("X", new Column("YId", "int", false, 1));
        Table tgt = T("X", new Column("YId", "int", false, 1)) with { Constraints = [fk] };

        ComparisonResult result = new([Diff(src, tgt)]);
        string sql = new ScriptGenerator().Generate(result);
        sql.Should().Contain("ALTER TABLE [dbo].[X] DROP CONSTRAINT [FK_X_Y];");
    }

    [Fact]
    public void Generator_adds_new_fk_for_different_table()
    {
        ForeignKey fk = new("FK_X_Y", ["YId"], "dbo", "Y", ["Id"],
            OnDelete: ReferentialAction.NoAction, OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false, IsNotForReplication: false);
        Table src = T("X", new Column("YId", "int", false, 1)) with { Constraints = [fk] };
        Table tgt = T("X", new Column("YId", "int", false, 1));

        ComparisonResult result = new([Diff(src, tgt)]);
        string sql = new ScriptGenerator().Generate(result);
        sql.Should().Contain("ADD CONSTRAINT [FK_X_Y] FOREIGN KEY ([YId])");
    }
}
