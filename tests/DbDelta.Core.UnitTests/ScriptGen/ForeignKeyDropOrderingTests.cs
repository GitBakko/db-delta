using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Foreign-key DROPs used to live in the FK pass at the very END of the script,
/// while DROP TABLE ran near the beginning. A table removed from the source was
/// therefore dropped while another table still referenced it, so the deploy died
/// on Msg 3726 ("Could not drop object ... because it is referenced by a FOREIGN
/// KEY constraint") every single time. All FK drops now happen up front.
/// </summary>
public class ForeignKeyDropOrderingTests
{
    private static readonly ScriptGenerator Sut = new();

    private static ForeignKey Fk(string name, string refTable) => new(
        Name: name,
        Columns: ["CurrencyId"],
        ReferencedSchema: "dbo",
        ReferencedTable: refTable,
        ReferencedColumns: ["Id"],
        OnDelete: ReferentialAction.NoAction,
        OnUpdate: ReferentialAction.NoAction,
        IsDisabled: false,
        IsNotForReplication: false);

    private static Table Currency() => new("dbo", "Currency", [new Column("Id", "int", false, 1)]);

    private static Table Invoice(params ForeignKey[] fks) =>
        new("dbo", "Invoice",
            [new Column("Id", "int", false, 1), new Column("CurrencyId", "int", false, 2)],
            fks,
            []);

    [Fact]
    public void Fk_from_a_different_table_is_dropped_before_the_table_it_references()
    {
        // Source removed dbo.Currency; on the source side Invoice no longer
        // carries the FK, so Invoice is Different and the drop IS generated —
        // it was just generated far too late.
        ComparisonResult r = new(
        [
            new DifferencePair(Currency().Identity, DifferenceStatus.OnlyInB, null, Currency()),
            new DifferencePair(Invoice().Identity, DifferenceStatus.Different,
                Invoice(), Invoice(Fk("FK_Invoice_Currency", "Currency"))),
        ]);

        string sql = Sut.Generate(r);

        int dropFk = sql.IndexOf(
            "ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];",
            StringComparison.Ordinal);
        int dropTable = sql.IndexOf("DROP TABLE [dbo].[Currency];", StringComparison.Ordinal);
        dropFk.Should().BeGreaterThan(0, "the referencing FK must be dropped");
        dropTable.Should().BeGreaterThan(dropFk,
            "DROP TABLE fails with Msg 3726 while an FK still references the table");
    }

    [Fact]
    public void Fk_held_by_an_identical_table_is_still_dropped_before_the_referenced_table()
    {
        // The nastier shape: the table holding the FK is Identical on both sides,
        // so it never enters the working set and no per-pair pass can see it.
        Table invoice = Invoice(Fk("FK_Invoice_Currency", "Currency"));
        ComparisonResult r = new(
        [
            new DifferencePair(Currency().Identity, DifferenceStatus.OnlyInB, null, Currency()),
            new DifferencePair(invoice.Identity, DifferenceStatus.Identical, invoice, invoice),
        ]);

        string sql = Sut.Generate(r);

        int dropFk = sql.IndexOf(
            "ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];",
            StringComparison.Ordinal);
        int dropTable = sql.IndexOf("DROP TABLE [dbo].[Currency];", StringComparison.Ordinal);
        dropFk.Should().BeGreaterThan(0,
            "an FK on an Identical table still blocks the DROP, so it must be found");
        dropTable.Should().BeGreaterThan(dropFk);
    }

    /// <summary>
    /// Two target-only tables joined by a foreign key. Whether the deploy
    /// survives must not depend on which name sorts first.
    /// </summary>
    /// <remarks>
    /// The holder scan used to skip a holder that was itself being dropped, on
    /// the grounds that its DROP TABLE takes the FK with it. That is true only
    /// if the holder is dropped FIRST, and the drop pass orders target-only
    /// objects by reversed kind rank and then reverse-alphabetically — so it
    /// held for Currency/Invoice and failed for Zone/Alpha. The predecessor of
    /// this test used the lucky pair and asserted only that both DROP TABLEs
    /// were present, never their order, so it pinned the broken behaviour
    /// instead of catching it.
    /// </remarks>
    [Theory]
    [InlineData("Currency", "Invoice")]
    [InlineData("Zone", "Alpha")]
    public void Two_target_only_tables_joined_by_an_fk_drop_in_a_survivable_order(string parent, string child)
    {
        Table parentTable = new("dbo", parent, [new Column("Id", "int", false, 1)]);
        Table childTable = new(
            "dbo",
            child,
            [new Column("Id", "int", false, 1), new Column("CurrencyId", "int", false, 2)],
            [Fk($"FK_{child}_{parent}", parent)],
            []);

        ComparisonResult r = new(
        [
            new DifferencePair(parentTable.Identity, DifferenceStatus.OnlyInB, null, parentTable),
            new DifferencePair(childTable.Identity, DifferenceStatus.OnlyInB, null, childTable),
        ]);

        string sql = Sut.Generate(r);

        int dropFk = sql.IndexOf($"DROP CONSTRAINT [FK_{child}_{parent}]", StringComparison.Ordinal);
        int dropParent = sql.IndexOf($"DROP TABLE [dbo].[{parent}];", StringComparison.Ordinal);

        dropFk.Should().BeGreaterThan(0, "Msg 3726 fires the moment the parent is dropped while the FK stands");
        dropParent.Should().BeGreaterThan(dropFk);
        sql.Should().Contain($"DROP TABLE [dbo].[{child}];");
    }

