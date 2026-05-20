using System.Text.RegularExpressions;

namespace DbDelta.Core.Diff;

/// <summary>
/// Normalizes T-SQL module bodies for comparison. v1 strategy:
/// <list type="number">
///   <item>Replace CRLF + CR with LF.</item>
///   <item>Collapse any run of whitespace (spaces, tabs, newlines) into a single space.</item>
///   <item>Trim outer whitespace.</item>
/// </list>
/// Case is preserved — case-insensitive diffing is a future option.
/// </summary>
public static partial class BodyNormalizer
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Returns a comparison-friendly form of <paramref name="body"/>. Returns <c>null</c>
    /// when the input is <c>null</c> (encrypted modules surface that way and must round-trip
    /// the null through the diff engine).
    /// </summary>
    public static string? Normalize(string? body)
    {
        if (body is null)
        {
            return null;
        }
        string lf = body.Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n');
        string collapsed = WhitespaceRun().Replace(lf, " ");
        return collapsed.Trim();
    }
}
