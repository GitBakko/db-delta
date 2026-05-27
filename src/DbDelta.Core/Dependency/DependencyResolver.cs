using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// Produces a deterministic topological order of object identities from a
/// dependency edge list, for CREATE emission. Foreign-key edges are ignored
/// (FKs are emitted in a final phase). Cycles among deferred-resolution kinds
/// (Procedure, Trigger) are tolerated and ordered alphabetically; cycles that
/// touch any create-validated kind throw <see cref="DependencyCycleException"/>.
/// </summary>
public sealed class DependencyResolver
{
    private static readonly IReadOnlyDictionary<string, int> KindRank =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Sequence"] = 0,
            ["UserDefinedType"] = 1,
            ["TableType"] = 2,
            ["Table"] = 3,
            ["View"] = 4,
            ["Function"] = 5,
            ["Procedure"] = 6,
            ["Trigger"] = 7,
            ["Synonym"] = 8,
        };

    private static readonly HashSet<string> DeferredKinds =
        new(StringComparer.Ordinal) { "Procedure", "Trigger" };

    private static int Rank(ObjectIdentity id) =>
        KindRank.TryGetValue(id.Kind, out int r) ? r : int.MaxValue;

    private static int CompareNodes(ObjectIdentity a, ObjectIdentity b)
    {
        int byKind = Rank(a).CompareTo(Rank(b));
        if (byKind != 0) { return byKind; }
        int bySchema = string.CompareOrdinal(a.SchemaName, b.SchemaName);
        return bySchema != 0 ? bySchema : string.CompareOrdinal(a.ObjectName, b.ObjectName);
    }

    public IReadOnlyList<ObjectIdentity> Order(
        IReadOnlyCollection<ObjectIdentity> nodes,
        IReadOnlyCollection<DependencyEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        HashSet<ObjectIdentity> nodeSet = [.. nodes];

        Dictionary<ObjectIdentity, List<ObjectIdentity>> adj = [];
        Dictionary<ObjectIdentity, int> inDegree = [];
        foreach (ObjectIdentity n in nodeSet)
        {
            adj[n] = [];
            inDegree[n] = 0;
        }

        HashSet<(ObjectIdentity, ObjectIdentity)> seen = [];
        foreach (DependencyEdge e in edges)
        {
            if (e.Kind == EdgeKind.ForeignKey) { continue; }
            if (e.Dependent.Equals(e.Referenced)) { continue; }
            if (!nodeSet.Contains(e.Dependent) || !nodeSet.Contains(e.Referenced)) { continue; }
            if (!seen.Add((e.Referenced, e.Dependent))) { continue; }
            adj[e.Referenced].Add(e.Dependent);
            inDegree[e.Dependent]++;
        }

        SortedSet<ObjectIdentity> ready = new(Comparer<ObjectIdentity>.Create(CompareNodes));
        foreach (ObjectIdentity n in nodeSet)
        {
            if (inDegree[n] == 0) { ready.Add(n); }
        }

        List<ObjectIdentity> order = new(nodeSet.Count);
        while (ready.Count > 0)
        {
            ObjectIdentity n = ready.Min;
            ready.Remove(n);
            order.Add(n);
            foreach (ObjectIdentity dep in adj[n])
            {
                if (--inDegree[dep] == 0) { ready.Add(dep); }
            }
        }

        if (order.Count == nodeSet.Count) { return order; }

        HashSet<ObjectIdentity> emitted = [.. order];
        List<ObjectIdentity> remaining = [.. nodeSet.Where(n => !emitted.Contains(n))];
        if (remaining.Any(n => !DeferredKinds.Contains(n.Kind)))
        {
            throw new DependencyCycleException(FindCycle(remaining, adj));
        }
        remaining.Sort(CompareNodes);
        order.AddRange(remaining);
        return order;
    }

    private static IReadOnlyList<ObjectIdentity> FindCycle(
        List<ObjectIdentity> remaining,
        Dictionary<ObjectIdentity, List<ObjectIdentity>> adj)
    {
        HashSet<ObjectIdentity> inScope = [.. remaining];
        HashSet<ObjectIdentity> onStack = [];
        List<ObjectIdentity> stack = [];

        ObjectIdentity start = remaining.Min(Comparer<ObjectIdentity>.Create(CompareNodes));

        HashSet<ObjectIdentity> visited = [];
        IReadOnlyList<ObjectIdentity>? found = null;
        void Dfs(ObjectIdentity node)
        {
            if (found is not null || visited.Contains(node)) { return; }
            stack.Add(node);
            onStack.Add(node);
            foreach (ObjectIdentity next in adj[node].Where(inScope.Contains))
            {
                if (onStack.Contains(next))
                {
                    int from = stack.IndexOf(next);
                    found = [.. stack.Skip(from), next];
                    return;
                }
                if (found is null) { Dfs(next); }
            }
            stack.RemoveAt(stack.Count - 1);
            onStack.Remove(node);
            visited.Add(node);
        }

        Dfs(start);
        // Every residual node has in-degree >= 1 within the residual, so a cycle is always reachable; the fallback is defensive only.
        return found ?? remaining;
    }
}
