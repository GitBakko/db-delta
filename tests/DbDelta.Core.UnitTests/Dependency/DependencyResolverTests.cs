using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Dependency;

public class DependencyResolverTests
{
    private static ObjectIdentity Id(string name, string kind) => new("dbo", name, kind);

    [Fact]
    public void Empty_edges_orders_by_kindrank_then_schema_then_name()
    {
        ObjectIdentity[] nodes =
        [
            Id("vB", "View"), Id("fnA", "Function"), Id("tB", "Table"),
            Id("tA", "Table"), Id("vA", "View"),
        ];

        IReadOnlyList<ObjectIdentity> order = new DependencyResolver().Order(nodes, edges: []);

        order.Should().Equal(
            Id("tA", "Table"), Id("tB", "Table"),
            Id("vA", "View"), Id("vB", "View"),
            Id("fnA", "Function"));
    }

    [Fact]
    public void Referenced_object_is_ordered_before_dependent_across_kinds()
    {
        ObjectIdentity table = Id("Customer", "Table");
        ObjectIdentity fn = Id("fnFullName", "Function");
        DependencyEdge[] edges = [new DependencyEdge(table, fn, EdgeKind.ComputedColumn)];

        IReadOnlyList<ObjectIdentity> order = new DependencyResolver().Order([table, fn], edges);

        order.Should().Equal(fn, table);
    }

    [Fact]
    public void View_on_view_is_ordered_base_first()
    {
        ObjectIdentity baseV = Id("vBase", "View");
        ObjectIdentity topV = Id("vTop", "View");
        ObjectIdentity aOnZ = Id("vAlpha", "View");
        ObjectIdentity zBase = Id("vZeta", "View");
        DependencyEdge[] edges =
        [
            new DependencyEdge(topV, baseV, EdgeKind.ModuleReference),
            new DependencyEdge(aOnZ, zBase, EdgeKind.ModuleReference),
        ];

        List<ObjectIdentity> order = [.. new DependencyResolver().Order([topV, baseV, aOnZ, zBase], edges)];

        order.IndexOf(baseV).Should().BeLessThan(order.IndexOf(topV));
        order.IndexOf(zBase).Should().BeLessThan(order.IndexOf(aOnZ));
    }

    [Fact]
    public void Foreign_key_edges_are_ignored()
    {
        ObjectIdentity a = Id("A", "Table");
        ObjectIdentity b = Id("B", "Table");
        DependencyEdge[] edges = [new DependencyEdge(a, b, EdgeKind.ForeignKey)];

        IReadOnlyList<ObjectIdentity> order = new DependencyResolver().Order([a, b], edges);

        order.Should().Equal(a, b);
    }

    [Fact]
    public void Deterministic_across_input_permutations()
    {
        ObjectIdentity t = Id("T", "Table");
        ObjectIdentity v = Id("V", "View");
        ObjectIdentity f = Id("F", "Function");
        var resolver = new DependencyResolver();

        IReadOnlyList<ObjectIdentity> o1 = resolver.Order([t, v, f], []);
        IReadOnlyList<ObjectIdentity> o2 = resolver.Order([f, t, v], []);
        IReadOnlyList<ObjectIdentity> o3 = resolver.Order([v, f, t], []);

        o1.Should().Equal(o2);
        o2.Should().Equal(o3);
    }

    [Fact]
    public void Cycle_among_procedures_is_tolerated_alphabetically()
    {
        ObjectIdentity pA = Id("uspA", "Procedure");
        ObjectIdentity pB = Id("uspB", "Procedure");
        DependencyEdge[] edges =
        [
            new DependencyEdge(pA, pB, EdgeKind.ModuleReference),
            new DependencyEdge(pB, pA, EdgeKind.ModuleReference),
        ];

        IReadOnlyList<ObjectIdentity> order = new DependencyResolver().Order([pA, pB], edges);

        order.Should().Equal(pA, pB);
    }

    [Fact]
    public void Cycle_touching_a_view_throws()
    {
        ObjectIdentity vA = Id("vA", "View");
        ObjectIdentity vB = Id("vB", "View");
        DependencyEdge[] edges =
        [
            new DependencyEdge(vA, vB, EdgeKind.ModuleReference),
            new DependencyEdge(vB, vA, EdgeKind.ModuleReference),
        ];

        Action act = () => new DependencyResolver().Order([vA, vB], edges);

        act.Should().Throw<DependencyCycleException>();
    }
}
