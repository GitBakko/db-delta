using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Regressions from the C2 wave. Making the engine pair <c>dbo.Clienti</c> with
/// <c>dbo.CLIENTI</c> put both spellings of one table into the same run for the
/// first time — and the generator keys a dozen sets on value tuples of strings,
/// whose equality is ordinal. A set filled from the source side and probed from
/// the target side then silently missed.
/// </summary>
public class CaseDriftKeyTests
{
    private const string Ci = "SQL_Latin1_General_CP1_CI_AS";

    private static Database Db(params Table[] tables) =>
        new("X", [new Schema("dbo")], tables) { DefaultCollation = Ci };

    private static ForeignKey Fk(string name, string column, string refTable, string refColumn) => new(
        Name: name,
        Columns: [column],
        ReferencedSchema: "dbo",
        ReferencedTable: refTable,
        ReferencedColumns: [refColumn],
        OnDelete: ReferentialAction.NoAction,
        OnUpdate: ReferentialAction.NoAction,
        IsDisabled: false,
        IsNotForReplication: false);

    private static string Generate(Database source, Database target)
    {
        ComparisonResult r = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        return new ScriptGenerator().Generate(r);
    }

    /// <summary>
    /// The parent is an identity rebuild, so its DROP TABLE needs the child's
    /// foreign key gone first. rebuildTargets was filled from the SOURCE
    /// spelling and probed with the TARGET's, so the drop was never emitted and
    /// the deploy died on Msg 3726 — with XACT_ABORT rolling everything back.
    /// </summary>
    [Fact]
    public void An_inbound_fk_to_a_rebuilt_table_is_dropped_even_when_the_table_name_case_drifts()
    {
        Database source = Db(
            new Table("dbo", "Clienti",
                [new Column("Id", "int", false, 1, isIdentity: true)],
                [new PrimaryKey("PK_Clienti", ["Id"], IsClustered: true)], []),
            new Table("dbo", "Ordini",
                [new Column("Id", "int", false, 1), new Column("ClienteId", "int", false, 2)],
                [Fk("FK_Ordini_Clienti", "ClienteId", "Clienti", "Id")], []));

        Database target = Db(
            new Table("dbo", "CLIENTI",
                [new Column("Id", "int", false, 1)],
                [new PrimaryKey("PK_Clienti", ["Id"], IsClustered: true)], []),
            new Table("dbo", "ORDINI",
                [new Column("Id", "int", false, 1), new Column("ClienteId", "int", false, 2)],
                [Fk("FK_Ordini_Clienti", "ClienteId", "CLIENTI", "Id")], []));

        string sql = Generate(source, target);

        int dropFk = sql.IndexOf("DROP CONSTRAINT [FK_Ordini_Clienti]", StringComparison.Ordinal);
        int dropTable = sql.IndexOf("DROP TABLE [dbo].[Clienti];", StringComparison.Ordinal);

        dropFk.Should().BeGreaterThan(0, "Msg 3726 fires on the rebuild's DROP TABLE while the FK stands");
        dropTable.Should().BeGreaterThan(dropFk);
    }

    /// <summary>
    /// Two feeders claimed the same constraint under two spellings of its
    /// holder, and the ordinal dedupe key did not collapse them. The second
    /// DROP CONSTRAINT is Msg 3728 and aborts the deploy.
    /// </summary>
    [Fact]
    public void A_foreign_key_claimed_under_both_spellings_is_dropped_exactly_once()
    {
        Database source = Db(
            new Table("dbo", "Testa",
                [new Column("Id", "bigint", false, 1)],
                [new PrimaryKey("PK_Testa", ["Id"], IsClustered: true)], []),
            new Table("dbo", "Righe",
                [new Column("TestaId", "bigint", false, 1)],
                [Fk("FK_Righe_Testa", "TestaId", "Testa", "Id")], []));

        Database target = Db(
            new Table("dbo", "Testa",
                [new Column("Id", "int", false, 1)],
                [new PrimaryKey("PK_Testa", ["Id"], IsClustered: true)], []),
            new Table("dbo", "RIGHE",
                [new Column("TestaId", "int", false, 1)],
                [Fk("FK_Righe_Testa", "TestaId", "Testa", "Id")], []));

        string sql = Generate(source, target);

        CountOf(sql, "DROP CONSTRAINT [FK_Righe_Testa]").Should().Be(
            1, "a second drop of the same constraint is Msg 3728");
    }

