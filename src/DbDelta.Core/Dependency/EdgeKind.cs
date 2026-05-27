namespace DbDelta.Core.Dependency;

/// <summary>
/// Classifies why one object depends on another. <see cref="ForeignKey"/>
/// edges are recorded for completeness but excluded from the topological
/// graph: FKs are always emitted in a final phase, which breaks FK cycles.
/// </summary>
public enum EdgeKind
{
    ModuleReference,
    ComputedColumn,
    CheckConstraint,
    DefaultConstraint,
    FunctionOnTable,
    TriggerOnTable,
    ForeignKey,
}
