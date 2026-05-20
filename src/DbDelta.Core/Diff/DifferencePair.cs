using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Diff;

/// <summary>
/// One paired (or unpaired) object between sides A and B of a comparison.
/// </summary>
public sealed record DifferencePair(
    ObjectIdentity Identity,
    DifferenceStatus Status,
    object? SideA,
    object? SideB);
