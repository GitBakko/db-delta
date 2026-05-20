namespace DbDelta.Core.Diff;

/// <summary>
/// Outcome of running <see cref="ComparisonEngine.Compare"/>.
/// </summary>
public sealed record ComparisonResult(IReadOnlyList<DifferencePair> Differences);
