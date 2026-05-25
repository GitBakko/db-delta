using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// M13-PARITY.5 #32 — explicit column collation handling. Two surfaces:
/// 1. The diff engine treats two columns with different <see cref="Column.Collation"/>
///    as a column-shape change (so the table is flagged Different and a delta
///    is emitted).
/// 2. The script generator emits an explicit <c>COLLATE &lt;name&gt;</c>
///    clause only when the column's collation diverges from the target
///    database's default collation — matching Redgate SQL Compare's
///    behaviour observed in parity scenarios 01 and 11.
/// </summary>
public class ColumnCollationTests
{
    private const string DbDefault = "Latin1_General_CI_AS";
    private const string NonDefault = "SQL_Latin1_General_CP1_CI_AS";

    private static readonly ScriptGenerator Sut = new();

    [Fact]
    public void Engine_flags_collation_only_change_as_Different()
    {
        Table src = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
            new Column("Name", "nvarchar(100)", false, 2, collation: NonDefault),
        ]);
        Table tgt = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
            new Column("Name", "nvarchar(100)", false, 2, collation: DbDefault),
        ]);
        Database a = new("Db", [new Schema("dbo")], [src]) { DefaultCollation = DbDefault };
        Database b = new("Db", [new Schema("dbo")], [tgt]) { DefaultCollation = DbDefault };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "Table")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Engine_treats_matching_collation_as_Identical()
    {
        Table src = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
            new Column("Name", "nvarchar(100)", false, 2, collation: DbDefault),
        ]);
        Table tgt = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
            new Column("Name", "nvarchar(100)", false, 2, collation: DbDefault),
        ]);
        Database a = new("Db", [new Schema("dbo")], [src]) { DefaultCollation = DbDefault };
        Database b = new("Db", [new Schema("dbo")], [tgt]) { DefaultCollation = DbDefault };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "Table")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void OnlyInA_table_skips_COLLATE_when_column_matches_target_default()
    {
        Table table = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
            new Column("Name", "nvarchar(100)", false, 2, collation: DbDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: DbDefault);

        sql.Should().Contain("[Name] nvarchar(100) NOT NULL");
        sql.Should().NotContain("COLLATE");
    }

    [Fact]
    public void OnlyInA_table_emits_COLLATE_when_column_diverges_from_target_default()
    {
        Table table = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
            new Column("Name", "nvarchar(100)", false, 2, collation: NonDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: DbDefault);

        sql.Should().Contain($"[Name] nvarchar(100) COLLATE {NonDefault} NOT NULL");
    }

    [Fact]
    public void ALTER_ADD_COLUMN_emits_COLLATE_when_column_diverges_from_target_default()
    {
        Table oldT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
        ]);
        Table newT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
            new Column("Email", "nvarchar(200)", true, 2, collation: NonDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: DbDefault);

        sql.Should().Contain($"ALTER TABLE [dbo].[Customer] ADD [Email] nvarchar(200) COLLATE {NonDefault} NULL;");
    }

    [Fact]
    public void ALTER_ADD_COLUMN_omits_COLLATE_when_column_matches_target_default()
    {
        Table oldT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
        ]);
        Table newT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
            new Column("Email", "nvarchar(200)", true, 2, collation: DbDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: DbDefault);

        sql.Should().Contain("ALTER TABLE [dbo].[Customer] ADD [Email] nvarchar(200) NULL;");
        sql.Should().NotContain("COLLATE");
    }

    [Fact]
    public void ALTER_COLUMN_emits_COLLATE_when_only_collation_changed()
    {
        Table oldT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
            new Column("Name", "nvarchar(100)", false, 2, collation: DbDefault),
        ]);
        Table newT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
            new Column("Name", "nvarchar(100)", false, 2, collation: NonDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: DbDefault);

        sql.Should().Contain($"ALTER TABLE [dbo].[Customer] ALTER COLUMN [Name] nvarchar(100) COLLATE {NonDefault} NOT NULL;");
    }

    [Fact]
    public void UDTT_emits_COLLATE_when_column_diverges_from_target_default()
    {
        TableTypeUdt udt = new("dbo", "OrderItemTvp",
        [
            new Column("ProductId", "int", isNullable: false, ordinal: 1),
            new Column("Notes", "nvarchar(100)", isNullable: true, ordinal: 2, collation: NonDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInA, udt, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: DbDefault);

        sql.Should().Contain($"[Notes] nvarchar(100) COLLATE {NonDefault} NULL");
        sql.Should().Contain("[ProductId] int NOT NULL");
    }

    [Fact]
    public void UDTT_omits_COLLATE_when_column_matches_target_default()
    {
        TableTypeUdt udt = new("dbo", "OrderItemTvp",
        [
            new Column("ProductId", "int", isNullable: false, ordinal: 1),
            new Column("Notes", "nvarchar(100)", isNullable: true, ordinal: 2, collation: DbDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInA, udt, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: DbDefault);

        sql.Should().Contain("[Notes] nvarchar(100) NULL");
        sql.Should().NotContain("COLLATE");
    }

    [Fact]
    public void Non_string_column_never_carries_COLLATE_even_with_unknown_default()
    {
        // sys.columns.collation_name is NULL for non-character types — model
        // that as Column.Collation = null. Even when the target default is
        // unknown (null), the emitter must not synthesise a COLLATE clause.
        Table table = new("dbo", "T",
        [
            new Column("Id", "int", false, 1),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: null);

        sql.Should().NotContain("COLLATE");
    }

    [Fact]
    public void Unknown_target_default_falls_back_to_defensive_explicit_COLLATE()
    {
        // When the target default is unknown (null) we cannot prove the
        // column inherits it — emit the explicit clause so the script is
        // unambiguous on apply. Mirrors Redgate's always-explicit shape.
        Table table = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1),
            new Column("Name", "nvarchar(100)", false, 2, collation: DbDefault),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default, targetDefaultCollation: null);

        sql.Should().Contain($"[Name] nvarchar(100) COLLATE {DbDefault} NOT NULL");
    }
}
