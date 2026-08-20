using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A column typed with a user-defined alias type must NOT carry a COLLATE
/// clause: SQL Server answers "COLLATE clause cannot be used on user-defined
/// data types" and the whole deploy stops there.
/// </summary>
/// <remarks>
/// <para>
/// The rule everywhere else is the opposite and stays that way — an explicit
/// COLLATE on every string column, matching Redgate, deliberately re-introduced
/// after being optimised away once. An alias type is the one exception, and it
/// exists because the collation is a property of the TYPE, not of the column
/// that uses it.
/// </para>
/// <para>
/// Found by the live round-trip, not by reading: <c>sys.columns</c> reports a
/// collation for these columns exactly as it does for an <c>nvarchar</c> one,
/// so nothing about the row said the clause would be refused.
/// </para>
/// </remarks>
public class AliasTypeColumnTests
{
    private const string Collation = "Latin1_General_CI_AS";

    private static Table WithColumn(Column c) =>
        new("app", "Articolo", [new Column("Id", "int", isNullable: false, ordinal: 1), c], [], []);

    private static string Create(Table t) =>
        new TableScriptEmitter().Emit(new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));

    [Fact]
    public void A_column_of_an_alias_type_gets_no_collation()
    {
        Table t = WithColumn(new Column("Codice", "CodiceArticolo", isNullable: false, ordinal: 2,
            collation: Collation)
        { IsUserDefinedType = true });

        Create(t).Should().Contain("[Codice] [CodiceArticolo] NOT NULL").And.NotContain("COLLATE");
    }

    [Fact]
    public void A_string_column_still_gets_its_explicit_collation()
    {
        // The negative control, and it guards a rule the owner restored on
        // purpose: DbDelta always states the collation on a character column.
        Table t = WithColumn(new Column("Nome", "nvarchar(100)", isNullable: false, ordinal: 2,
            collation: Collation));

        Create(t).Should().Contain($"COLLATE {Collation}");
    }

    [Fact]
    public void A_table_type_column_of_an_alias_type_gets_no_collation_either()
    {
        // The same trap, one emitter over: a table type's columns come from the
        // same catalog view and are written by a different class.
        TableTypeUdt tt = new("dbo", "TvpRighe",
        [
            new Column("Codice", "CodiceArticolo", isNullable: false, ordinal: 1, collation: Collation)
            { IsUserDefinedType = true },
        ]);

        new TableTypeUdtScriptEmitter().EmitCreate(tt).Should().NotContain("COLLATE");
    }
}
