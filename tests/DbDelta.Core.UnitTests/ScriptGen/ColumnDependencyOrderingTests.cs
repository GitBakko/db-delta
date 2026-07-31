using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// SQL Server refuses to retype or drop a column while an index, key or CHECK
/// constraint depends on it (Msg 5074, "The object '…' is dependent on column
/// '…'"). Widening an indexed int to bigint — about as routine as a migration
/// gets — therefore failed outright. Whatever depends on the column has to be
/// dropped first and put back afterwards, including when it is byte-identical on
/// both sides and so absent from every delta.
/// </summary>
public class ColumnDependencyOrderingTests
{
    private static readonly ScriptGenerator Sut = new();

    private static Table Orders(string customerIdType, params TableIndex[] indexes) =>
        new("dbo", "Orders",
            [new Column("Id", "int", false, 1), new Column("CustomerId", customerIdType, false, 2)],
            [],
            indexes);

    private static TableIndex Ix(string name, string keyColumn) =>
        new(name, false, false, null, [new IndexColumn(keyColumn, false)], []);

    private static DifferencePair Diff(Table src, Table tgt) =>
        new(src.Identity, DifferenceStatus.Different, src, tgt);

    [Fact]
    public void Index_on_an_altered_column_is_dropped_before_the_alter_and_recreated_after()
    {
        TableIndex ix = Ix("IX_Orders_CustomerId", "CustomerId");
        Table src = Orders("bigint", ix);
        Table tgt = Orders("int", ix);

        string sql = Sut.Generate(new ComparisonResult([Diff(src, tgt)]));

        int dropIx = sql.IndexOf("DROP INDEX [IX_Orders_CustomerId] ON [dbo].[Orders];", StringComparison.Ordinal);
        int alter = sql.IndexOf("ALTER COLUMN [CustomerId] [bigint]", StringComparison.Ordinal);
        int createIx = sql.IndexOf("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId]", StringComparison.Ordinal);

        dropIx.Should().BeGreaterThan(0, "Msg 5074 blocks the ALTER while the index covers the column");
        alter.Should().BeGreaterThan(dropIx);
        createIx.Should().BeGreaterThan(alter, "the index must come back, and with the new column type");
    }

    [Fact]
    public void Index_on_an_untouched_column_is_left_alone()
    {
        // Only CustomerId is retyped; an index on Id must not be disturbed.
        TableIndex onId = Ix("IX_Orders_Id", "Id");
        Table src = Orders("bigint", onId);
        Table tgt = Orders("int", onId);

        string sql = Sut.Generate(new ComparisonResult([Diff(src, tgt)]));

        sql.Should().NotContain("DROP INDEX [IX_Orders_Id]");
        sql.Should().NotContain("CREATE NONCLUSTERED INDEX [IX_Orders_Id]");
    }

    [Fact]
    public void Index_that_merely_includes_the_altered_column_is_also_dropped()
    {
        TableIndex including = new("IX_Orders_Cover", false, false, null,
            [new IndexColumn("Id", false)], ["CustomerId"]);
        Table src = Orders("bigint", including);
        Table tgt = Orders("int", including);

        string sql = Sut.Generate(new ComparisonResult([Diff(src, tgt)]));

        sql.Should().Contain("DROP INDEX [IX_Orders_Cover]");
        sql.Should().Contain("CREATE NONCLUSTERED INDEX [IX_Orders_Cover]");
    }

    [Fact]
    public void Primary_key_on_an_altered_column_is_dropped_and_restored()
    {
        PrimaryKey pk = new("PK_Orders", ["CustomerId"], IsClustered: true);
        Table src = new("dbo", "Orders",
            [new Column("CustomerId", "bigint", false, 1)], [pk], []);
        Table tgt = new("dbo", "Orders",
            [new Column("CustomerId", "int", false, 1)], [pk], []);

        string sql = new TableScriptEmitter().Emit(Diff(src, tgt));

        int dropPk = sql.IndexOf("DROP CONSTRAINT [PK_Orders];", StringComparison.Ordinal);
        int alter = sql.IndexOf("ALTER COLUMN [CustomerId] [bigint]", StringComparison.Ordinal);
        int addPk = sql.IndexOf("ADD CONSTRAINT [PK_Orders] PRIMARY KEY", StringComparison.Ordinal);
        dropPk.Should().BeGreaterThan(0);
        alter.Should().BeGreaterThan(dropPk);
        addPk.Should().BeGreaterThan(alter);
    }

