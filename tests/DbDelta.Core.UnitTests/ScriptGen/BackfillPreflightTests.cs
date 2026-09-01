using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A NOT NULL column with no DEFAULT cannot be added to a populated table
/// (Msg 4901) — no tool can, without inventing a value. The first live smoke
/// died on exactly this, halfway through a long deploy. These cover finding it
/// BEFORE the script runs and applying the value an operator supplies.
/// </summary>
public class BackfillPreflightTests
{
    private static Table Target() =>
        new("dbo", "TipiDoc", [new Column("Id", "int", false, 1)]);

    private static Table SourceWith(params Column[] extra) =>
        new("dbo", "TipiDoc", [new Column("Id", "int", false, 1), .. extra]);

    private static ComparisonResult Compare(Table src, Table tgt) =>
        new([new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt)]);

    [Fact]
    public void A_not_null_column_with_no_default_is_reported()
    {
        Table src = SourceWith(new Column("Corriere", "nvarchar(16)", false, 2));

        IReadOnlyList<BackfillRequirement> found = BackfillPreflight.Scan(Compare(src, Target()));

        found.Should().ContainSingle();
        found[0].Should().BeEquivalentTo(
            new { Schema = "dbo", Table = "TipiDoc", Column = "Corriere", DataType = "nvarchar(16)" });
    }

    /// <summary>
    /// Every shape that CAN seed itself must stay out of the list, or the app
    /// interrogates the user about columns that were never a problem.
    /// </summary>
    [Fact]
    public void Columns_that_can_seed_themselves_are_not_reported()
    {
        Table src = SourceWith(
            new Column("Nullable", "int", true, 2),
            new Column("WithDefault", "int", false, 3, defaultExpression: "((0))"),
            new Column("Ident", "int", false, 4, isIdentity: true));

        BackfillPreflight.Scan(Compare(src, Target())).Should().BeEmpty();
    }

    [Fact]
    public void A_named_default_constraint_also_counts_as_seeded()
    {
        Table src = new(
            "dbo",
            "TipiDoc",
            [new Column("Id", "int", false, 1), new Column("Flag", "bit", false, 2)],
            [new DefaultConstraint("DF_TipiDoc_Flag", "Flag", "((0))")],
            []);

        BackfillPreflight.Scan(Compare(src, Target())).Should().BeEmpty();
    }

    /// <summary>
    /// The supplied value seeds the existing rows and then goes away, so the
    /// column is left exactly as the source declares it — NOT NULL, no default.
    /// Leaving the constraint behind would make the very next comparison report
    /// a difference the user never asked for.
    /// </summary>
    [Fact]
    public void The_supplied_value_seeds_the_rows_then_the_constraint_is_dropped()
    {
        Table src = SourceWith(new Column("Corriere", "nvarchar(16)", false, 2));
        Dictionary<(string, string, string), string> backfill = new()
        {
            [("dbo", "TipiDoc", "Corriere")] = "('GLS')",
        };

        string sql = new ScriptGenerator().Generate(
            Compare(src, Target()), backfillDefaults: backfill);

        sql.Should().Contain(
            "ADD [Corriere] [nvarchar] (16) NOT NULL CONSTRAINT [DF__dbdelta_backfill__Corriere] DEFAULT ('GLS');");
        sql.Should().Contain("DROP CONSTRAINT [DF__dbdelta_backfill__Corriere];");
    }

    [Fact]
    public void Without_a_supplied_value_the_column_is_emitted_unchanged()
    {
        Table src = SourceWith(new Column("Corriere", "nvarchar(16)", false, 2));

        string sql = new ScriptGenerator().Generate(Compare(src, Target()));

        sql.Should().Contain("ADD [Corriere] [nvarchar] (16) NOT NULL;");
        sql.Should().NotContain("dbdelta_backfill", "nothing may be invented on the user's behalf");
    }

    /// <summary>
    /// A stale entry — for a column since made nullable, or given a default —
    /// must be ignored rather than bind a constraint the schema never asked
    /// for. The dictionary comes from a dialog the user filled in earlier, so
    /// it can always describe a shape that no longer needs seeding.
    /// </summary>
    [Fact]
    public void A_value_supplied_for_a_column_that_does_not_need_one_is_ignored()
    {
        Table src = SourceWith(new Column("Nota", "nvarchar(16)", true, 2));
        Dictionary<(string, string, string), string> backfill = new()
        {
            [("dbo", "TipiDoc", "Nota")] = "('x')",
        };

        string sql = new ScriptGenerator().Generate(
            Compare(src, Target()), backfillDefaults: backfill);

        sql.Should().Contain("ADD [Nota] [nvarchar] (16) NULL;");
        sql.Should().NotContain("dbdelta_backfill", "a nullable column seeds itself with NULL");
    }

    [Theory]
    [InlineData("int", "((0))")]
    [InlineData("bit", "((0))")]
    [InlineData("nvarchar(16)", "('')")]
    [InlineData("datetime2", "(SYSUTCDATETIME())")]
    [InlineData("uniqueidentifier", "(NEWID())")]
    // Below: every arm the single `_ => "('')"` fallback used to swallow. All
    // measured on mssql/server:2022-latest, not reasoned about — ('') is
    // REFUSED by the server for each of these.
    [InlineData("varbinary(16)", "(0x)")]
    [InlineData("binary(4)", "(0x)")]
    [InlineData("image", "(0x)")]
    [InlineData("hierarchyid", "(hierarchyid::GetRoot())")]
    [InlineData("geometry", "(geometry::Parse('POINT EMPTY'))")]
    [InlineData("geography", "(geography::Parse('POINT EMPTY'))")]
    // ('') is right for these, and they are now LISTED rather than reached by
    // falling off the end of the switch.
    [InlineData("varchar(8)", "('')")]
    [InlineData("sysname", "('')")]
    [InlineData("xml", "('')")]
    [InlineData("sql_variant", "('')")]
    public void The_suggested_value_matches_the_column_type(string dataType, string expected) =>
        new BackfillRequirement("dbo", "T", "C", dataType).SuggestedValue.Should().Be(expected);

    /// <summary>
    /// A type the switch does not know gets NOTHING, not a string's answer.
    /// </summary>
    /// <remarks>
    /// The dialog refuses to confirm a blank row (BackfillViewModel.CanConfirm),
    /// so a blank asks the operator. The old fallback answered ('') for
    /// everything it did not recognise, which for half of those shapes was a
    /// statement the server rejects and for the other half a value it silently
    /// converted into something the operator never chose.
    /// </remarks>
    [Fact]
    public void A_type_with_no_dull_value_suggests_nothing_rather_than_guessing() =>
        new BackfillRequirement("dbo", "T", "C", "some_future_type").SuggestedValue.Should().BeEmpty();

    /// <summary>
    /// The defect this entry was opened for, and the reason it is not cosmetic:
    /// ('') on an alias over bigint does NOT fail. It stores 0.
    /// </summary>
    [Theory]
    [InlineData("bigint", "((0))")]
    [InlineData("datetime2", "(SYSUTCDATETIME())")]
    [InlineData("uniqueidentifier", "(NEWID())")]
    [InlineData("decimal", "((0))")]
    [InlineData("nvarchar", "('')")]
    [InlineData("varbinary", "(0x)")]
    public void An_alias_type_is_suggested_a_value_of_its_BASE_type(string baseType, string expected)
    {
        Table src = SourceWith(new Column("Codice", "MioTipo", false, 2)
        {
            IsUserDefinedType = true,
            TypeSchema = "app",
        });

        ComparisonResult result =
            CompareWithAlias(src, new UserDefinedType("app", "MioTipo", baseType, 8, 0, 0, false));

        IReadOnlyList<BackfillRequirement> found = BackfillPreflight.Scan(result);

        found.Should().ContainSingle();
        // The operator still reads the alias name, never the base type.
        found[0].DataType.Should().Be("MioTipo");
        found[0].SuggestedValue.Should().Be(expected);
    }

    /// <summary>
    /// An alias the deploy does not touch compares Identical, and Identical is
    /// the COMMON case — a lookup built only from Different pairs would miss
    /// exactly the columns this exists for.
    /// </summary>
    [Fact]
    public void An_alias_type_that_is_Identical_still_resolves()
    {
        Table src = SourceWith(new Column("Codice", "MioTipo", false, 2)
        {
            IsUserDefinedType = true,
            TypeSchema = "app",
        });
        UserDefinedType udt = new("app", "MioTipo", "bigint", 8, 0, 0, false);

        ComparisonResult result = new([
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, Target()),
            new DifferencePair(udt.Identity, DifferenceStatus.Identical, udt, udt),
        ]);

        BackfillPreflight.Scan(result)[0].SuggestedValue.Should().Be("((0))");
    }

    /// <summary>
    /// An alias in a schema the map does not hold must not borrow another
    /// type's base: no lookup, no BaseType, and a blank rather than a guess.
    /// </summary>
    [Fact]
    public void An_alias_the_lookup_cannot_find_suggests_nothing()
    {
        Table src = SourceWith(new Column("Codice", "MioTipo", false, 2)
        {
            IsUserDefinedType = true,
            TypeSchema = "altro",
        });

        ComparisonResult result =
            CompareWithAlias(src, new UserDefinedType("app", "MioTipo", "bigint", 8, 0, 0, false));

        BackfillPreflight.Scan(result)[0].SuggestedValue.Should().BeEmpty();
    }

    /// <summary>
    /// A rowversion column fills itself, so it must never be asked about at all.
    /// </summary>
    /// <remarks>
    /// Msg 4901 names the exception itself — "or the column being added is an
    /// identity or timestamp" — and identity was already filtered here while
    /// this was not. Asking would be worse than pointless: any DEFAULT on such
    /// a column is refused, so the answer the operator gives builds a statement
    /// the server rejects.
    /// </remarks>
    [Fact]
    public void A_rowversion_column_is_never_reported()
    {
        Table src = SourceWith(
            new Column("Ver", "timestamp", false, 2),
            new Column("Ver2", "rowversion", false, 3));

        BackfillPreflight.Scan(Compare(src, Target())).Should().BeEmpty();
    }

    private static ComparisonResult CompareWithAlias(Table src, UserDefinedType udt) =>
        new([
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, Target()),
            new DifferencePair(udt.Identity, DifferenceStatus.Different, udt, null),
        ]);
}
