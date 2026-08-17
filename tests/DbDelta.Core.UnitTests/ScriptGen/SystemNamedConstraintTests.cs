using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Roadmap item 12, emission half. A constraint SQL Server named itself is
/// created WITHOUT a name, so the target server mints its own from its own
/// object_id — copying the source's <c>DF__Ordini__Stato__3B75D760</c> would
/// pin a name the next comparison can never reproduce.
/// </summary>
public class SystemNamedConstraintTests
{
    private const string SourceHash = "DF__Ordini__Stato__3B75D760";
    private const string TargetHash = "DF__Ordini__Stato__1A14E395";

    private static Column Col(string name, string type, int ordinal, bool nullable = false) =>
        new(name, type, nullable, ordinal);

    private static Table T(string name, IReadOnlyList<Column> cols, params Constraint[] constraints) =>
        new("dbo", name, cols, constraints, []);

    private static DifferencePair Diff(Table src, Table tgt) =>
        new(src.Identity, DifferenceStatus.Different, src, tgt);

    private static DefaultConstraint AutoDefault(string name, string column, string expression) =>
        new(name, column, expression) { IsSystemNamed = true };

    // ── CREATE TABLE ───────────────────────────────────────────────────────

    [Fact]
    public void A_new_table_creates_its_auto_named_constraints_without_a_name()
    {
        Table src = T("Ordini",
            [Col("Id", "int", 1), Col("Stato", "int", 2), Col("Qta", "int", 3), Col("Codice", "nvarchar(10)", 4)],
            new PrimaryKey("PK__Ordini__3214EC0762B6F0DA", ["Id"], true) { IsSystemNamed = true },
            new UniqueConstraint("UQ__Ordini__A1B2C3D4", ["Codice"], false) { IsSystemNamed = true },
            new CheckConstraint("CK__Ordini__Qta__5A6B7C8D", "([Qta]>(0))", false, false) { IsSystemNamed = true },
            AutoDefault(SourceHash, "Stato", "((0))"));

        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(src.Identity, DifferenceStatus.OnlyInA, src, null));

