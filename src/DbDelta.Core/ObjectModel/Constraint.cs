namespace DbDelta.Core.ObjectModel;

/// <summary>
/// Common shape for every table-level constraint. Concrete records carry the
/// shape-specific data; <see cref="Kind"/> is the discriminator used by
/// emitters and the diff engine.
/// </summary>
public abstract record Constraint(string Name)
{
    public abstract string Kind { get; }

    /// <summary>
    /// True when SQL Server minted <see cref="Name"/> itself, because the DDL
    /// did not supply one (an inline <c>DEFAULT</c>, a bare <c>CHECK</c>, a
    /// <c>PRIMARY KEY</c> with no <c>CONSTRAINT</c> clause).
    /// </summary>
    /// <remarks>
    /// The suffix of <c>DF__Ordini__Stato__3B75D760</c> is derived from the
    /// constraint's own <c>object_id</c>, so two servers carrying the very same
    /// schema disagree on it by construction. Such a name is therefore never a
    /// pairing key and never worth copying onto the other side — see
    /// <c>ConstraintPairing</c>. An init property rather than a positional
    /// member so every existing positional construction still compiles;
    /// anything that does not read the catalog leaves it false, which is the
    /// pre-existing "pair by name" behaviour.
    /// </remarks>
    public bool IsSystemNamed { get; init; }
}