    [Fact]
    public void Check_constraint_referencing_an_altered_column_is_dropped_and_restored()
    {
        CheckConstraint ck = new("CK_Orders_Qty", "([Qty] > 0)", IsDisabled: false, IsNotForReplication: false);
        Table Build(string qtyType)
        {
            return new Table("dbo", "Orders",
                [new Column("Id", "int", false, 1), new Column("Qty", qtyType, false, 2)], [ck], []);
        }

        string sql = new TableScriptEmitter().Emit(Diff(Build("bigint"), Build("int")));

        int dropCk = sql.IndexOf("DROP CONSTRAINT [CK_Orders_Qty];", StringComparison.Ordinal);
        int alter = sql.IndexOf("ALTER COLUMN [Qty] [bigint]", StringComparison.Ordinal);
        int addCk = sql.IndexOf("ADD CONSTRAINT [CK_Orders_Qty] CHECK", StringComparison.Ordinal);
        dropCk.Should().BeGreaterThan(0);
        alter.Should().BeGreaterThan(dropCk);
        addCk.Should().BeGreaterThan(alter);
    }

    // ── Foreign keys over a retyped column (S2) ────────────────────────────

    /// <summary>
    /// Builds the canonical widening: parent Testa.Id and child Righe.TestaId
    /// both go int → bigint, joined by an FK that is byte-identical on the two
    /// sides. The FK's shape never changes and neither table is dropped or
    /// rebuilt, so none of the original three drop feeders saw it.
    /// </summary>
    private static (Table SrcParent, Table TgtParent, Table SrcChild, Table TgtChild) WideningPair()
    {
        static Table Parent(string idType)
        {
            return new Table(
                "dbo", "Testa",
                [new Column("Id", idType, false, 1)],
                [new PrimaryKey("PK_Testa", ["Id"], IsClustered: true)],
                []);
        }

        static Table Child(string fkColumnType)
        {
            return new Table(
                "dbo", "Righe",
                [new Column("Id", "int", false, 1), new Column("TestaId", fkColumnType, false, 2)],
                [
                    new ForeignKey(
                        "FK_Righe_Testa",
                        ["TestaId"],
                        "dbo",
                        "Testa",
                        ["Id"],
                        ReferentialAction.NoAction,
                        ReferentialAction.NoAction,
                        IsDisabled: false,
                        IsNotForReplication: false)
                ],
                []);
        }

        return (Parent("bigint"), Parent("int"), Child("bigint"), Child("int"));
    }

    [Fact]
    public void Foreign_key_over_a_retyped_column_is_dropped_first_and_re_added_after()
    {
        (Table srcParent, Table tgtParent, Table srcChild, Table tgtChild) = WideningPair();

        string sql = Sut.Generate(new ComparisonResult(
            [Diff(srcParent, tgtParent), Diff(srcChild, tgtChild)]));

        int dropFk = sql.IndexOf("DROP CONSTRAINT [FK_Righe_Testa]", StringComparison.Ordinal);
        int alterChild = sql.IndexOf("ALTER COLUMN [TestaId] [bigint]", StringComparison.Ordinal);
        int dropPk = sql.IndexOf("DROP CONSTRAINT [PK_Testa]", StringComparison.Ordinal);
        int addFk = sql.IndexOf("ADD CONSTRAINT [FK_Righe_Testa]", StringComparison.Ordinal);

        dropFk.Should().BeGreaterThan(0, "Msg 5074 blocks the child's ALTER COLUMN while the FK constrains it");
        alterChild.Should().BeGreaterThan(dropFk);
        dropPk.Should().BeGreaterThan(dropFk, "Msg 3725 blocks the PK drop while the FK references it");
        addFk.Should().BeGreaterThan(alterChild, "the FK must come back — silently losing it is worse than failing");
    }

    /// <summary>
    /// The holder of the blocking FK is a table with no differences of its own,
    /// so it appears in no pass. Only a walk of the full result can find it.
    /// </summary>
    [Fact]
    public void An_identical_table_holding_the_blocking_foreign_key_still_gets_it_dropped_and_re_added()
    {
        (Table srcParent, Table tgtParent, Table srcChild, _) = WideningPair();

        string sql = Sut.Generate(new ComparisonResult(
        [
            Diff(srcParent, tgtParent),
            new DifferencePair(srcChild.Identity, DifferenceStatus.Identical, srcChild, srcChild),
        ]));

        sql.Should().Contain("DROP CONSTRAINT [FK_Righe_Testa]");
        sql.Should().Contain("ADD CONSTRAINT [FK_Righe_Testa]");
    }

