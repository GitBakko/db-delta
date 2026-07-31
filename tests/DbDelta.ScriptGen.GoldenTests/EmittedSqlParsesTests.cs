using System.Runtime.CompilerServices;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using Microsoft.SqlServer.TransactSql.ScriptDom;
// ScriptDom also defines a `Permission` type; ours is the schema object.
using Permission = DbDelta.Core.ObjectModel.Permission;

namespace DbDelta.ScriptGen.GoldenTests;

/// <summary>
/// Parses everything this repo claims is valid T-SQL, with the same parser SQL
/// Server's own tooling uses.
/// </summary>
/// <remarks>
/// <para>
/// A tool whose entire output is DDL had no mechanical check that the DDL was
/// even syntactically valid — every assertion in the suite was a hand-written
/// <c>Contain</c> or index comparison, which pins wording and ordering but never
/// grammar. Two consequences shipped: a golden test asserted a table-level
/// <c>CONSTRAINT [x] DEFAULT (e) FOR [c]</c> inside CREATE TABLE (DEFAULT is not
/// in the <c>table_constraint</c> grammar — Msg 102), and a unit test asserted
/// <c>GRANT … ON DATABASE</c> (not valid either). Both were pinned AS CORRECT for
/// months.
/// </para>
/// <para>
/// <c>Microsoft.SqlServer.TransactSql.ScriptDom</c> was already a PackageReference
/// on DbDelta.Core and used by nothing at all, so this costs a file.
/// </para>
/// <para>
/// The parser is a syntax check, not a semantic one: it will not catch a
/// reference to a table that does not exist, or an ALTER blocked by a dependency.
/// Those need the live round-trip tests. What it does catch is the entire class
/// of "we emitted something SQL Server cannot even parse", cheaply and for every
/// golden that will ever be added.
/// </para>
/// </remarks>
public class EmittedSqlParsesTests
{
    /// <summary>
    /// Every <c>.verified.txt</c> snapshot in this project, so a new golden is
    /// covered the moment it is written without anyone remembering to opt in.
    /// </summary>
    public static TheoryData<string> GoldenFiles()
    {
        TheoryData<string> data = [];
        foreach (string path in Directory.EnumerateFiles(GoldenDirectory(), "*.verified.txt"))
        {
            data.Add(Path.GetFileName(path));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public async Task Golden_snapshot_is_parseable_tsql(string fileName)
    {
        string sql = await File.ReadAllTextAsync(Path.Combine(GoldenDirectory(), fileName));
        AssertParses(sql, fileName);
    }

    [Fact]
    public void Generated_deploy_script_is_parseable_tsql()
    {
        // The real product output: the full envelope plus every pass — schema
        // creates, the up-front FK and index drops, the object drops, the topo
        // CREATE pass, the rebuild dance, the index and FK re-adds, schema drops.
        string sql = new ScriptGenerator().Generate(RichComparison(), options: ComparisonOptions.Default);
        AssertParses(sql, "ScriptGenerator.Generate");
    }

    [Fact]
    public void Generated_deploy_script_is_parseable_with_permissions_included()
    {
        string sql = new ScriptGenerator().Generate(
            RichComparison(),
            options: ComparisonOptions.Default & ~ComparisonOptions.IgnorePermissions);
        AssertParses(sql, "ScriptGenerator.Generate(+permissions)");
    }

    /// <summary>
    /// S11 — the whole point of quoting identifiers is that the emitted script
    /// still parses when a catalog name contains a closing bracket. Before it,
    /// the name terminated its own identifier and the remainder became script
    /// text, which is the difference between a broken deploy and an arbitrary
    /// statement running with the deploy's privileges.
    /// </summary>
    [Fact]
    public void Generated_deploy_script_is_parseable_when_names_contain_a_closing_bracket()
    {
        const string nasty = "Ev]il";
        Table hostile = new(
            Schema: nasty,
            Name: nasty,
            Columns: [new Column(nasty, "nvarchar(50)", false, 1)],
            Constraints: [new PrimaryKey(nasty, [nasty], IsClustered: true)],
            Indexes:
            [
                new TableIndex(nasty, false, false, null, [new IndexColumn(nasty, false)], [])
            ]);

        string sql = new ScriptGenerator().Generate(new ComparisonResult(
        [
            new DifferencePair(new ObjectIdentity(nasty, nasty, "Schema"),
                DifferenceStatus.OnlyInA, new Schema(nasty), null),
            new DifferencePair(hostile.Identity, DifferenceStatus.OnlyInA, hostile, null),
        ]));

        AssertParses(sql, "ScriptGenerator.Generate(hostile identifiers)");
    }

    /// <summary>
    /// Negative control: the gate must REJECT the two forms this repo actually
    /// shipped and pinned as correct.
    /// </summary>
    /// <remarks>
    /// A gate nobody has watched fail is worth nothing — it could be passing
    /// because it validates everything. These two cases are the historical
    /// defects, so they prove the parser catches the class of bug the gate exists
    /// for. If either of these ever starts parsing, the gate has gone blind and
    /// the assertions above stopped meaning anything.
    /// </remarks>
    [Theory]
    // DEFAULT is absent from the table_constraint grammar: valid only inline on
    // the column, or via ALTER TABLE ADD CONSTRAINT. A golden pinned this form.
    [InlineData("""
        CREATE TABLE [dbo].[Person] (
            [Id] [int] NOT NULL,
            [CreatedAt] [datetime2] (7) NOT NULL,
            CONSTRAINT [DF_Person_CreatedAt] DEFAULT (sysutcdatetime()) FOR [CreatedAt]
        );
        """)]
    // A database-scoped grant takes no ON clause; the only ON form is
    // ON DATABASE::[name]. A unit test pinned this form.
    [InlineData("GRANT CONNECT ON DATABASE TO [app];")]
    public void Gate_rejects_the_invalid_forms_this_repo_used_to_emit(string invalidSql)
    {
        TSql160Parser parser = new(initialQuotedIdentifiers: true);
        using StringReader reader = new(invalidSql);
        _ = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count == 0)
        {
            throw new InvalidOperationException(
                "The parse gate accepted T-SQL that SQL Server rejects, so it can no "
                + $"longer catch the defect class it exists for:{Environment.NewLine}{invalidSql}");
        }
    }

    /// <summary>
    /// A comparison that walks as many emission paths as one fixture can: a new
    /// schema holding a new table, a dropped table with an inbound FK from an
    /// Identical table, an identity rebuild, an indexed column being retyped, a
    /// module, a trigger, a sequence and a permission.
    /// </summary>
    private static ComparisonResult RichComparison()
    {
        Schema sales = new("sales");

        Table newTable = new("sales", "Order",
            [new Column("Id", "int", false, 1, isIdentity: true, identitySeed: 1, identityIncrement: 1),
             new Column("Total", "decimal(18,2)", false, 2, defaultExpression: "((0))")],
            [new PrimaryKey("PK_Order", ["Id"], IsClustered: true),
             new DefaultConstraint("DF_Order_Total", "Total", "((0))")],
            [new TableIndex("IX_Order_Total", false, false, null, [new IndexColumn("Total", false)], [])]);

        Table droppedTable = new("dbo", "Currency", [new Column("Id", "int", false, 1)]);

        ForeignKey inbound = new(
            Name: "FK_Invoice_Currency",
            Columns: ["CurrencyId"],
            ReferencedSchema: "dbo",
            ReferencedTable: "Currency",
            ReferencedColumns: ["Id"],
            OnDelete: ReferentialAction.NoAction,
            OnUpdate: ReferentialAction.NoAction,
            IsDisabled: false,
            IsNotForReplication: false);
        Table identicalHolder = new("dbo", "Invoice",
            [new Column("Id", "int", false, 1), new Column("CurrencyId", "int", false, 2)],
            [inbound],
            []);

        // Identity flip → rebuild, with an index and a CHECK to be re-created.
        Table RebuiltSide(bool identity)
        {
            return new Table("dbo", "Movimento",
                [new Column("Id", "int", false, 1,
                    isIdentity: identity, identitySeed: identity ? 1 : null, identityIncrement: identity ? 1 : null),
                 new Column("Importo", "decimal(18,2)", false, 2)],
                [new PrimaryKey("PK_Movimento", ["Id"], IsClustered: true),
                 new CheckConstraint("CK_Movimento_Importo", "([Importo] >= (0))", IsDisabled: false, IsNotForReplication: false)],
                [new TableIndex("IX_Movimento_Importo", false, false, null, [new IndexColumn("Importo", false)], [])]);
        }

        // Indexed column retyped → blocking-index drop + forced re-create.
        Table RetypedSide(string type)
        {
            return new Table("dbo", "Cliente",
                [new Column("Id", "int", false, 1), new Column("Codice", type, false, 2)],
                [new UniqueConstraint("UQ_Cliente_Codice", ["Codice"], IsClustered: false)],
                [new TableIndex("IX_Cliente_Codice", false, false, null, [new IndexColumn("Codice", false)], [])]);
        }

        View view = new("sales", "vOrdini", "CREATE VIEW sales.vOrdini AS SELECT Id, Total FROM sales.[Order]", false);
        Trigger trigger = new("dbo", "trg_Movimento_Audit",
            "CREATE TRIGGER dbo.trg_Movimento_Audit ON dbo.Movimento AFTER INSERT AS BEGIN SET NOCOUNT ON; END",
            IsEncrypted: false, ParentSchema: "dbo", ParentTable: "Movimento",
            IsDisabled: true, IsNotForReplication: false);
        Sequence sequence = new("dbo", "SeqOrder", "bigint", 1, 1, null, null,
            IsCycling: false, IsCached: false, CacheSize: null);
        Permission permission = new("app_writer", "EXECUTE", PermissionState.Grant,
            "OBJECT_OR_COLUMN", "dbo", "uspChiudi", null);

        return new ComparisonResult(
        [
            new DifferencePair(sales.Identity, DifferenceStatus.OnlyInA, sales, null),
            new DifferencePair(newTable.Identity, DifferenceStatus.OnlyInA, newTable, null),
            new DifferencePair(droppedTable.Identity, DifferenceStatus.OnlyInB, null, droppedTable),
            new DifferencePair(identicalHolder.Identity, DifferenceStatus.Identical, identicalHolder, identicalHolder),
            new DifferencePair(RebuiltSide(true).Identity, DifferenceStatus.Different, RebuiltSide(true), RebuiltSide(false)),
            new DifferencePair(RetypedSide("nvarchar(50)").Identity, DifferenceStatus.Different,
                RetypedSide("nvarchar(50)"), RetypedSide("nvarchar(20)")),
            new DifferencePair(view.Identity, DifferenceStatus.OnlyInA, view, null),
            new DifferencePair(trigger.Identity, DifferenceStatus.Identical, trigger, trigger),
            new DifferencePair(sequence.Identity, DifferenceStatus.OnlyInA, sequence, null),
            new DifferencePair(permission.Identity, DifferenceStatus.OnlyInA, permission, null),
        ]);
    }

    /// <summary>
    /// Fails with every parse error, quoting the offending line, when
    /// <paramref name="sql"/> is not syntactically valid T-SQL.
    /// </summary>
    private static void AssertParses(string sql, string what)
    {
        if (string.IsNullOrWhiteSpace(sql)) { return; }

        // QUOTED_IDENTIFIER ON matches the deploy script's own preamble, and so
        // matches how SQL Server will actually parse what we emit.
        TSql160Parser parser = new(initialQuotedIdentifiers: true);
        using StringReader reader = new(sql);
        _ = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count == 0) { return; }

        string[] lines = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string detail = string.Join(
            Environment.NewLine,
            errors.Select(e =>
            {
                string source = e.Line >= 1 && e.Line <= lines.Length ? lines[e.Line - 1].Trim() : "(line out of range)";
                return $"  [{e.Number}] line {e.Line} col {e.Column}: {e.Message}{Environment.NewLine}      > {source}";
            }));

        // This project has no xunit.assert (only extensibility.core, via
        // Verify.Xunit — the csproj warns against adding xunit to avoid a
        // FactAttribute collision), so fail by throwing: xunit reports the
        // message either way.
        throw new InvalidOperationException(
            $"{what} is not valid T-SQL — {errors.Count} parse error(s):{Environment.NewLine}{detail}");
    }

    /// <summary>
    /// The source directory of this file, which is where Verify keeps the
    /// snapshots. Tests execute from <c>bin/</c>, so the path cannot be derived
    /// from the working directory.
    /// </summary>
    private static string GoldenDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;
}
