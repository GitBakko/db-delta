namespace DbDelta.Shared.Dtos;

/// <summary>
/// Wire form of <see cref="Core.Diff.ComparisonResult"/>.
/// </summary>
public sealed record ComparisonResultDto(IReadOnlyList<DifferenceDto> Differences);