        sql.Should().NotContain("CONSTRAINT");
        sql.Should().Contain("UNIQUE NONCLUSTERED ([Codice])");
        sql.Should().Contain("CHECK ([Qta]>(0))");
        sql.Should().Contain("[Stato] [int] NOT NULL DEFAULT ((0))");
        sql.Should().Contain("ADD PRIMARY KEY CLUSTERED ([Id])");
    }

    [Fact]
    public void A_new_table_still_creates_an_explicitly_named_constraint_by_name()
    {
        Table src = T("Ordini",
            [Col("Id", "int", 1), Col("Stato", "int", 2)],
            new PrimaryKey("PK_Ordini", ["Id"], true),
            new DefaultConstraint("DF_Ordini_Stato", "Stato", "((0))"));

        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(src.Identity, DifferenceStatus.OnlyInA, src, null));

        sql.Should().Contain("ADD CONSTRAINT [PK_Ordini] PRIMARY KEY CLUSTERED ([Id])");
        sql.Should().Contain("CONSTRAINT [DF_Ordini_Stato] DEFAULT ((0))");
    }

    // ── ALTER ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The bug this item exists for, seen from the script side: the two hashes
    /// never matched, so every ALTER of such a table dropped the target's
    /// constraint to add the source's — on production primary keys.
    /// </summary>
    [Fact]
    public void An_unchanged_auto_named_default_is_neither_dropped_nor_re_added()
    {
        Table src = T("Ordini",
            [Col("Id", "int", 1), Col("Stato", "int", 2), Col("Note", "nvarchar(50)", 3, nullable: true)],
            AutoDefault(SourceHash, "Stato", "((0))"));
        Table tgt = T("Ordini",
            [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(TargetHash, "Stato", "((0))"));

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(Diff(src, tgt));

        // The column that actually changed still moves.
        sql.Should().Contain("ADD [Note]");
        sql.Should().NotContain("DROP CONSTRAINT");
        sql.Should().NotContain(SourceHash);
        sql.Should().NotContain(TargetHash);
    }

    [Fact]
    public void An_auto_named_default_whose_expression_changed_is_dropped_by_the_targets_name_and_re_added_anonymously()
    {
        Table src = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(SourceHash, "Stato", "((1))"));
        Table tgt = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(TargetHash, "Stato", "((0))"));

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(Diff(src, tgt));

        sql.Should().Contain($"DROP CONSTRAINT [{TargetHash}];");
        sql.Should().Contain("ADD DEFAULT ((1)) FOR [Stato];");
        // The source-side hash is meaningless on the target and must not travel.
        sql.Should().NotContain(SourceHash);
    }

    [Fact]
    public void An_auto_named_check_the_source_dropped_is_dropped_by_the_targets_name()
    {
        Table src = T("Ordini", [Col("Id", "int", 1), Col("Qta", "int", 2)]);
        Table tgt = T("Ordini", [Col("Id", "int", 1), Col("Qta", "int", 2)],
            new CheckConstraint("CK__Ordini__Qta__5A6B7C8D", "([Qta]>(0))", false, false) { IsSystemNamed = true });

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(Diff(src, tgt));

        sql.Should().Contain("DROP CONSTRAINT [CK__Ordini__Qta__5A6B7C8D];");
    }

    /// <summary>
    /// An explicitly named constraint on the target facing an anonymous one on
    /// the source is a real difference: the script removes the name the source
    /// never declared and lets the server mint its own.
    /// </summary>
    [Fact]
    public void A_named_target_constraint_facing_an_auto_named_source_one_is_replaced()
    {
        Table src = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(SourceHash, "Stato", "((0))"));
        Table tgt = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            new DefaultConstraint("DF_Ordini_Stato", "Stato", "((0))"));

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(Diff(src, tgt));

        sql.Should().Contain("DROP CONSTRAINT [DF_Ordini_Stato];");
        sql.Should().Contain("ADD DEFAULT ((0)) FOR [Stato];");
    }

    /// <summary>
    /// A constraint dropped only to free an altered column is restored in
    /// section 5. The restore is looked up by the SOURCE side's name, which for
    /// an auto-named constraint is not the name section 1 dropped.
    /// </summary>
    [Fact]
    public void An_auto_named_check_dropped_to_free_an_altered_column_is_restored()
    {
        Table src = T("Ordini", [Col("Id", "int", 1), Col("Qta", "bigint", 2)],
            new CheckConstraint("CK__Ordini__Qta__AAAA", "([Qta]>(0))", false, false) { IsSystemNamed = true });
        Table tgt = T("Ordini", [Col("Id", "int", 1), Col("Qta", "int", 2)],
            new CheckConstraint("CK__Ordini__Qta__BBBB", "([Qta]>(0))", false, false) { IsSystemNamed = true });

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(Diff(src, tgt));

        sql.Should().Contain("DROP CONSTRAINT [CK__Ordini__Qta__BBBB];");
        sql.Should().Contain("ALTER COLUMN [Qta] [bigint]");
        sql.Should().Contain("ADD CHECK ([Qta]>(0));");
        sql.Should().NotContain("CK__Ordini__Qta__AAAA");
    }

    // ── Rebuild (identity change) ──────────────────────────────────────────

    /// <summary>
    /// The rebuild drops the target's named non-FK constraints and re-adds them
    /// from the SOURCE after sp_rename. Re-adding an auto-named one by name
    /// would plant the source server's hash on the target — the third structure
    /// that has to agree with the other two.
    /// </summary>
    [Fact]
    public void A_rebuild_re_adds_auto_named_constraints_without_a_name()
    {
        Table src = T("Ordini",
            [new Column("Id", "int", false, 1, isIdentity: true, identitySeed: 1, identityIncrement: 1),
             Col("Stato", "int", 2)],
            new PrimaryKey("PK__Ordini__AAAA", ["Id"], true) { IsSystemNamed = true },
            AutoDefault(SourceHash, "Stato", "((0))"));
        Table tgt = T("Ordini",
            [Col("Id", "int", 1), Col("Stato", "int", 2)],
            new PrimaryKey("PK__Ordini__BBBB", ["Id"], true) { IsSystemNamed = true },
            AutoDefault(TargetHash, "Stato", "((0))"));

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(Diff(src, tgt));

        sql.Should().Contain("DROP CONSTRAINT [PK__Ordini__BBBB];");
        sql.Should().Contain("DROP CONSTRAINT [" + TargetHash + "];");
        sql.Should().Contain("ADD PRIMARY KEY CLUSTERED ([Id]);");
        sql.Should().Contain("ADD DEFAULT ((0)) FOR [Stato];");
        sql.Should().NotContain("PK__Ordini__AAAA");
        sql.Should().NotContain(SourceHash);
    }

    // ── The full chain ─────────────────────────────────────────────────────

    /// <summary>
    /// Engine and emitter have to agree. A table whose only divergence is the
    /// hash of an auto-named constraint is Identical, and an Identical table
    /// produces no script at all.
    /// </summary>
    [Fact]
    public void A_table_differing_only_by_the_hash_produces_no_script()
    {
        Table src = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(SourceHash, "Stato", "((0))"));
        Table tgt = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(TargetHash, "Stato", "((0))"));

        Database a = new("X", [new Schema("dbo")], [src]);
        Database b = new("X", [new Schema("dbo")], [tgt]);
        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Single(d => d.Identity.Kind == "Table")
            .Status.Should().Be(DifferenceStatus.Identical);
        new ScriptGenerator().Generate(result).Should().NotContain("ALTER TABLE");
    }

    /// <summary>
    /// The diff viewer reads the same emitter. Two bodies that still printed
    /// their own hash would show a difference on a row the grid now calls
    /// Identical — the empty-diff bug of item 4, seen from the other end.
    /// </summary>
    [Fact]
    public void The_diff_viewer_body_is_identical_on_both_sides()
    {
        Table src = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(SourceHash, "Stato", "((0))"));
        Table tgt = T("Ordini", [Col("Id", "int", 1), Col("Stato", "int", 2)],
            AutoDefault(TargetHash, "Stato", "((0))"));

        TableScriptEmitter.GenerateFullTableBody(src)
            .Should().Be(TableScriptEmitter.GenerateFullTableBody(tgt));
    }
}
