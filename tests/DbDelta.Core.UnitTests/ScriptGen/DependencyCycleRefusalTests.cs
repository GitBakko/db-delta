using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A CHECK constraint that calls a function reading its own table is a legal,
/// ordinary schema, and it is a dependency cycle for anything that writes the
/// constraint inside <c>CREATE TABLE</c> — which DbDelta does.
/// </summary>
/// <remarks>
/// <para>
/// Measured on <c>mssql/server:2022-latest</c>: the table, the function and
/// <c>CHECK (dbo.fnRowCount() &lt; 100)</c> all create, and the table then
/// accepts rows. DbDelta's own reader query returns both arcs —
/// <c>fnRowCount [FN] → Righe [U]</c>, and <c>Righe [U] → fnRowCount [FN]</c>
/// because a CHECK's references are attributed to its parent table.
/// </para>
/// <para>
/// The exception's own doc-comment used to call such a cycle a reader bug
/// rather than user error, and that sentence is why the CREATE path went
/// unguarded and the CLI answered 99 with "open an issue" for a schema someone
/// is entitled to have.
/// </para>
/// </remarks>
public class DependencyCycleRefusalTests
{
    private static readonly ScriptGenerator Sut = new();

    private static ObjectIdentity TblId => new("dbo", "Righe", "Table");
    private static ObjectIdentity FnId => new("dbo", "fnRowCount", "Function");

    private static Table Tbl => new("dbo", "Righe",
        [new Column("Id", "int", isNullable: false, ordinal: 1)]);

    private static Function Fn => new("dbo", "fnRowCount",
        "CREATE FUNCTION dbo.fnRowCount() RETURNS int AS BEGIN RETURN 1 END",
        IsEncrypted: false, FunctionKind: FunctionKind.Scalar);

    private static DependencyEdge[] Cycle =>
    [
        new(FnId, TblId, EdgeKind.ModuleReference),
        new(TblId, FnId, EdgeKind.CheckConstraint),
    ];

    private static ComparisonResult Creating() => new(
    [
        new DifferencePair(TblId, DifferenceStatus.OnlyInA, Tbl, null),
        new DifferencePair(FnId, DifferenceStatus.OnlyInA, Fn, null),
    ]);

    [Fact]
    public void A_cycle_on_the_CREATE_path_is_refused_and_not_swallowed()
    {
        // It has to escape Generate: the CLI and the app both dispatch on the
        // concrete type, and a cycle quietly absorbed here would emit a script
        // in an order the server refuses.
        Action act = () => Sut.Generate(Creating(), dependencies: Cycle);

        act.Should().Throw<DependencyCycleException>()
           .Which.Cycle.Should().NotBeEmpty("the message has to name the objects that close the loop");
    }

    [Fact]
    public void The_refusal_names_both_objects_in_the_loop()
    {
        Action act = () => Sut.Generate(Creating(), dependencies: Cycle);

        string message = act.Should().Throw<DependencyCycleException>().Which.Message;

        message.Should().Contain("Righe").And.Contain("fnRowCount");
    }

    // ── negative controls ────────────────────────────────────────────────

    [Fact]
    public void The_same_two_objects_without_the_closing_edge_still_generate()
    {
        DependencyEdge[] oneWay = [new(TblId, FnId, EdgeKind.CheckConstraint)];

        Sut.Generate(Creating(), dependencies: oneWay).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_cycle_on_the_DROP_path_is_still_absorbed_and_falls_back()
    {
        // Deliberately NOT symmetric, and it must stay that way: the DROP pass
        // catches the cycle and falls back to the reversed create order, which
        // is a legal order for everything except a schemabound chain. Only the
        // CREATE half has no fallback, because there is no order to fall back
        // to. Widening the DROP catch to cover both would swallow the refusal.
        ComparisonResult dropping = new(
        [
            new DifferencePair(TblId, DifferenceStatus.OnlyInB, null, Tbl),
            new DifferencePair(FnId, DifferenceStatus.OnlyInB, null, Fn),
        ]);

        Action act = () => Sut.Generate(dropping, dependencies: [], dropDependencies: Cycle);

        act.Should().NotThrow();
    }
}
