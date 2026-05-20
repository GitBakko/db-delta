namespace DbDelta.Core.Diff;

/// <summary>
/// Three-state classification of a single object pairing. See spec §3 and §5.
/// </summary>
public enum DifferenceStatus
{
    Identical,
    Different,
    OnlyInA,
    OnlyInB,
}
