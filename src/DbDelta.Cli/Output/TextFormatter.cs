using System.Text;
using DbDelta.Core.Diff;

namespace DbDelta.Cli.Output;

/// <summary>
/// Human-readable text output for a <see cref="ComparisonResult"/>.
/// </summary>
internal static class TextFormatter
{
    public static string Format(ComparisonResult result)
    {
        StringBuilder sb = new();
        IEnumerable<IGrouping<DifferenceStatus, DifferencePair>> grouped = result.Differences
            .GroupBy(d => d.Status)
            .OrderBy(g => g.Key);

        foreach (IGrouping<DifferenceStatus, DifferencePair> g in grouped)
        {
            sb.Append('=').Append(g.Key).Append(" (").Append(g.Count()).AppendLine(")");
            foreach (DifferencePair pair in g.OrderBy(p => p.Identity.SchemaName).ThenBy(p => p.Identity.ObjectName))
            {
                sb.Append("  [").Append(pair.Identity.Kind).Append("] ")
                  .Append(pair.Identity.SchemaName).Append('.').AppendLine(pair.Identity.ObjectName);
            }
        }
        return sb.ToString();
    }
}