    /// <summary>
    /// The half the referenced-column feeder cannot see. Re-collating the
    /// CHILD's key column is a legitimate migration that leaves the parent
    /// untouched, so nothing points at a retyped column — but SQL Server still
    /// refuses the ALTER COLUMN while the FK constrains it (Msg 5074).
    /// </summary>
    [Fact]
    public void Foreign_key_over_a_re_collated_child_column_is_dropped_even_though_the_parent_is_untouched()
    {
        static Table Parent()
        {
            return new Table(
                "dbo", "Testa",
                [new Column("Codice", "nvarchar(20)", false, 1, collation: "Latin1_General_CI_AS")],
                [new PrimaryKey("PK_Testa", ["Codice"], IsClustered: true)],
                []);
        }

        static Table Child(string collation)
        {
            return new Table(
                "dbo", "Righe",
                [new Column("TestaCodice", "nvarchar(20)", false, 1, collation: collation)],
                [
                    new ForeignKey(
                        "FK_Righe_Testa",
                        ["TestaCodice"],
                        "dbo",
                        "Testa",
                        ["Codice"],
                        ReferentialAction.NoAction,
                        ReferentialAction.NoAction,
                        IsDisabled: false,
                        IsNotForReplication: false)
                ],
                []);
        }

        string sql = Sut.Generate(new ComparisonResult(
        [
            new DifferencePair(Parent().Identity, DifferenceStatus.Identical, Parent(), Parent()),
            Diff(Child("Latin1_General_BIN2"), Child("Latin1_General_CI_AS")),
        ]));

        int dropFk = sql.IndexOf("DROP CONSTRAINT [FK_Righe_Testa]", StringComparison.Ordinal);
        int alter = sql.IndexOf("ALTER COLUMN [TestaCodice]", StringComparison.Ordinal);
        int addFk = sql.IndexOf("ADD CONSTRAINT [FK_Righe_Testa]", StringComparison.Ordinal);

        dropFk.Should().BeGreaterThan(0);
        alter.Should().BeGreaterThan(dropFk);
        addFk.Should().BeGreaterThan(alter);
    }

    [Fact]
    public void A_foreign_key_over_untouched_columns_is_left_alone()
    {
        (Table srcParent, _, Table srcChild, _) = WideningPair();

        // Both sides keep TestaId as it is; the child only gains a column the FK
        // does not cover, so nothing the FK constrains is being retyped.
        Table childWithNote = srcChild with
        {
            Columns = [.. srcChild.Columns, new Column("Note", "nvarchar(50)", true, 3)],
        };

        string sql = Sut.Generate(new ComparisonResult(
        [
            new DifferencePair(srcParent.Identity, DifferenceStatus.Identical, srcParent, srcParent),
            Diff(childWithNote, srcChild),
        ]));

        sql.Should().Contain("ADD [Note]");
        sql.Should().NotContain("FK_Righe_Testa", "nothing the FK covers was retyped");
    }

    [Fact]
    public void A_default_only_change_drops_nothing_but_the_default()
    {
        // A DEFAULT change is carried by dropping and re-adding the DF_
        // constraint; it touches no index and no key. Deciding the touched-column
        // set with ColumnShapeEqual — which compares DefaultExpression — put the
        // column in that set anyway, so ((0)) → ((1)) dropped the primary key
        // (Msg 3725 as soon as an FK references it) and rebuilt the clustered
        // index inside a batch capped at 60 s, for an ALTER COLUMN that is a
        // no-op. Verified on SQL Server 2022 that the no-op ALTER COLUMN is
        // accepted while the PK and the index stay in place.
        static Table Orders(string defaultExpression)
        {
            return new Table("dbo", "Orders",
                [new Column("Id", "int", false, 1),
                 new Column("Amount", "decimal(18,2)", false, 2, defaultExpression: defaultExpression)],
                [new PrimaryKey("PK_Orders", ["Amount"], IsClustered: true),
                 new DefaultConstraint("DF_Orders_Amount", "Amount", defaultExpression)],
                [Ix("IX_Orders_Amount", "Amount")]);
        }

        string sql = Sut.Generate(new ComparisonResult([Diff(Orders("((1))"), Orders("((0))"))]));

        sql.Should().NotContain("DROP CONSTRAINT [PK_Orders]");
        sql.Should().NotContain("DROP INDEX [IX_Orders_Amount]");
        sql.Should().NotContain("CREATE NONCLUSTERED INDEX [IX_Orders_Amount]");
        // What the change actually requires, and all it requires.
        sql.Should().Contain("DROP CONSTRAINT [DF_Orders_Amount]");
        sql.Should().Contain("ADD CONSTRAINT [DF_Orders_Amount] DEFAULT ((1)) FOR [Amount];");
    }

