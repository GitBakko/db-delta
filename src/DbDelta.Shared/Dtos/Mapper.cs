using DbDelta.Core.Diff;

namespace DbDelta.Shared.Dtos;

/// <summary>
/// Pure projection from <see cref="ComparisonResult"/> to <see cref="ComparisonResultDto"/>.
/// </summary>
public static class Mapper
{
    public static ComparisonResultDto ToDto(ComparisonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
        [
            .. result.Differences.Select(d => new DifferenceDto(
                Kind: d.Identity.Kind,
                SchemaName: d.Identity.SchemaName,
                ObjectName: d.Identity.ObjectName,
                Status: d.Status.ToString()))
        ]);
    }
}
