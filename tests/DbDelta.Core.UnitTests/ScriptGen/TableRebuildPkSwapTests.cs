using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// M13-PARITY.6 #33 — identity rebuild now drops the named non-FK
/// constraints (PK / UQ / CK / named DEFAULT) BEFORE creating the `_tmp`
/// table, then re-adds them AFTER <c>sp_rename</c>. Two invariants:
/// <list type="bullet">
///   <item>Constraint names are DB-scoped, so `_tmp` cannot carry the
///         original PK name in parallel with the old table.</item>
///   <item>Inbound FKs from other tables pointing at the rebuilt PK
///         must survive the swap — drop them before, re-add after
///         (Redgate SQL Compare scenario 03 pattern).</item>
/// </list>
/// </summary>
public class TableRebuildPkSwapTests
{
    private static readonly ScriptGenerator Sut = new();

    private static Table InvoiceWithPk(bool identityFlipped) =>
        new("dbo", "Invoice",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: identityFlipped, identitySeed: identityFlipped ? 1 : null, identityIncrement: identityFlipped ? 1 : null),
             new Column("Amount", "decimal(18,2)", isNullable: false, ordinal: 2)],
            [new PrimaryKey("PK_Invoice", ["Id"], IsClustered: true)],
            []);

    [Fact]
    public void Rebuild_drops_PK_before_creating_tmp()
    {
        Table oldT = InvoiceWithPk(identityFlipped: false);
        Table newT = InvoiceWithPk(identityFlipped: true);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        int dropPkIdx = sql.IndexOf("ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [PK_Invoice]", StringComparison.Ordinal);
        int createTmpIdx = sql.IndexOf("CREATE TABLE [dbo].[Invoice_tmp]", StringComparison.Ordinal);
        dropPkIdx.Should().BeGreaterThan(0);
        createTmpIdx.Should().BeGreaterThan(dropPkIdx);
    }

    [Fact]
    public void Rebuild_emits_tmp_table_without_inline_PK_constraint()
    {
        Table oldT = InvoiceWithPk(identityFlipped: false);
        Table newT = InvoiceWithPk(identityFlipped: true);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        // The `_tmp` CREATE TABLE must NOT carry the PK_Invoice constraint —
        // SQL Server constraint names are DB-scoped and would collide with
        // the original table's PK_Invoice.
        int tmpStart = sql.IndexOf("CREATE TABLE [dbo].[Invoice_tmp]", StringComparison.Ordinal);
        int tmpEnd = sql.IndexOf("SET IDENTITY_INSERT [dbo].[Invoice_tmp]", StringComparison.Ordinal);
        tmpStart.Should().BeGreaterThan(0);
        tmpEnd.Should().BeGreaterThan(tmpStart);
        string tmpBlock = sql[tmpStart..tmpEnd];
        tmpBlock.Should().NotContain("PK_Invoice");
        tmpBlock.Should().NotContain("PRIMARY KEY");
    }

    [Fact]
    public void Rebuild_re_adds_PK_after_sp_rename()
    {
        Table oldT = InvoiceWithPk(identityFlipped: false);
        Table newT = InvoiceWithPk(identityFlipped: true);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        int renameIdx = sql.IndexOf("sp_rename", StringComparison.Ordinal);
        int readdPkIdx = sql.IndexOf(
            "ALTER TABLE [dbo].[Invoice] ADD CONSTRAINT [PK_Invoice] PRIMARY KEY CLUSTERED ([Id])",
            StringComparison.Ordinal);
        renameIdx.Should().BeGreaterThan(0);
        readdPkIdx.Should().BeGreaterThan(renameIdx);
    }

    [Fact]
    public void Rebuild_drops_inbound_FK_from_Identical_table_before_rebuild()
    {
        // Invoice is rebuilt; InvoiceLine (Identical) holds FK_InvoiceLine_Invoice → Invoice.Id.
        // Pre-#33 the DROP TABLE [Invoice] would fail because the FK
        // references it; #33 drops the inbound FK first.
        Table invoiceOld = InvoiceWithPk(identityFlipped: false);
        Table invoiceNew = InvoiceWithPk(identityFlipped: true);
        ForeignKey lineFk = new(
            Name: "FK_InvoiceLine_Invoice",
            Columns: ["InvoiceId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Invoice",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.NoAction,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);
        Table invoiceLine = new("dbo", "InvoiceLine",
            [new Column("Id", "int", false, 1),
             new Column("InvoiceId", "int", false, 2)],
            [lineFk],
            []);
        ComparisonResult result = new(
        [
            new DifferencePair(invoiceNew.Identity, DifferenceStatus.Different, invoiceNew, invoiceOld),
            new DifferencePair(invoiceLine.Identity, DifferenceStatus.Identical, invoiceLine, invoiceLine),
        ]);

        string sql = Sut.Generate(result);

        int dropInboundIdx = sql.IndexOf(
            "ALTER TABLE [dbo].[InvoiceLine] DROP CONSTRAINT [FK_InvoiceLine_Invoice]",
            StringComparison.Ordinal);
        int rebuildStartIdx = sql.IndexOf("CREATE TABLE [dbo].[Invoice_tmp]", StringComparison.Ordinal);
        dropInboundIdx.Should().BeGreaterThan(0);
        rebuildStartIdx.Should().BeGreaterThan(dropInboundIdx);
    }

    [Fact]
    public void Rebuild_re_adds_inbound_FK_from_source_after_FK_section()
    {
        Table invoiceOld = InvoiceWithPk(identityFlipped: false);
        Table invoiceNew = InvoiceWithPk(identityFlipped: true);
        ForeignKey lineFk = new(
            Name: "FK_InvoiceLine_Invoice",
            Columns: ["InvoiceId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Invoice",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.NoAction,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);
        Table invoiceLine = new("dbo", "InvoiceLine",
            [new Column("Id", "int", false, 1),
             new Column("InvoiceId", "int", false, 2)],
            [lineFk],
            []);
        ComparisonResult result = new(
        [
            new DifferencePair(invoiceNew.Identity, DifferenceStatus.Different, invoiceNew, invoiceOld),
            new DifferencePair(invoiceLine.Identity, DifferenceStatus.Identical, invoiceLine, invoiceLine),
        ]);

        string sql = Sut.Generate(result);

        int readdInboundIdx = sql.IndexOf(
            "ALTER TABLE [dbo].[InvoiceLine] ADD CONSTRAINT [FK_InvoiceLine_Invoice] FOREIGN KEY",
            StringComparison.Ordinal);
        int renameIdx = sql.IndexOf("sp_rename", StringComparison.Ordinal);
        readdInboundIdx.Should().BeGreaterThan(0);
        readdInboundIdx.Should().BeGreaterThan(renameIdx);
    }

    [Fact]
    public void Different_table_with_inbound_FK_to_rebuilt_table_is_not_double_dropped()
    {
        // InvoiceLine is *also* Different (column added) and holds a FK to
        // Invoice. The pair-level FK delta in section 7 must NOT emit a
        // second DROP / ADD for FK_InvoiceLine_Invoice — section 0.9 + 7.9
        // already wrap it around the rebuild.
        Table invoiceOld = InvoiceWithPk(identityFlipped: false);
        Table invoiceNew = InvoiceWithPk(identityFlipped: true);
        ForeignKey lineFk = new(
            Name: "FK_InvoiceLine_Invoice",
            Columns: ["InvoiceId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Invoice",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.NoAction,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);
        Table lineOld = new("dbo", "InvoiceLine",
            [new Column("Id", "int", false, 1),
             new Column("InvoiceId", "int", false, 2)],
            [lineFk],
            []);
        Table lineNew = new("dbo", "InvoiceLine",
            [new Column("Id", "int", false, 1),
             new Column("InvoiceId", "int", false, 2),
             new Column("Quantity", "int", false, 3)],
            [lineFk],
            []);
        ComparisonResult result = new(
        [
            new DifferencePair(invoiceNew.Identity, DifferenceStatus.Different, invoiceNew, invoiceOld),
            new DifferencePair(lineNew.Identity, DifferenceStatus.Different, lineNew, lineOld),
        ]);

        string sql = Sut.Generate(result);

        int dropCount = CountOccurrences(sql,
            "ALTER TABLE [dbo].[InvoiceLine] DROP CONSTRAINT [FK_InvoiceLine_Invoice]");
        int addCount = CountOccurrences(sql,
            "ALTER TABLE [dbo].[InvoiceLine] ADD CONSTRAINT [FK_InvoiceLine_Invoice]");
        dropCount.Should().Be(1);
        addCount.Should().Be(1);
    }

    [Fact]
    public void Rebuild_drops_named_check_and_unique_constraints_along_with_PK()
    {
        // The old table carries a PK + a named CHECK constraint. The rebuild
        // must drop both up front so the _tmp create doesn't collide with
        // their DB-scoped names.
        Table withCheckOld = new("dbo", "Invoice",
            [new Column("Id", "int", isNullable: false, ordinal: 1),
             new Column("Amount", "decimal(18,2)", isNullable: false, ordinal: 2)],
            [new PrimaryKey("PK_Invoice", ["Id"], IsClustered: true),
             new CheckConstraint("CK_Invoice_AmountPositive", "([Amount] > 0)", IsDisabled: false, IsNotForReplication: false)],
            []);
        Table withCheckNew = new("dbo", "Invoice",
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1),
             new Column("Amount", "decimal(18,2)", isNullable: false, ordinal: 2)],
            [new PrimaryKey("PK_Invoice", ["Id"], IsClustered: true),
             new CheckConstraint("CK_Invoice_AmountPositive", "([Amount] > 0)", IsDisabled: false, IsNotForReplication: false)],
            []);
        ComparisonResult result = new(
        [
            new DifferencePair(withCheckNew.Identity, DifferenceStatus.Different, withCheckNew, withCheckOld),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [PK_Invoice]");
        sql.Should().Contain("ALTER TABLE [dbo].[Invoice] DROP CONSTRAINT [CK_Invoice_AmountPositive]");
        sql.Should().Contain("ALTER TABLE [dbo].[Invoice] ADD CONSTRAINT [PK_Invoice] PRIMARY KEY CLUSTERED ([Id])");
        sql.Should().Contain("ALTER TABLE [dbo].[Invoice] ADD CONSTRAINT [CK_Invoice_AmountPositive] CHECK ([Amount] > 0)");
    }

    [Fact]
    public void Rebuild_without_inbound_FKs_emits_no_FK_orchestration_block()
    {
        Table oldT = InvoiceWithPk(identityFlipped: false);
        Table newT = InvoiceWithPk(identityFlipped: true);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        // No inbound FKs => no orchestration lines mentioning a foreign
        // table; only the PK lifecycle around the rebuild.
        sql.Should().NotContain("FK_");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
