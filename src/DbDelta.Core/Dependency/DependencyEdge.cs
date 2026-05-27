using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Dependency;

/// <summary>
/// "<paramref name="Dependent"/> depends on <paramref name="Referenced"/>"
/// ⇒ Referenced must be created before Dependent.
/// </summary>
public readonly record struct DependencyEdge(
    ObjectIdentity Dependent,
    ObjectIdentity Referenced,
    EdgeKind Kind);
