using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// An alias type whose base type changed is emitted as DROP TYPE + CREATE TYPE
/// in one indivisible body at the type's topological slot. SQL Server refuses
/// the DROP with Msg 3732 while anything still uses the type, and no ordering
/// can save it: UserDefinedType ranks before every kind that can bind one, so
/// even a binder the script does emit is emitted too late — and a binder that
/// compares Identical is filtered out before generation and never emitted at all.
/// </summary>
/// <remarks>
/// Six binder forms block the drop, all measured on mssql/server:2022-latest
/// with one type per form so each DROP was isolated. Three are DECLARATIONS and
/// appear in no dependency view — a table column, a sequence, a table type's
/// column — and are read off the model. Three appear only in
/// sys.sql_expression_dependencies as referenced_class = 6 rows: a procedure
/// parameter, a function parameter and a function's RETURN type. A seventh does
/// not exist: an alias type is illegal in a CAST (Msg 243).
/// </remarks>
public class BoundTypeDropRefusalTests
{
    private static readonly ScriptGenerator Sut = new();

    private static ObjectIdentity TypeId => new("app", "Codice", "UserDefinedType");

    /// <summary>The base type changes, which is what forces the drop-and-recreate.</summary>
    private static UserDefinedType SrcType => new("app", "Codice", "bigint", 8, 19, 0, false);
    private static UserDefinedType TgtType => new("app", "Codice", "int", 4, 10, 0, false);

    private static DifferencePair TypePair =>
        new(TypeId, DifferenceStatus.Different, SrcType, TgtType);

    private static Column TypedColumn => new("C", "Codice", isNullable: false, ordinal: 1)
    {
        IsUserDefinedType = true,
        TypeSchema = "app",
    };

    // ── The three declaration binders: no dependency view records them ──────

    [Fact]
    public void A_table_column_of_the_type_refuses_the_drop()
    {
        Table bound = new("dbo", "Ordini", [TypedColumn]);
        ComparisonResult result = new([
            TypePair,
            // Identical on purpose: this is the shape the entry was opened for.
            new DifferencePair(bound.Identity, DifferenceStatus.Identical, bound, bound),
        ]);

        BoundTypeDropException ex = Assert.Throws<BoundTypeDropException>(() => Sut.Generate(result));

        ex.Type.Should().Be(TypeId);
        ex.Binder.ObjectName.Should().Be("Ordini");
        ex.Message.Should().Contain("3732");
    }

    [Fact]
    public void A_sequence_of_the_type_refuses_the_drop()
    {
        Sequence seq = new("dbo", "SeqOrdini", "Codice", 1, 1, null, null, false, false, null)
        {
            TypeSchema = "app",
        };
        ComparisonResult result = new([
            TypePair,
            new DifferencePair(seq.Identity, DifferenceStatus.Identical, seq, seq),
        ]);

        Assert.Throws<BoundTypeDropException>(() => Sut.Generate(result))
              .Binder.ObjectName.Should().Be("SeqOrdini");
    }

    [Fact]
    public void A_table_type_column_of_the_type_refuses_the_drop()
    {
        TableTypeUdt tt = new("dbo", "TT_Ordini", [TypedColumn]);
        ComparisonResult result = new([
            TypePair,
            new DifferencePair(tt.Identity, DifferenceStatus.Identical, tt, tt),
        ]);

        // The server's own message names the internal type-table here
        // (TT_UsaTT_37A5467C), which is a name nobody wrote. This one names the
        // table type.
        Assert.Throws<BoundTypeDropException>(() => Sut.Generate(result))
              .Binder.ObjectName.Should().Be("TT_Ordini");
    }

    // ── The three parameter binders: ONLY a dependency edge sees them ───────

