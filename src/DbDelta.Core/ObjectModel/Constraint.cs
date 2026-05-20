namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Common shape for every table-level constraint. Concrete records carry the
/// shape-specific data; <see cref="Kind"/> is the discriminator used by
/// emitters and the diff engine.
/// </summary>
public abstract record Constraint(string Name)
{
    public abstract string Kind { get; }
}
