using System.Text.Json;
using DbDelta.Core.Diff;

namespace DbDelta.Cli.Output;

/// <summary>
/// JSON output for <c>compare --format json</c>.
/// </summary>
/// <remarks>
/// It is stable, and it is NOT the only one: <c>report --json</c> goes through
/// <c>JsonReportGenerator</c>, which names the same two fields
/// <c>schemaName</c> / <c>objectName</c> instead of <c>schema</c> / <c>name</c>
/// and adds the two modify dates. Anyone who reads one shape and assumes the
/// other is wrong in a way nothing here used to admit. Collapsing them onto one
/// generator breaks whatever scripts the released CLI already feeds, so it is
/// the owner's call and lives in the backlog; meanwhile
/// <c>Compare_json_keeps_its_published_field_names</c> pins this shape so it
/// cannot drift by accident.
/// </remarks>
internal static class JsonFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static string Format(ComparisonResult result)
    {
        var dto = new
        {
            differences = result.Differences.Select(d => new
            {
                kind = d.Identity.Kind,
                schema = d.Identity.SchemaName,
                name = d.Identity.ObjectName,
                status = d.Status.ToString(),
            }).ToArray()
        };
        return JsonSerializer.Serialize(dto, Options);
    }
}
