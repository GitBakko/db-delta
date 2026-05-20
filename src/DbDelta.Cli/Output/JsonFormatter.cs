using System.Text.Json;
using DbDelta.Core.Diff;

namespace DbDelta.Cli.Output;

/// <summary>
/// JSON output for a <see cref="ComparisonResult"/>. Stable, machine-readable contract.
/// </summary>
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