    [Theory]
    [InlineData("UsaProcP", "Procedure")]
    [InlineData("UsaFuncP", "Function")]
    [InlineData("UsaFuncR", "Function")]
    public void A_parameter_or_return_type_refuses_the_drop(string name, string kind)
    {
        ObjectIdentity binder = new("dbo", name, kind);
        ComparisonResult result = new([TypePair]);

        BoundTypeDropException ex = Assert.Throws<BoundTypeDropException>(() =>
            Sut.Generate(result, dropDependencies: [new DependencyEdge(binder, TypeId, EdgeKind.ModuleReference)]));

        ex.Binder.Should().Be(binder);
    }

    // ── Negative controls ──────────────────────────────────────────────────

    /// <summary>
    /// A type nothing uses is dropped and re-created without complaint.
    /// </summary>
    [Fact]
    public void A_type_nobody_uses_is_not_refused() =>
        Sut.Invoking(g => g.Generate(new ComparisonResult([TypePair]))).Should().NotThrow();

    /// <summary>
    /// A column of a BUILT-IN type named the same as the alias is not a binder —
    /// IsUserDefinedType is what decides, not the type name.
    /// </summary>
    [Fact]
    public void A_column_of_a_builtin_type_is_not_a_binder()
    {
        Table bound = new("dbo", "Ordini", [new Column("C", "Codice", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new([
            TypePair,
            new DifferencePair(bound.Identity, DifferenceStatus.Identical, bound, bound),
        ]);

        Sut.Invoking(g => g.Generate(result)).Should().NotThrow();
    }

    /// <summary>
    /// The type lives where it was created, not where the thing using it lives:
    /// a column of dbo.Codice must not block a drop of app.Codice.
    /// </summary>
    [Fact]
    public void A_column_of_a_same_named_type_in_another_schema_is_not_a_binder()
    {
        Table bound = new("dbo", "Ordini",
            [new Column("C", "Codice", isNullable: false, ordinal: 1) { IsUserDefinedType = true, TypeSchema = "dbo" }]);
        ComparisonResult result = new([
            TypePair,
            new DifferencePair(bound.Identity, DifferenceStatus.Identical, bound, bound),
        ]);

        Sut.Invoking(g => g.Generate(result)).Should().NotThrow();
    }

    /// <summary>
    /// A binder this very script drops first is not a binder: the DROP pass runs
    /// before the CREATE pass that carries the type's drop-and-recreate.
    /// </summary>
    [Fact]
    public void A_binder_this_script_drops_first_is_not_refused()
    {
        Table bound = new("dbo", "Ordini", [TypedColumn]);
        ComparisonResult result = new([
            TypePair,
            new DifferencePair(bound.Identity, DifferenceStatus.OnlyInB, null, bound),
        ]);

        Sut.Invoking(g => g.Generate(result)).Should().NotThrow();
    }

    /// <summary>
    /// A type only the SOURCE has is created, never dropped, so nothing can be
    /// bound to it yet.
    /// </summary>
    [Fact]
    public void A_type_that_is_only_created_is_not_refused()
    {
        Table bound = new("dbo", "Ordini", [TypedColumn]);
        ComparisonResult result = new([
            new DifferencePair(TypeId, DifferenceStatus.OnlyInA, SrcType, null),
            new DifferencePair(bound.Identity, DifferenceStatus.Identical, bound, bound),
        ]);

        Sut.Invoking(g => g.Generate(result)).Should().NotThrow();
    }

    /// <summary>
    /// An edge over a type this run does not touch is nobody's business.
    /// </summary>
    [Fact]
    public void An_edge_over_an_untouched_type_is_not_refused() =>
        Sut.Invoking(g => g.Generate(
                new ComparisonResult([TypePair]),
                dropDependencies:
                [
                    new DependencyEdge(
                        new ObjectIdentity("dbo", "UsaAltro", "Procedure"),
                        new ObjectIdentity("app", "Altro", "UserDefinedType"),
                        EdgeKind.ModuleReference)
                ]))
           .Should().NotThrow();
}
