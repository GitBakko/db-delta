using System.Text.RegularExpressions;

namespace DbDelta.Core.Diff;

/// <summary>
/// Normalizes T-SQL module bodies for comparison. v1 strategy:
/// <list type="number">
///   <item>Replace CRLF + CR with LF.</item>
///   <item>Collapse any run of whitespace (spaces, tabs, newlines) into a single space.</item>
///   <item>Trim outer whitespace.</item>
///   <item>Strip trailing <c>;</c> — SQL Server sometimes appends one when storing a
///       module body (e.g. after <c>CREATE OR ALTER FUNCTION ... END</c>), producing a
///       cosmetic divergence on a round-trip that should compare as identical.
///       ALL of them, not one: a body ending <c>";;"</c> says exactly what one
///       ending <c>";"</c> says, and stripping a single semicolon made the two
///       compare Different — a difference no script can remove, since the
///       generator would emit the same text it had already deployed.</item>
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
        // Whitespace is already collapsed to single spaces, so trimming ';' and
        // ' ' together handles "…;", "…;;" and "…; ;" alike.
        return collapsed.Trim().TrimEnd(';', ' ');
    }

    /// <summary>
    /// Whitespace-insensitive equality for definition texts read back from
    /// <c>sys.*</c> catalog views — DEFAULT / CHECK / computed-column /
    /// index-filter expressions. SQL Server re-formats these when storing
    /// them, so the stored whitespace (including line endings) differs across
    /// servers and across re-creates of the very same script; cosmetic drift
    /// must never count as a difference, or the diff can never be flattened
    /// by applying the generated alignment script. <c>null</c> and empty
    /// compare equal. The same predicate is shared by the comparison engine
    /// and the script emitters so they always agree on "changed".
    /// </summary>
    public static bool ExpressionsEqual(string? a, string? b) =>
        string.Equals(
            Normalize(a) ?? string.Empty,
            Normalize(b) ?? string.Empty,
            StringComparison.Ordinal);
}
