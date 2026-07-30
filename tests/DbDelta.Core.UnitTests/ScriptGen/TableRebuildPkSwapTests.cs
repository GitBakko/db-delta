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
///   <item>Constraint names are unique per SCHEMA, and `_tmp` is created in
///         the same schema as the original, so it cannot carry the original
///         PK name in parallel with the old table.</item>
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
        // constraint names are unique per schema and `_tmp` lives in the same
        // schema, so it would collide with the original table's PK_Invoice.
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
    public void An_unrelated_same_named_fk_in_another_schema_is_still_added()
    {
        // The set of FKs the rebuild orchestrator owns is consulted to SKIP an
        // add, and it spans the whole script. Keyed on the bare constraint name
        // it also swallowed an unrelated namesake: constraint names are unique
        // per schema, so sales.FK_Fattura_Valuta is a different constraint from
        // dbo.FK_Fattura_Valuta.
        //   dbo.Valuta gains IDENTITY => rebuild. dbo.Fattura holds an FK
        //   pointing at it, so that FK is dropped up front and re-added by the
        //   inbound pass — its name enters the orchestrated set. sales.Fattura
        //   is an unrelated Different table that GAINS its own
        //   FK_Fattura_Valuta pointing at sales.Valuta (untouched, not in the
        //   comparison). Its ADD was skipped: the deploy reported success and
        //   sales.Fattura was left with no foreign key, so orphan rows became
        //   writable.
        static ForeignKey ValutaFk(string referencedSchema)
        {
            return new ForeignKey(
                Name: "FK_Fattura_Valuta",
                Columns: ["ValutaId"],
                ReferencedSchema: referencedSchema,
                ReferencedTable: "Valuta",
                ReferencedColumns: ["Id"],
                OnDelete: ReferentialAction.NoAction,
                OnUpdate: ReferentialAction.NoAction,
                IsDisabled: false,
                IsNotForReplication: false);
        }
        static Table Fattura(string schema, params ForeignKey[] fks)
        {
            return new Table(schema, "Fattura",
                [new Column("Id", "int", false, 1), new Column("ValutaId", "int", false, 2)],
                fks,
                []);
        }
        static Table Valuta(bool identity)
        {
            return new Table("dbo", "Valuta",
                [new Column("Id", "int", isNullable: false, ordinal: 1,
                    isIdentity: identity, identitySeed: identity ? 1 : null,
                    identityIncrement: identity ? 1 : null)],
                [new PrimaryKey("PK_Valuta", ["Id"], IsClustered: true)],
                []);
        }

        Table dboFattura = Fattura("dbo", ValutaFk("dbo"));
        ComparisonResult result = new(
        [
            new DifferencePair(Valuta(true).Identity, DifferenceStatus.Different,
                Valuta(true), Valuta(false)),
            new DifferencePair(dboFattura.Identity, DifferenceStatus.Identical,
                dboFattura, dboFattura),
            new DifferencePair(Fattura("sales").Identity, DifferenceStatus.Different,
                Fattura("sales", ValutaFk("sales")), Fattura("sales")),
        ]);

        string sql = Sut.Generate(result);

        CountOccurrences(sql,
            "ALTER TABLE [sales].[Fattura] ADD CONSTRAINT [FK_Fattura_Valuta] FOREIGN KEY")
            .Should().Be(1, "nothing about the dbo rebuild concerns the sales constraint");
        CountOccurrences(sql,
            "ALTER TABLE [dbo].[Fattura] ADD CONSTRAINT [FK_Fattura_Valuta] FOREIGN KEY")
            .Should().Be(1, "the inbound pass owns this one, exactly once");
    }

    [Fact]
    public void Two_rebuilt_tables_referencing_each_other_still_drop_the_fk_between_them()
    {
        // Both tables get IDENTITY, so both are rebuilt, and Fattura points at
        // Cliente. The holder scan used to skip an FK whose holder was itself a
        // rebuild target, so nothing dropped it and whichever DROP TABLE ran
        // first died on Msg 3726 — the outcome depended on the alphabetical
        // order of the two table names.
        static Table Cliente(bool identity)
        {
            return new Table("dbo", "Cliente",
                [new Column("Id", "int", isNullable: false, ordinal: 1,
                    isIdentity: identity, identitySeed: identity ? 1 : null,
                    identityIncrement: identity ? 1 : null)],
                [new PrimaryKey("PK_Cliente", ["Id"], IsClustered: true)],
                []);
        }
        static Table Fattura(bool identity)
        {
            return new Table("dbo", "Fattura",
                [new Column("Id", "int", isNullable: false, ordinal: 1,
                    isIdentity: identity, identitySeed: identity ? 1 : null,
                    identityIncrement: identity ? 1 : null),
                 new Column("ClienteId", "int", isNullable: false, ordinal: 2)],
                [new PrimaryKey("PK_Fattura", ["Id"], IsClustered: true),
                 new ForeignKey(
                     Name: "FK_Fattura_Cliente",
                     Columns: ["ClienteId"],
                     ReferencedSchema: "dbo",
                     ReferencedTable: "Cliente",
                     ReferencedColumns: ["Id"],
                     OnDelete: ReferentialAction.NoAction,
                     OnUpdate: ReferentialAction.NoAction,
                     IsDisabled: false,
                     IsNotForReplication: false)],
                []);
        }

        ComparisonResult result = new(
        [
            new DifferencePair(Cliente(true).Identity, DifferenceStatus.Different, Cliente(true), Cliente(false)),
            new DifferencePair(Fattura(true).Identity, DifferenceStatus.Different, Fattura(true), Fattura(false)),
        ]);

        string sql = Sut.Generate(result);

        int dropFk = sql.IndexOf(
            "ALTER TABLE [dbo].[Fattura] DROP CONSTRAINT [FK_Fattura_Cliente];", StringComparison.Ordinal);
        int dropCliente = sql.IndexOf("DROP TABLE [dbo].[Cliente];", StringComparison.Ordinal);
        int readdFk = sql.IndexOf(
            "ALTER TABLE [dbo].[Fattura] ADD CONSTRAINT [FK_Fattura_Cliente] FOREIGN KEY",
            StringComparison.Ordinal);

        dropFk.Should().BeGreaterThan(0, "DROP TABLE [Cliente] fails with Msg 3726 while the FK stands");
        dropCliente.Should().BeGreaterThan(dropFk);
        readdFk.Should().BeGreaterThan(dropCliente, "and the FK must come back once both tables are rebuilt");
    }

    [Fact]
    public void Rebuild_drops_named_check_and_unique_constraints_along_with_PK()
    {
        // The old table carries a PK + a named CHECK constraint. The rebuild
        // must drop both up front so the _tmp create doesn't collide with
        // their names inside the shared schema.
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

    // ── DROP TABLE inside the rebuild takes the table's indexes, triggers and
    //    outbound FKs with it. Anything IDENTICAL on both sides is absent from
    //    the delta, so it used to be silently lost: deploy reported success and
    //    the re-compare reported Identical while production had lost the object.

    [Fact]
    public void Rebuild_recreates_every_index_including_the_identical_ones()
    {
        TableIndex ix = new("IX_Invoice_Amount", false, false, null,
            [new IndexColumn("Amount", false)], []);
        Table oldT = InvoiceWithPk(identityFlipped: false) with { Indexes = [ix] };
        Table newT = InvoiceWithPk(identityFlipped: true) with { Indexes = [ix] };
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result);

        int renameIdx = sql.IndexOf("sp_rename", StringComparison.Ordinal);
        int createIxIdx = sql.IndexOf("CREATE NONCLUSTERED INDEX [IX_Invoice_Amount]", StringComparison.Ordinal);
        renameIdx.Should().BeGreaterThan(0);
        createIxIdx.Should().BeGreaterThan(renameIdx, "the index must be re-created after the table is back");
        // Deleted here: a `NotContain("DROP INDEX [IX_Invoice_Amount]")` assert
        // that could not fail. The delta path never emits a DROP for an index
        // that is identical on both sides, so it held before the fix and after
        // it — and the comment justifying it claimed the opposite of what the
        // code does.
    }

    [Fact]
    public void Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled()
    {
        Table oldT = InvoiceWithPk(identityFlipped: false);
        Table newT = InvoiceWithPk(identityFlipped: true);
        Trigger trg = new(
            Schema: "dbo",
            Name: "trg_Invoice_Audit",
            Body: "CREATE TRIGGER dbo.trg_Invoice_Audit ON dbo.Invoice AFTER INSERT AS BEGIN SET NOCOUNT ON; END",
            IsEncrypted: false,
            ParentSchema: "dbo",
            ParentTable: "Invoice",
            IsDisabled: true,
            IsNotForReplication: false);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
            new DifferencePair(trg.Identity, DifferenceStatus.Identical, trg, trg),
        ]);

        string sql = Sut.Generate(result);

        int renameIdx = sql.IndexOf("sp_rename", StringComparison.Ordinal);
        int trgIdx = sql.IndexOf("TRIGGER dbo.trg_Invoice_Audit", StringComparison.Ordinal);
        trgIdx.Should().BeGreaterThan(renameIdx);
        // It was disabled before the rebuild; CREATE OR ALTER yields an enabled
        // trigger, so the disabled state has to be re-applied.
        sql.Should().Contain("DISABLE TRIGGER [dbo].[trg_Invoice_Audit] ON [dbo].[Invoice];");
    }

    [Fact]
    public void Rebuild_readds_its_own_outbound_fk_when_identical_on_both_sides()
    {
        ForeignKey outbound = new(
            Name: "FK_Invoice_Customer",
            Columns: ["CustomerId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Customer",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.NoAction,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);
        Table Build(bool flipped)
        {
            return new Table("dbo", "Invoice",
                [new Column("Id", "int", isNullable: false, ordinal: 1,
                    isIdentity: flipped, identitySeed: flipped ? 1 : null, identityIncrement: flipped ? 1 : null),
                 new Column("CustomerId", "int", isNullable: false, ordinal: 2)],
                [new PrimaryKey("PK_Invoice", ["Id"], IsClustered: true), outbound],
                []);
        }
        ComparisonResult result = new(
        [
            new DifferencePair(Build(true).Identity, DifferenceStatus.Different, Build(true), Build(false)),
        ]);

        string sql = Sut.Generate(result);

        int renameIdx = sql.IndexOf("sp_rename", StringComparison.Ordinal);
        int fkIdx = sql.IndexOf(
            "ALTER TABLE [dbo].[Invoice] ADD CONSTRAINT [FK_Invoice_Customer] FOREIGN KEY",
            StringComparison.Ordinal);
        fkIdx.Should().BeGreaterThan(renameIdx,
            "DROP TABLE took the outbound FK with it, so it must be re-added after the rename");
    }

    [Fact]
    public void Rebuild_tmp_table_carries_no_inline_default_for_a_named_default()
    {
        // The rebuild re-adds named constraints by name AFTER sp_rename. If the
        // _tmp table were created with the default inline, SQL Server would
        // auto-name it there and the later re-add would fail with "Column
        // already has a DEFAULT bound to it" — so the _tmp create must omit it.
        DefaultConstraint df = new("DF_Invoice_CreatedAt", "CreatedAt", "(sysutcdatetime())");
        Table src = new("dbo", "Invoice",
            [
                new Column("Id", "int", false, 1, isIdentity: true, identitySeed: 1, identityIncrement: 1),
                new Column("CreatedAt", "datetime2", false, 2, defaultExpression: "(sysutcdatetime())"),
            ],
            [df],
            []);
        Table tgt = new("dbo", "Invoice",
            [
                new Column("Id", "int", false, 1),
                new Column("CreatedAt", "datetime2", false, 2, defaultExpression: "(sysutcdatetime())"),
            ],
            [df],
            []);

        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt));

        // Sanity: this really is the rebuild path.
        sql.Should().Contain("[dbo].[Invoice_tmp]");
        // The default appears exactly once, as the post-rename named re-add.
        CountOccurrences(sql, "DEFAULT (sysutcdatetime())").Should().Be(1);
        sql.Should().Contain("ADD CONSTRAINT [DF_Invoice_CreatedAt] DEFAULT (sysutcdatetime()) FOR [CreatedAt];");
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
