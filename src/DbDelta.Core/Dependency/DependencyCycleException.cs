using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// Thrown when a dependency cycle is detected among create-validated objects.
/// Such a cycle is uncreatable in a valid source database (SQL Server validates
/// referenced objects at CREATE of views/functions), so this signals a reader
/// bug rather than user error.
/// </summary>
public sealed class DependencyCycleException(IReadOnlyList<ObjectIdentity> cycle) : Exception("Dependency cycle among create-validated objects: "
               + string.Join(" → ", cycle.Select(o => $"{o.SchemaName}.{o.ObjectName}")))
{
    public IReadOnlyList<ObjectIdentity> Cycle { get; } = cycle;
}