    // ── The drop pass dedupes per (schema, table, name), not per name ───────

    private static ForeignKey TestaFk(string referencedSchema) => new(
        Name: "FK_Righe_Testa",
        Columns: ["TestaId"],
        ReferencedSchema: referencedSchema,
        ReferencedTable: "Testa",
        ReferencedColumns: ["Id"],
        OnDelete: ReferentialAction.NoAction,
        OnUpdate: ReferentialAction.NoAction,
        IsDisabled: false,
        IsNotForReplication: false);

    private static Table Righe(string schema, params ForeignKey[] fks) =>
        new(schema, "Righe",
            [new Column("Id", "int", false, 1), new Column("TestaId", "int", false, 2)],
            fks,
            []);

    [Fact]
    public void Same_named_fks_in_two_schemas_are_both_dropped()
    {
        // Constraints are sys.objects rows carrying their parent table's
        // schema_id, so a name is unique per SCHEMA: dbo.FK_Righe_Testa and
        // sales.FK_Righe_Testa are two different constraints. Deduping the
        // up-front drop pass on the bare name emitted only ONE
        // ALTER TABLE … DROP CONSTRAINT, and nothing re-emits the other — the
        // FK pass only ADDs. The constraint the source removed survived in
        // production while the tool reported success.
        ComparisonResult r = new(
        [
            new DifferencePair(Righe("dbo").Identity, DifferenceStatus.Different,
                Righe("dbo"), Righe("dbo", TestaFk("dbo"))),
            new DifferencePair(Righe("sales").Identity, DifferenceStatus.Different,
                Righe("sales"), Righe("sales", TestaFk("sales"))),
        ]);

        string sql = Sut.Generate(r);

        sql.Should().Contain("ALTER TABLE [dbo].[Righe] DROP CONSTRAINT [FK_Righe_Testa];");
        sql.Should().Contain("ALTER TABLE [sales].[Righe] DROP CONSTRAINT [FK_Righe_Testa];");
    }

    [Fact]
    public void A_reshaped_fk_is_dropped_in_the_up_front_pass_and_re_added_in_the_late_one()
    {
        // The drop and the add are in different passes; both must still fire, in
        // that order. Anchored to the two PASS LABELS, not merely to
        // `addFk > dropFk`: the old EmitFkDelta emitted DROP then ADD inside the
        // same late batch, so relative statement order was already satisfied and
        // the earlier version of this test passed with the whole restructuring
        // reverted. Asserting the up-front pass EXISTS is what makes it bite.
        Table src = Invoice(Fk("FK_Invoice_Currency", "Currency"));
        Table tgt = Invoice(Fk("FK_Invoice_Currency", "OldCurrency"));
        ComparisonResult r = new(
        [
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt),
        ]);

        string sql = Sut.Generate(r);

        int dropPass = sql.IndexOf("PRINT N'Dropping foreign keys';", StringComparison.Ordinal);
        int dropFk = sql.IndexOf(
            "ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];",
            StringComparison.Ordinal);
        int addPass = sql.IndexOf(
            "PRINT N'Adding foreign keys on [dbo].[Invoice]';", StringComparison.Ordinal);
        int addFk = sql.IndexOf(
            "ADD CONSTRAINT [FK_Invoice_Currency] FOREIGN KEY",
            StringComparison.Ordinal);

        dropPass.Should().BeGreaterThan(0, "the up-front foreign-key drop pass must exist");
        dropFk.Should().BeGreaterThan(dropPass, "the drop belongs to that pass, not to the late one");
        addPass.Should().BeGreaterThan(dropFk);
        addFk.Should().BeGreaterThan(addPass);
    }
}