    // ── The forced-recreate set is per table, not per index name ────────────
    //    Index names are unique per object_id, not per database, so IX_TenantId
    //    on two tables is routine. The set of "indexes we dropped up front" is
    //    global to the script and was keyed on the bare name, then handed to
    //    every Different table's index delta — so an unrelated namesake on
    //    another table inherited the flag. Both fixtures below need TWO tables;
    //    every other fixture in the suite is single-table, which is why this
    //    was invisible.

    private static Table Tenanted(string name, string tenantIdType, params TableIndex[] indexes) =>
        new("dbo", name,
            [new Column("Id", "int", false, 1), new Column("TenantId", tenantIdType, false, 2)],
            [],
            indexes);

    [Fact]
    public void A_namesake_index_on_another_table_is_not_force_recreated()
    {
        // Orders.TenantId is retyped, so dbo.Orders' IX_TenantId is dropped up
        // front and must be re-created. Invoices' IX_TenantId is identical on
        // both sides and was never dropped: a CREATE for it fails with Msg 1913
        // ("operation failed, an index with name IX_TenantId already exists"),
        // and XACT_ABORT rolls the whole deploy back.
        TableIndex ix = Ix("IX_TenantId", "TenantId");
        Table invoiceTgt = Tenanted("Invoices", "int", ix);
        Table invoiceSrc = new("dbo", "Invoices",
            [new Column("Id", "int", false, 1), new Column("TenantId", "int", false, 2),
             new Column("Note", "nvarchar(50)", true, 3)],
            [],
            [ix]);

        string sql = Sut.Generate(new ComparisonResult(
        [
            Diff(Tenanted("Orders", "bigint", ix), Tenanted("Orders", "int", ix)),
            Diff(invoiceSrc, invoiceTgt),
        ]));

        sql.Should().Contain("CREATE NONCLUSTERED INDEX [IX_TenantId] ON [dbo].[Orders]",
            "Orders' index really was dropped up front");
        sql.Should().NotContain("CREATE NONCLUSTERED INDEX [IX_TenantId] ON [dbo].[Invoices]");
    }

    [Fact]
    public void A_namesake_index_removed_from_the_source_is_still_dropped_on_another_table()
    {
        // Mirror of the above and the silent half: the source deleted Invoices'
        // IX_TenantId, so the delta owes a DROP. Skipping it as "already
        // dropped" left the index in production while the tool reported success
        // and the next compare still showed the table as Different.
        TableIndex ix = Ix("IX_TenantId", "TenantId");

        string sql = Sut.Generate(new ComparisonResult(
        [
            Diff(Tenanted("Orders", "bigint", ix), Tenanted("Orders", "int", ix)),
            Diff(Tenanted("Invoices", "int"), Tenanted("Invoices", "int", ix)),
        ]));

        sql.Should().Contain("DROP INDEX [IX_TenantId] ON [dbo].[Orders];",
            "Orders' index blocks the ALTER COLUMN");
        sql.Should().Contain("DROP INDEX [IX_TenantId] ON [dbo].[Invoices];",
            "the source removed this one — nothing else emits it");
    }

    [Fact]
    public void Index_on_a_dropped_column_is_dropped_first()
    {
        // DROP COLUMN is blocked by the same dependency, and the index cannot
        // come back — its column is gone.
        TableIndex ix = Ix("IX_Orders_Legacy", "Legacy");
        Table src = new("dbo", "Orders", [new Column("Id", "int", false, 1)], [], []);
        Table tgt = new("dbo", "Orders",
            [new Column("Id", "int", false, 1), new Column("Legacy", "int", true, 2)], [], [ix]);

        string sql = Sut.Generate(new ComparisonResult([Diff(src, tgt)]));

        int dropIx = sql.IndexOf("DROP INDEX [IX_Orders_Legacy]", StringComparison.Ordinal);
        int dropCol = sql.IndexOf("DROP COLUMN [Legacy];", StringComparison.Ordinal);
        dropIx.Should().BeGreaterThan(0);
        dropCol.Should().BeGreaterThan(dropIx);
        sql.Should().NotContain("CREATE NONCLUSTERED INDEX [IX_Orders_Legacy]");
    }
}
