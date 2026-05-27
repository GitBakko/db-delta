using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using FsCheck.Fluent;
using Xunit;

namespace DbDelta.PropertyTests.Properties;

/// <summary>
/// Property tests for <see cref="DependencyResolver.Order"/>.
/// Verifies that for any randomly generated acyclic graph the resolver
/// produces a valid topological linearization: every Referenced node
/// precedes its Dependent, every input node appears exactly once, and
/// the output is deterministic across permutations of the input.
/// Uses the same sampled-arbitrary pattern as
/// <see cref="ComparisonEngineProperties"/> and
/// <see cref="ScriptGeneratorProperties"/>.
/// </summary>
public class DependencyResolverProperties
{
    private const int SampleCount = 40;
    private const int Size = 6;

    private static readonly DependencyResolver _resolver = new();

    private static readonly string[] Kinds =
        ["Table", "View", "Function", "Sequence", "Synonym"];

    private static ObjectIdentity Id(int n, string kind) =>
        new("dbo", $"o{n:D3}", kind);

    [Fact]
    public void Order_is_a_valid_linearization_and_deterministic()
    {
        foreach (int nodeCount in Gen.Sample(Gen.Choose(2, 12), SampleCount, size: Size))
        {
            ObjectIdentity[] nodes = [.. Enumerable.Range(0, nodeCount)
                .Select(i => Id(i, Kinds[i % Kinds.Length]))];

            // Acyclic by construction: edges always point from a higher index
            // (Dependent) to a lower index (Referenced). Node i depends on
            // some j < i, so Referenced always precedes Dependent in index
            // order and no cycle can form.
            List<DependencyEdge> edges = [];
            foreach (int i in Enumerable.Range(1, nodeCount - 1))
            {
                foreach (int j in Gen.Sample(Gen.Choose(0, i - 1), 2, size: Size).Distinct())
                {
                    edges.Add(new DependencyEdge(nodes[i], nodes[j], EdgeKind.ModuleReference));
                }
            }

            List<ObjectIdentity> order = [.. _resolver.Order(nodes, edges)];

            // 1. Every node present exactly once.
            order.Should().BeEquivalentTo(nodes,
                "Order must contain every input node exactly once");

            // 2. Every edge respected: Referenced precedes Dependent.
            foreach (DependencyEdge e in edges)
            {
                int refIdx = order.IndexOf(e.Referenced);
                int depIdx = order.IndexOf(e.Dependent);
                refIdx.Should().BeLessThan(depIdx,
                    $"Referenced {e.Referenced.ObjectName} (pos {refIdx}) must precede " +
                    $"Dependent {e.Dependent.ObjectName} (pos {depIdx})");
            }

            // 3. Deterministic: reshuffled input produces identical output.
            ObjectIdentity[] reshuffled = [.. nodes.Reverse()];
            IReadOnlyList<ObjectIdentity> order2 = _resolver.Order(reshuffled, edges);
            order2.Should().Equal(order,
                "Order must be a pure function of its inputs regardless of input permutation");
        }
    }
}
