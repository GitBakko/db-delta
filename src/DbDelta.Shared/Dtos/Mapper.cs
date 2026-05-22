using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;

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
                Status: d.Status.ToString(),
                LastModifiedSourceUtc: ExtractModifyDate(d.SideA),
                LastModifiedTargetUtc: ExtractModifyDate(d.SideB)))
        ]);
    }

    private static DateTime? ExtractModifyDate(object? side) => side switch
    {
        Module m => m.ModifyDate,
        Table t => t.ModifyDate,
        _ => null,
    };
}
