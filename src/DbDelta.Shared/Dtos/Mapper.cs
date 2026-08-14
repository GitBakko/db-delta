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
                LastModifiedSource: ExtractModifyDate(d.SideA),
                LastModifiedTarget: ExtractModifyDate(d.SideB)))
        ])
        {
            Unexamined =
            [
                .. result.Unexamined.Groups.Select(g =>
                    new UnexaminedGroupDto(g.Key, UnexaminedCensus.LabelFor(g.Key), g.Count))
            ],
            UnexaminedSummary = result.Unexamined.Summary,
        };
    }

    private static DateTime? ExtractModifyDate(object? side) => side switch
    {
        Module m => m.ModifyDate,
        Table t => t.ModifyDate,
        // M5 kinds (Sequence / Synonym / UserDefinedType) — sys.objects has
        // a modify_date column, but the M5 reader pipeline doesn't surface
        // it yet. Surface null today; revisit when the readers gain a
        // ModifyDate field.
        _ => null,
    };
}
