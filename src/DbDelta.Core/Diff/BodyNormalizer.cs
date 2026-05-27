using System.Text.RegularExpressions;

namespace DbDelta.Core.Diff;

/// <summary>
/// Normalizes T-SQL module bodies for comparison. v1 strategy:
/// <list type="number">
///   <item>Replace CRLF + CR with LF.</item>
///   <item>Collapse any run of whitespace (spaces, tabs, newlines) into a single space.</item>
///   <item>Trim outer whitespace.</item>
///   <item>Strip a trailing <c>;</c> — SQL Server sometimes appends one when storing a
///       module body (e.g. after <c>CREATE OR ALTER FUNCTION ... END</c>), producing a
///       cosmetic divergence on a round-trip that should compare as identical.</item>
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
        string trimmed = collapsed.Trim();
        return trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
    }
}
