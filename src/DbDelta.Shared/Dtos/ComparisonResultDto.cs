namespace DbDelta.Shared.Dtos;

/// <summary>
/// Wire form of <see cref="DbDelta.Core.Diff.ComparisonResult"/>.
/// </summary>
public sealed record ComparisonResultDto(IReadOnlyList<DifferenceDto> Differences);
