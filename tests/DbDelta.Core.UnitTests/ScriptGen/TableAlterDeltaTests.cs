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

    // ── EmitAlter — case sensitivity of the column pairing (C2) ────────────

    /// <summary>
    /// The engine pairs the TABLE case-insensitively on a CI target, so a
    /// genuinely changed column reaches the emitter as a Different pair. If the
    /// emitter then pairs its columns ordinally it sees one column removed and
    /// another added, and emits DROP COLUMN — which takes the data with it —
    /// instead of the ALTER COLUMN the change actually needs.
    /// </summary>
    [Fact]
    public void EmitAlter_pairs_columns_by_the_supplied_comparer_instead_of_dropping_and_re_adding()
    {
        Table src = T("Clienti", new Column("Nome", "nvarchar(50)", false, 1));
        Table tgt = T("Clienti", new Column("NOME", "nvarchar(40)", false, 1));

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(Diff(src, tgt));

        sql.Should().Contain("ALTER COLUMN [Nome] [nvarchar] (50)");
        sql.Should().NotContain("DROP COLUMN");
    }

    [Fact]
    public void EmitAlter_still_drops_and_re_adds_when_the_target_is_case_sensitive()
    {
        Table src = T("Clienti", new Column("Nome", "nvarchar(50)", false, 1));
        Table tgt = T("Clienti", new Column("NOME", "nvarchar(40)", false, 1));

        string sql = new TableScriptEmitter(StringComparer.Ordinal).Emit(Diff(src, tgt));

        sql.Should().Contain("DROP COLUMN [NOME]").And.Contain("ADD [Nome]");
    }

    /// <summary>
    /// The whole chain, not just the emitter: ScriptGenerator has to hand the
    /// comparer down from the comparison result, or the fix stops at the door.
    /// </summary>
    [Fact]
    public void Generate_hands_the_results_comparer_down_to_the_table_emitter()
    {
        Table src = T("Clienti", new Column("Nome", "nvarchar(50)", false, 1));
        Table tgt = T("Clienti", new Column("NOME", "nvarchar(40)", false, 1));
        ComparisonResult result = new([Diff(src, tgt)])
        {
            NameComparer = StringComparer.OrdinalIgnoreCase,
        };

        string sql = new ScriptGenerator().Generate(result);

        sql.Should().Contain("ALTER COLUMN [Nome]");
        sql.Should().NotContain("DROP COLUMN");
    }

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

    // ── Named DEFAULT constraints travel inline on the ADD ─────────────────

    [Fact]
    public void EmitAlter_adds_not_null_column_with_its_default_inline()
    {
        // A NOT NULL column added to a populated table WITHOUT a DEFAULT fails
        // with Msg 4901 ("ALTER TABLE only allows columns to be added that can
        // contain nulls, or have a DEFAULT definition specified") — and this is
        // the single most common real migration there is. The default must ride
        // on the ADD, keeping its constraint name, not arrive as a later
        // standalone ADD CONSTRAINT.
        DefaultConstraint df = new("DF_X_Status", "Status", "((0))");
        Table src = T("X",
            new Column("Id", "int", false, 1),
            new Column("Status", "int", false, 2, defaultExpression: "((0))")) with
        { Constraints = [df] };
        Table tgt = T("X", new Column("Id", "int", false, 1));

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));

        sql.Should().Contain("ADD [Status] [int] NOT NULL CONSTRAINT [DF_X_Status] DEFAULT ((0));");
        // ...and must NOT also be re-added standalone, which would fail with
        // "Column already has a DEFAULT bound to it".
        sql.Should().NotContain("ADD CONSTRAINT [DF_X_Status]");
    }

    [Fact]
    public void EmitCreate_puts_named_default_on_the_column_not_at_table_level()
    {
        // DEFAULT is absent from the T-SQL table_constraint grammar: a
        // table-level "CONSTRAINT [x] DEFAULT (e) FOR [c]" inside CREATE TABLE
        // is Msg 102. The column-level form is the only valid one and keeps the
        // constraint name. (A golden test used to pin the broken form.)
        DefaultConstraint df = new("DF_X_CreatedAt", "CreatedAt", "(sysutcdatetime())");
        Table src = T("X",
            new Column("Id", "int", false, 1),
            new Column("CreatedAt", "datetime2", false, 2, defaultExpression: "(sysutcdatetime())"))
            with
        { Constraints = [df] };

        string sql = TableScriptEmitter.GenerateCreateTable(src);

        sql.Should().Contain("[CreatedAt] [datetime2] NOT NULL CONSTRAINT [DF_X_CreatedAt] DEFAULT (sysutcdatetime())");
        sql.Should().NotContain("FOR [CreatedAt]");
    }

    /// <summary>
    /// A CHECK over a column the same run adds must not share the column's
    /// batch.
    /// </summary>
    /// <remarks>
    /// SQL Server compiles a batch IN FULL before running any of it, so the
    /// constraint cannot resolve a column that only exists once an earlier
    /// statement in that same batch has run: Msg 207, "invalid column name",
    /// at compile time. Every deploy adding a column plus a CHECK over it died
    /// on it — and under XACT_ABORT that takes the whole transaction with it.
    /// Found by the first live smoke against a real database.
    /// </remarks>
    [Fact]
    public void A_constraint_over_a_newly_added_column_lands_in_a_later_batch()
    {
        Table tgt = T("Regole", new Column("Id", "int", false, 1));
        Table src = new(
            "dbo",
            "Regole",
            [new Column("Id", "int", false, 1), new Column("Cond", "nvarchar(8)", true, 2)],
            [new CheckConstraint("CK_Regole_Cond", "([Cond] IS NULL OR [Cond]='HAS')", false, false)],
            []);

        string sql = new ScriptGenerator().Generate(new ComparisonResult([Diff(src, tgt)]));

        int addColumn = sql.IndexOf("ADD [Cond]", StringComparison.Ordinal);
        int addCheck = sql.IndexOf("ADD CONSTRAINT [CK_Regole_Cond]", StringComparison.Ordinal);
        addColumn.Should().BeGreaterThan(0);
        addCheck.Should().BeGreaterThan(addColumn);

        string between = sql[addColumn..addCheck];
        between.Split('\n').Select(l => l.Trim())
            .Should().Contain("GO", "the column has to be committed to the table before the CHECK compiles");
    }

    /// <summary>
    /// The separator is emitted only when a column was actually added: a table
    /// whose constraints alone changed keeps its single-batch shape, so the
    /// script does not grow a batch per table for no reason.
    /// </summary>
    [Fact]
    public void A_constraint_change_with_no_new_column_stays_in_one_batch()
    {
        Table tgt = new(
            "dbo",
            "Regole",
            [new Column("Id", "int", false, 1)],
            [new CheckConstraint("CK_Regole_Id", "([Id]>(0))", false, false)],
            []);
        Table src = new(
            "dbo",
            "Regole",
            [new Column("Id", "int", false, 1)],
            [new CheckConstraint("CK_Regole_Id", "([Id]>(1))", false, false)],
            []);

        string sql = new ScriptGenerator().Generate(new ComparisonResult([Diff(src, tgt)]));

        int drop = sql.IndexOf("DROP CONSTRAINT [CK_Regole_Id]", StringComparison.Ordinal);
        int add = sql.IndexOf("ADD CONSTRAINT [CK_Regole_Id]", StringComparison.Ordinal);
        drop.Should().BeGreaterThan(0);
        add.Should().BeGreaterThan(drop);

        string between = sql[drop..add];
        between.Split('\n').Select(l => l.Trim())
            .Should().NotContain("GO", "nothing here needs a new batch");
    }
}
