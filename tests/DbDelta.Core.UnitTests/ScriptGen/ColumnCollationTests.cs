using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Explicit column collation handling. Two surfaces:
/// 1. The diff engine treats two columns with different <see cref="Column.Collation"/>
///    as a column-shape change (so the table is flagged Different and a delta
///    is emitted).
/// 2. The script generator ALWAYS emits an explicit <c>COLLATE &lt;name&gt;</c>
///    clause on string columns, matching Redgate SQL Compare, which emits the
///    explicit collation regardless of the target database default (parity run
///    2026-05-28). Non-string columns never carry a COLLATE clause.
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

    [Theory]
    [InlineData(DbDefault)]
    [InlineData(NonDefault)]
    public void OnlyInA_table_always_emits_explicit_COLLATE(string collation)
    {
        Table table = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
            new Column("Name", "nvarchar(100)", false, 2, collation: collation),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default);

        sql.Should().Contain($"[Name] [nvarchar] (100) COLLATE {collation} NOT NULL");
    }

    [Theory]
    [InlineData(DbDefault)]
    [InlineData(NonDefault)]
    public void ALTER_ADD_COLUMN_always_emits_explicit_COLLATE(string collation)
    {
        Table oldT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
        ]);
        Table newT = new("dbo", "Customer",
        [
            new Column("Id", "int", false, 1, isIdentity: true),
            new Column("Email", "nvarchar(200)", true, 2, collation: collation),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default);

        sql.Should().Contain($"ALTER TABLE [dbo].[Customer] ADD [Email] [nvarchar] (200) COLLATE {collation} NULL;");
    }

    [Fact]
    public void ALTER_COLUMN_emits_explicit_COLLATE_when_only_collation_changed()
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

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default);

        sql.Should().Contain($"ALTER TABLE [dbo].[Customer] ALTER COLUMN [Name] [nvarchar] (100) COLLATE {NonDefault} NOT NULL;");
    }

    [Theory]
    [InlineData(DbDefault)]
    [InlineData(NonDefault)]
    public void UDTT_always_emits_explicit_COLLATE(string collation)
    {
        TableTypeUdt udt = new("dbo", "OrderItemTvp",
        [
            new Column("ProductId", "int", isNullable: false, ordinal: 1),
            new Column("Notes", "nvarchar(100)", isNullable: true, ordinal: 2, collation: collation),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInA, udt, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default);

        sql.Should().Contain($"[Notes] [nvarchar] (100) COLLATE {collation} NULL");
        sql.Should().Contain("[ProductId] [int] NOT NULL");
    }

    [Fact]
    public void Non_string_column_never_carries_COLLATE()
    {
        // sys.columns.collation_name is NULL for non-character types — model
        // that as Column.Collation = null. A non-string column must never
        // synthesise a COLLATE clause.
        Table table = new("dbo", "T",
        [
            new Column("Id", "int", false, 1),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result, selection: null, options: ComparisonOptions.Default);

        sql.Should().NotContain("COLLATE");
    }
}
