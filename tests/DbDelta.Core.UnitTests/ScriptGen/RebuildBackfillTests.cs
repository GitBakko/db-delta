using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A rebuild copies the rows with <c>INSERT … SELECT</c> over the columns the
/// two tables share, so a column only the SOURCE has gets whatever the
/// <c>_tmp</c> table can put there by itself. For a NOT NULL column that is
/// nothing, and the copy dies on Msg 515 — halfway through a script, in front
/// of the user. The transaction rolls it back, so nothing is lost except the
/// deploy.
/// </summary>
/// <remarks>
/// Two holes, one cause. The named DEFAULT constraints are deliberately kept
/// off <c>_tmp</c> (their names still belong to the table being replaced), so
/// a column whose default is named has none while the copy runs. And
/// <see cref="TableScriptEmitter.ColumnsNeedingABackfillDefault"/> returned
/// nothing at all for a rebuild, so the operator was never asked for the value
/// in the one case where no default exists to fall back on.
/// </remarks>
public class RebuildBackfillTests
{
    private static Column Id(bool identity) =>
        new("Id", "int", isNullable: false, ordinal: 1,
            isIdentity: identity, identitySeed: identity ? 1 : null, identityIncrement: identity ? 1 : null);

    private static readonly Column Name = new("Name", "nvarchar(100)", isNullable: false, ordinal: 2);

    /// <summary>The old table: Id is a plain int, so any new Id forces a rebuild.</summary>
    private static Table Old() => new("dbo", "Customer", [Id(identity: false), Name], [], []);

    private static Table New(Column extra, params Constraint[] constraints) =>
        new("dbo", "Customer", [Id(identity: true), Name, extra], constraints, []);

    private static string Emit(Table newT, TableScriptEmitter? emitter = null) =>
        (emitter ?? new TableScriptEmitter()).Emit(
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, Old()));

    private static Column Stato(bool nullable = false, string? inlineDefault = null) =>
        new("Stato", "int", isNullable: nullable, ordinal: 3, defaultExpression: inlineDefault);

    [Fact]
    public void A_rebuild_carries_a_named_default_into_the_copy()
    {
        Table newT = New(Stato(), new DefaultConstraint("DF_Customer_Stato", "Stato", "((0))"));

        string sql = Emit(newT);

        sql.Should().Contain("INSERT INTO [dbo].[Customer_tmp] ([Id], [Name], [Stato]) "
                           + "SELECT [Id], [Name], ((0)) FROM [dbo].[Customer];");
    }

    [Fact]
    public void A_rebuild_uses_the_value_the_operator_supplied()
    {
        Table newT = New(Stato());
        TableScriptEmitter emitter = new(
            names: null,
            backfillDefaults: new Dictionary<(string, string, string), string>
            {
                [("dbo", "Customer", "Stato")] = "(42)",
            });

        string sql = Emit(newT, emitter);

        sql.Should().Contain("INSERT INTO [dbo].[Customer_tmp] ([Id], [Name], [Stato]) "
                           + "SELECT [Id], [Name], (42) FROM [dbo].[Customer];");
    }

    [Fact]
    public void The_backfill_preflight_asks_about_a_rebuild_too()
    {
        // It used to return nothing the moment a rebuild was in play, which is
        // exactly the case where nobody else can supply the value.
        Table newT = New(Stato());

        TableScriptEmitter.ColumnsNeedingABackfillDefault(newT, Old(), StringComparer.Ordinal)
            .Should().Equal("Stato");
    }

    [Fact]
    public void An_inline_default_is_left_to_the_tmp_table_that_already_carries_it()
    {
        // The negative control on the fix: CREATE TABLE [Customer_tmp] writes
        // this default itself, so naming the column in the INSERT would only
        // repeat it. Nothing to carry means nothing to add.
        Table newT = New(Stato(inlineDefault: "((7))"));

        string sql = Emit(newT);

        sql.Should().Contain("[Stato] [int] NOT NULL DEFAULT ((7))");
        sql.Should().Contain("INSERT INTO [dbo].[Customer_tmp] ([Id], [Name]) "
                           + "SELECT [Id], [Name] FROM [dbo].[Customer];");
    }

    [Fact]
    public void A_nullable_new_column_stays_out_of_the_copy()
    {
        // The other negative control: NULL is a perfectly good value here, and
        // the preflight must not ask for one.
        Table newT = New(Stato(nullable: true));

        Emit(newT).Should().Contain("INSERT INTO [dbo].[Customer_tmp] ([Id], [Name]) "
                                  + "SELECT [Id], [Name] FROM [dbo].[Customer];");
        TableScriptEmitter.ColumnsNeedingABackfillDefault(newT, Old(), StringComparer.Ordinal)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_rebuild_with_no_new_column_still_copies_only_what_both_sides_have()
    {
        Table newT = new("dbo", "Customer", [Id(identity: true), Name], [], []);

        Emit(newT).Should().Contain("INSERT INTO [dbo].[Customer_tmp] ([Id], [Name]) "
                                  + "SELECT [Id], [Name] FROM [dbo].[Customer];");
    }
}
