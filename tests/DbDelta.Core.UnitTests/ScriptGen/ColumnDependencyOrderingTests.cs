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
