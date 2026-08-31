using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A sequence declared over an alias type has to be ordered against that type,
/// in BOTH directions, and today it is ordered against it in neither.
/// </summary>
/// <remarks>
/// <para>
/// Measured on <c>mssql/server:2022-latest</c>. CREATE: a
/// <c>CREATE SEQUENCE … AS [app].[AliasInt]</c> emitted before the type exists
/// dies with Msg 243, "Type app.AliasInt is not a defined system type" — a
/// different message from the Msg 2715 every other binding gives, which is why
/// it was not recognised as the same defect. DROP: <c>DROP TYPE</c> while a
/// sequence still binds it dies with Msg 3732, "Cannot drop type … because it
/// is being referenced by object 'SeqC'".
/// </para>
/// <para>
/// No dependency EDGE is involved and none can be: the binding of a sequence to
/// its base type is not an expression, so
/// <c>sys.sql_expression_dependencies</c> records no row for it — measured,
/// unlike a parameter binding, which it does record. The ordering comes from
/// <c>DependencyResolver.KindRank</c> alone, and the rank had Sequence ahead of
/// UserDefinedType, which is backwards: a sequence can be declared over an
/// alias type, and an alias type can be based on nothing at all
/// (<c>CREATE TYPE b FROM a</c> where a is an alias is Msg 222, measured, and
/// <c>NEXT VALUE FOR</c> is illegal in every expression a type could carry,
/// Msg 11719). The dependency can therefore only ever point one way, which is
/// what makes the rank safe rather than merely convenient.
/// </para>
/// </remarks>
public class SequenceOverAliasTypeOrderTests
{
    private static readonly ScriptGenerator Sut = new();

    private static UserDefinedType AliasInt =>
        new("app", "AliasInt", "bigint", MaxLength: 8, Precision: 19, Scale: 0, IsNullable: false);

    private static Sequence SeqOverAlias =>
        new("app", "SeqC", "AliasInt", 1, 1, null, null, false, true, null) { TypeSchema = "app" };

    [Fact]
    public void The_alias_type_is_created_before_the_sequence_declared_over_it()
    {
        UserDefinedType t = AliasInt;
        Sequence s = SeqOverAlias;
        ComparisonResult result = new(
        [
            new DifferencePair(s.Identity, DifferenceStatus.OnlyInA, s, null),
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null),
        ]);

        string sql = Sut.Generate(result);

        int typeIdx = sql.IndexOf("CREATE TYPE [app].[AliasInt]", StringComparison.Ordinal);
        int seqIdx = sql.IndexOf("CREATE SEQUENCE [app].[SeqC]", StringComparison.Ordinal);

        typeIdx.Should().BeGreaterThanOrEqualTo(0);
        seqIdx.Should().BeGreaterThan(typeIdx, "CREATE SEQUENCE … AS [app].[AliasInt] before the type is Msg 243");
    }

    [Fact]
    public void The_sequence_is_dropped_before_the_alias_type_it_is_declared_over()
    {
        // The half that was asserted to be correct already, on reasoning rather
        // than measurement, and is not: the drop order is the create rank
        // reversed, so getting the create half wrong got this half wrong too.
        UserDefinedType t = AliasInt;
        Sequence s = SeqOverAlias;
        ComparisonResult result = new(
        [
            new DifferencePair(s.Identity, DifferenceStatus.OnlyInB, null, s),
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInB, null, t),
        ]);

        string sql = Sut.Generate(result);

        int seqIdx = sql.IndexOf("DROP SEQUENCE [app].[SeqC]", StringComparison.Ordinal);
        int typeIdx = sql.IndexOf("DROP TYPE [app].[AliasInt]", StringComparison.Ordinal);

        seqIdx.Should().BeGreaterThanOrEqualTo(0);
        typeIdx.Should().BeGreaterThan(seqIdx, "DROP TYPE while a sequence still binds it is Msg 3732");
    }

    [Fact]
    public void A_sequence_over_a_built_in_type_keeps_its_place_ahead_of_everything()
    {
        // The negative control for the rank move: nothing about a sequence over
        // a BUILT-IN type changes, and a sequence still precedes the table whose
        // DEFAULT may say NEXT VALUE FOR it.
        Sequence s = new("dbo", "SeqPlain", "bigint", 1, 1, null, null, false, true, null);
        Table tbl = new("dbo", "Ordine", [new Column("Id", "bigint", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            new DifferencePair(tbl.Identity, DifferenceStatus.OnlyInA, tbl, null),
            new DifferencePair(s.Identity, DifferenceStatus.OnlyInA, s, null),
        ]);

        string sql = Sut.Generate(result);

        sql.IndexOf("CREATE SEQUENCE [dbo].[SeqPlain]", StringComparison.Ordinal)
           .Should().BeLessThan(sql.IndexOf("CREATE TABLE", StringComparison.Ordinal));
    }
}
