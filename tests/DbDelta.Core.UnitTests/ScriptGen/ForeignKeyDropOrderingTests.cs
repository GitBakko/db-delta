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

    private static Table Currency() =>
        new("dbo", "Currency", [new Column("Id", "int", false, 1)]);

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

    [Fact]
    public void A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk()
    {
        // Invoice itself is being dropped, so its FK goes with it — emitting an
        // explicit DROP CONSTRAINT first would be noise.
        Table invoice = Invoice(Fk("FK_Invoice_Currency", "Currency"));
        ComparisonResult r = new(
        [
            new DifferencePair(Currency().Identity, DifferenceStatus.OnlyInB, null, Currency()),
            new DifferencePair(invoice.Identity, DifferenceStatus.OnlyInB, null, invoice),
        ]);

        string sql = Sut.Generate(r);

        sql.Should().NotContain("DROP CONSTRAINT [FK_Invoice_Currency]");
        sql.Should().Contain("DROP TABLE [dbo].[Invoice];");
        sql.Should().Contain("DROP TABLE [dbo].[Currency];");
    }

    [Fact]
    public void A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end()
    {
        // The drop and the add are now in different passes; both must still fire,
        // in that order.
        Table src = Invoice(Fk("FK_Invoice_Currency", "Currency"));
        Table tgt = Invoice(Fk("FK_Invoice_Currency", "OldCurrency"));
        ComparisonResult r = new(
        [
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt),
        ]);

        string sql = Sut.Generate(r);

        int dropFk = sql.IndexOf(
            "ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [FK_Invoice_Currency];",
            StringComparison.Ordinal);
        int addFk = sql.IndexOf(
            "ADD CONSTRAINT [FK_Invoice_Currency] FOREIGN KEY",
            StringComparison.Ordinal);
        dropFk.Should().BeGreaterThan(0);
        addFk.Should().BeGreaterThan(dropFk);
    }
}
