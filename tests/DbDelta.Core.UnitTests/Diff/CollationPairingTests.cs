using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

/// <summary>
/// C2 — the engine must pair objects the way the TARGET resolves names. Getting
/// this wrong is not cosmetic: an unmatched pair is reported as source-only plus
/// target-only, and the generated script drops the live table with its data and
/// re-creates it empty while reporting success.
/// </summary>
public class CollationPairingTests
{
    private const string CaseInsensitive = "SQL_Latin1_General_CP1_CI_AS";
    private const string CaseSensitive = "SQL_Latin1_General_CP1_CS_AS";

    private static Database DbWith(string? collation, params Table[] tables) =>
        new("X", Schemas: [new Schema("dbo")], Tables: tables) { DefaultCollation = collation };

    private static Column[] Cols() => [new Column("Id", "int", false, 1)];

    [Fact]
    public void A_case_insensitive_target_pairs_names_that_differ_only_by_case()
    {
        Database source = DbWith(CaseInsensitive, new Table("dbo", "Clienti", Cols()));
        Database target = DbWith(CaseInsensitive, new Table("dbo", "CLIENTI", Cols()));

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        result.Differences.Should().ContainSingle()
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void A_case_sensitive_target_keeps_them_apart()
    {
        Database source = DbWith(CaseSensitive, new Table("dbo", "Clienti", Cols()));
        Database target = DbWith(CaseSensitive, new Table("dbo", "CLIENTI", Cols()));

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        result.Differences.Select(d => d.Status).Should().BeEquivalentTo(
            [DifferenceStatus.OnlyInB, DifferenceStatus.OnlyInA],
            "on a CS server these really are two objects");
    }

    /// <summary>
    /// The target decides, not the source: the target is the endpoint that will
    /// execute the DDL, so its collation is the one that determines whether the
    /// two names can coexist.
    /// </summary>
    [Fact]
    public void The_targets_collation_wins_when_the_two_sides_disagree()
    {
        Database source = DbWith(CaseSensitive, new Table("dbo", "Clienti", Cols()));
        Database target = DbWith(CaseInsensitive, new Table("dbo", "CLIENTI", Cols()));

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        result.Differences.Should().ContainSingle()
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void An_unknown_collation_is_treated_as_case_insensitive()
    {
        Database source = DbWith(null, new Table("dbo", "Clienti", Cols()));
        Database target = DbWith(null, new Table("dbo", "CLIENTI", Cols()));

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        result.Differences.Should().ContainSingle()
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    /// <summary>
    /// Columns matter as much as tables: an unmatched column becomes
    /// <c>DROP COLUMN</c> + <c>ADD</c>, which destroys the column's data.
    /// </summary>
    [Fact]
    public void Columns_are_paired_by_the_same_rule_as_their_table()
    {
        Database source = DbWith(
            CaseInsensitive,
            new Table("dbo", "Clienti", [new Column("Nome", "nvarchar(50)", false, 1)]));
        Database target = DbWith(
            CaseInsensitive,
            new Table("dbo", "Clienti", [new Column("NOME", "nvarchar(50)", false, 1)]));

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        result.Differences.Should().ContainSingle()
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Constraints_and_indexes_are_paired_by_the_same_rule_too()
    {
        static Table Build(string pkName, string ixName, string keyColumn)
        {
            return new Table(
                Schema: "dbo",
                Name: "Clienti",
                Columns: [new Column("Id", "int", false, 1)],
                Constraints: [new PrimaryKey(pkName, ["Id"], IsClustered: true)],
                Indexes:
                [
                    new TableIndex(
                        Name: ixName,
                        IsUnique: false,
                        IsClustered: false,
                        FilterExpression: null,
                        KeyColumns: [new IndexColumn(keyColumn, false)],
                        IncludedColumns: [])
                ]);
        }

        Database source = DbWith(CaseInsensitive, Build("PK_Clienti", "IX_Clienti_Id", "Id"));
        Database target = DbWith(CaseInsensitive, Build("pk_clienti", "ix_clienti_id", "ID"));

        ComparisonResult result = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        result.Differences.Should().ContainSingle()
            .Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    /// <summary>
    /// A case-sensitive source holding both spellings cannot be deployed to a
    /// case-insensitive target at all. Keeping one and dropping the other would
    /// make the loser disappear from the comparison entirely — the same silent
    /// truncation C3 exists to prevent — so the engine refuses instead.
    /// </summary>
    [Fact]
    public void Two_objects_that_collide_under_the_targets_collation_are_refused()
    {
        Database source = DbWith(
            CaseSensitive,
            new Table("dbo", "Clienti", Cols()),
            new Table("dbo", "CLIENTI", Cols()));
        Database target = DbWith(CaseInsensitive, new Table("dbo", "Clienti", Cols()));

        Action act = () => new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*more than once*");
    }

    [Theory]
    [InlineData("SQL_Latin1_General_CP1_CI_AS", false)]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", false)]
    [InlineData(null, false)]
    [InlineData("SQL_Latin1_General_CP1_CS_AS", true)]
    [InlineData("Latin1_General_BIN2", true)]
    [InlineData("Japanese_BIN", true)]
    public void Collation_names_map_to_the_right_comparer(string? collation, bool expectCaseSensitive)
    {
        StringComparer comparer = NameComparison.ForCollation(collation);

        comparer.Equals("Clienti", "CLIENTI").Should().Be(!expectCaseSensitive);
    }
}
