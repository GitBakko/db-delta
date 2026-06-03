using System.Text.RegularExpressions;

namespace DbDelta.Core.Diff;

/// <summary>
/// Reconciles the object name embedded in a T-SQL module's stored definition
/// (<c>sys.sql_modules.definition</c>) with the catalog identity.
/// <para>
/// SQL Server's <c>sp_rename</c> updates the catalog name (<c>sys.objects.name</c>)
/// but leaves the original <c>CREATE … &lt;name&gt;</c> token frozen in the stored
/// definition text. Two databases whose only divergence is such a stale embedded
/// name would otherwise diff as <c>Different</c> even though the modules are
/// semantically identical — SQL Server resolves a module by its catalog identity,
/// never by the name baked into the definition. This helper lets the comparison
/// engine treat them as equal, and lets the script generator emit DDL that targets
/// the real (catalog) object rather than the stale name.
/// </para>
/// </summary>
public static partial class ModuleHeader
{
    // Matches the leading  CREATE [OR ALTER] <type> <name>  of a module definition,
    // tolerating leading whitespace. <name> is captured as an optional
    // schema-qualifier (group 2) plus the object name (group 4 when qualified,
    // else group 2). Identifiers may be bracket-quoted or bare.
    [GeneratedRegex(
        @"^\s*CREATE\s+(?:OR\s+ALTER\s+)?(VIEW|PROCEDURE|PROC|FUNCTION|TRIGGER)\s+(\[[^\]]+\]|[A-Za-z_]\w*)(\s*\.\s*(\[[^\]]+\]|[A-Za-z_]\w*))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();

    /// <summary>
    /// Returns a comparison-canonical form of <paramref name="body"/> in which the
    /// leading <c>CREATE … &lt;name&gt;</c> object name is rewritten to the catalog
    /// identity <c>[schema].[name]</c> and the create verb is reduced to a bare
    /// <c>CREATE &lt;TYPE&gt;</c> (the optional <c>OR ALTER</c> is dropped, the type
    /// keyword upper-cased). Because both sides of a pair share the same catalog
    /// identity, a stale embedded name (or a <c>CREATE</c> vs <c>CREATE OR ALTER</c>
    /// shape difference) collapses to the same header and no longer registers as a
    /// difference. The body is returned unchanged when it does not open with a
    /// recognised module header (e.g. body fragments in tests, or definitions that
    /// open with a comment).
    /// </summary>
    public static string? CanonicalizeObjectName(string? body, string schema, string name)
    {
        if (string.IsNullOrEmpty(body)) { return body; }
        Match m = HeaderRegex().Match(body);
        if (!m.Success) { return body; }
        string type = m.Groups[1].Value.ToUpperInvariant();
        return string.Concat($"CREATE {type} [{schema}].[{name}]", body.AsSpan(m.Index + m.Length));
    }

    /// <summary>
    /// When the name embedded in <paramref name="body"/> differs from the catalog
    /// <paramref name="name"/> (the <c>sp_rename</c> signature), rewrites just the
    /// name token to <c>[schema].[name]</c> while preserving the rest of the
    /// definition verbatim — including the original create verb and formatting — so
    /// generated DDL targets the real object. When the embedded name already matches
    /// the catalog, the body is returned byte-for-byte unchanged so existing
    /// (non-renamed) output is preserved exactly.
    /// </summary>
    public static string AlignNameToCatalog(string body, string schema, string name)
    {
        if (string.IsNullOrEmpty(body)) { return body; }
        Match m = HeaderRegex().Match(body);
        if (!m.Success) { return body; }

        Group nameGroup = m.Groups[3].Success ? m.Groups[4] : m.Groups[2];
        string embedded = Unquote(nameGroup.Value);
        if (string.Equals(embedded, name, StringComparison.OrdinalIgnoreCase))
        {
            return body; // not stale — preserve verbatim
        }

        int nameStart = m.Groups[2].Index;
        return string.Concat(
            body.AsSpan(0, nameStart),
            $"[{schema}].[{name}]",
            body.AsSpan(m.Index + m.Length));
    }

    private static string Unquote(string ident) =>
        ident.Length >= 2 && ident[0] == '[' && ident[^1] == ']' ? ident[1..^1] : ident;
}