    /// <summary>
    /// A partial selection: the user retypes the parent's key but leaves the
    /// target-only child alone. The child survives the deploy, so the key we
    /// drop to free the column has to come back — the source has no shape to
    /// restore it from, and dropping without restoring loses the child's
    /// referential integrity under a success verdict.
    /// </summary>
    [Fact]
    public void A_foreign_key_on_an_unselected_target_only_table_is_restored_not_just_dropped()
    {
        Table parentSrc = new("dbo", "Parent",
            [new Column("Id", "bigint", false, 1)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);
        Table parentTgt = new("dbo", "Parent",
            [new Column("Id", "int", false, 1)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);
        Table legacy = new("dbo", "Legacy",
            [new Column("ParentId", "int", false, 1)],
            [Fk("FK_Legacy_Parent", "ParentId", "Parent", "Id")], []);

        DifferencePair parentPair = new(parentSrc.Identity, DifferenceStatus.Different, parentSrc, parentTgt);
        ComparisonResult result = new(
        [
            parentPair,
            new DifferencePair(legacy.Identity, DifferenceStatus.OnlyInB, null, legacy),
        ]);

        // Only the parent is ticked — the user is not ready to drop Legacy.
        string sql = new ScriptGenerator().Generate(result, selection: [parentPair]);

        sql.Should().Contain("DROP CONSTRAINT [FK_Legacy_Parent]");
        sql.Should().Contain("ADD CONSTRAINT [FK_Legacy_Parent]");
        sql.Should().NotContain("DROP TABLE [dbo].[Legacy]", "the user did not select it");
    }

    /// <summary>
    /// EmitAlter compared key column lists ordinally, so a primary key that is
    /// the same key on a case-insensitive server was dropped and rebuilt — on a
    /// clustered key that is a full table rebuild inside a batch with a timeout.
    /// </summary>
    [Fact]
    public void An_unchanged_primary_key_survives_a_column_case_difference()
    {
        Table source = new("dbo", "T",
            [new Column("Id", "int", false, 1), new Column("Nota", "nvarchar(10)", true, 2)],
            [new PrimaryKey("PK_T", ["Id"], IsClustered: true)], []);
        Table target = new("dbo", "T",
            [new Column("ID", "int", false, 1)],
            [new PrimaryKey("PK_T", ["ID"], IsClustered: true)], []);

        string sql = new TableScriptEmitter(StringComparer.OrdinalIgnoreCase).Emit(
            new DifferencePair(source.Identity, DifferenceStatus.Different, source, target));

        sql.Should().Contain("ADD [Nota]");
        sql.Should().NotContain("DROP CONSTRAINT [PK_T]", "the key covers the same column");
    }

    /// <summary>
    /// The last two ordinal pairings in the generator: the index delta and the
    /// foreign-key ADD half. On a case-insensitive target <c>IX_Data</c> and
    /// <c>IX_DATA</c> are one index, but pairing them ordinally made one look
    /// removed and the other added — so a byte-identical index was dropped and
    /// rebuilt, minutes of table lock inside a batch with a 60 s cap.
    /// </summary>
    [Fact]
    public void An_unchanged_index_and_foreign_key_survive_a_name_case_difference()
    {
        Table parent = new("dbo", "Parent", [new Column("Id", "int", false, 1)]);
        TableIndex ixSrc = new("IX_Data", false, false, null, [new IndexColumn("ParentId", false)], []);
        TableIndex ixTgt = new("IX_DATA", false, false, null, [new IndexColumn("PARENTID", false)], []);

        Database source = Db(
            parent,
            new Table("dbo", "Child",
                [new Column("ParentId", "int", false, 1), new Column("Nota", "nvarchar(10)", true, 2)],
                [Fk("FK_Child_Parent", "ParentId", "Parent", "Id")],
                [ixSrc]));

        Database target = Db(
            parent,
            new Table("dbo", "Child",
                [new Column("PARENTID", "int", false, 1)],
                [Fk("FK_CHILD_PARENT", "PARENTID", "PARENT", "ID")],
                [ixTgt]));

        string sql = Generate(source, target);

        sql.Should().Contain("ADD [Nota]", "the run has a real change to make");
        sql.Should().NotContain("DROP INDEX", "the two spellings are one index on this server");
        sql.Should().NotContain(
            "ADD CONSTRAINT [FK_Child_Parent]",
            "the two spellings are one foreign key on this server");
    }

    /// <summary>
    /// The permission comparer was the one left keyed ordinally. A securable
    /// spelled differently on the two sides produced two rows, and deploying
    /// them emitted the GRANT and then a REVOKE of the same permission.
    /// </summary>
    [Fact]
    public void A_permission_on_a_case_differing_securable_is_one_identical_row()
    {
        static Permission Grant(string table)
        {
            return new Permission(
                GranteeName: "app",
                Action: "SELECT",
                State: PermissionState.Grant,
                ClassDesc: "OBJECT_OR_COLUMN",
                ObjectSchema: "dbo",
                ObjectName: table,
                ColumnName: null);
        }

        Database source = new("X", [new Schema("dbo")], []) { DefaultCollation = Ci, Permissions = [Grant("CLIENTI")] };
        Database target = new("X", [new Schema("dbo")], []) { DefaultCollation = Ci, Permissions = [Grant("Clienti")] };

        ComparisonResult r = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        r.Differences.Should().ContainSingle()
            .Which.Status.Should().Be(DifferenceStatus.Identical, "the server sees one permission, not two");
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }
}
