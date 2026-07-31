using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// S3 — the DROP pass used to reverse the CREATE order, which is resolved from
/// SOURCE-side edges. Every object being dropped was removed from the source, so
/// it appears in none of those edges, gets in-degree zero, and lands wherever
/// the inverted kind rank puts it. The pass ordered nothing.
/// </summary>
public class DropOrderingTests
{
    private static readonly ScriptGenerator Sut = new();

    private static View V(string name, string body) => new("dbo", name, body, IsEncrypted: false);

    private static DifferencePair Removed(View v) =>
        new(v.Identity, DifferenceStatus.OnlyInB, null, v);

    /// <summary>
    /// dbo.vTop selects from dbo.vBase WITH SCHEMABINDING, and both are removed
    /// from the source. Dropping vBase first is Msg 3729. The names are chosen
    /// so the fallback order gets it wrong: reverse-alphabetical drops vTop last.
    /// </summary>
    [Fact]
    public void A_schemabound_view_is_dropped_before_the_view_it_binds_to()
    {
        View vBase = V("vZBase", "CREATE VIEW dbo.vZBase WITH SCHEMABINDING AS SELECT Id FROM dbo.T;");
        View vTop = V("vATop", "CREATE VIEW dbo.vATop WITH SCHEMABINDING AS SELECT Id FROM dbo.vZBase;");

        DependencyEdge[] targetEdges =
        [
            new DependencyEdge(vTop.Identity, vBase.Identity, EdgeKind.ModuleReference),
        ];

        string sql = Sut.Generate(
            new ComparisonResult([Removed(vBase), Removed(vTop)]),
            selection: null,
            dropDependencies: targetEdges);

        int dropTop = sql.IndexOf("DROP VIEW IF EXISTS [dbo].[vATop]", StringComparison.Ordinal);
        int dropBase = sql.IndexOf("DROP VIEW IF EXISTS [dbo].[vZBase]", StringComparison.Ordinal);

        dropTop.Should().BeGreaterThan(0);
        dropBase.Should().BeGreaterThan(dropTop, "vZBase cannot go while vATop is bound to it (Msg 3729)");
    }

    /// <summary>
    /// The same objects with the SOURCE-side edges the generator used to be
    /// handed. A removed object is in none of them, so they cannot order it —
    /// which is why the drop pass needed its own list rather than a reversal of
    /// the create order.
    /// </summary>
    [Fact]
    public void Source_side_edges_cannot_order_the_drop_and_the_fallback_gets_it_wrong()
    {
        View vBase = V("vZBase", "CREATE VIEW dbo.vZBase WITH SCHEMABINDING AS SELECT Id FROM dbo.T;");
        View vTop = V("vATop", "CREATE VIEW dbo.vATop WITH SCHEMABINDING AS SELECT Id FROM dbo.vZBase;");

        string sql = Sut.Generate(new ComparisonResult([Removed(vBase), Removed(vTop)]));

        int dropTop = sql.IndexOf("DROP VIEW IF EXISTS [dbo].[vATop]", StringComparison.Ordinal);
        int dropBase = sql.IndexOf("DROP VIEW IF EXISTS [dbo].[vZBase]", StringComparison.Ordinal);

        dropBase.Should().BeLessThan(
            dropTop,
            "documents the fallback: with no target edges the pass falls through to reverse-alphabetical");
    }

    /// <summary>
    /// The drop edges must not leak into the CREATE order. The edge below would
    /// invert the two views' alphabetical order if it did, so a self-edge — which
    /// the resolver discards anyway — would not have proved anything.
    /// </summary>
    [Fact]
    public void Target_edges_do_not_disturb_the_create_order()
    {
        View vATop = V("vATop", "CREATE VIEW dbo.vATop AS SELECT 1 AS X;");
        View vZBase = V("vZBase", "CREATE VIEW dbo.vZBase AS SELECT 2 AS X;");
        DifferencePair[] added =
        [
            new(vATop.Identity, DifferenceStatus.OnlyInA, vATop, null),
            new(vZBase.Identity, DifferenceStatus.OnlyInA, vZBase, null),
        ];

        string withEdges = Sut.Generate(
            new ComparisonResult(added),
            selection: null,
            // "vZBase depends on vATop" — if this reached the create resolver it
            // would force vATop first... which is also the alphabetical order, so
            // state the edge the other way round to make the difference visible.
            dropDependencies: [new DependencyEdge(vATop.Identity, vZBase.Identity, EdgeKind.ModuleReference)]);
        string withoutEdges = Sut.Generate(new ComparisonResult(added));

        int top = withEdges.IndexOf("dbo.vATop", StringComparison.Ordinal);
        int bas = withEdges.IndexOf("dbo.vZBase", StringComparison.Ordinal);
        top.Should().BeLessThan(bas, "the create order stays kind-then-alphabetical");
        withEdges.Should().Be(withoutEdges);
    }
}
