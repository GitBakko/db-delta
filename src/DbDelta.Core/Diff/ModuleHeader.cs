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
/// <para>
/// Real-world definitions frequently open with a comment banner (the classic
/// SSMS "-- ===== Author / Create date =====" template) before the
/// <c>CREATE</c> token, and may carry arbitrary whitespace between header
/// keywords. The header regex therefore skips any mix of whitespace, line
/// comments (<c>--…</c>) and block comments (<c>/* … */</c>) before
/// <c>CREATE</c>, and tolerates whitespace runs between tokens.
/// </para>
/// </summary>
public static partial class ModuleHeader
{
    // Matches the leading  CREATE [OR ALTER] <type> <name>  of a module
    // definition, tolerating any mix of whitespace / -- line comments /
    // /* block */ comments before the CREATE token (atomic group — no
    // backtracking into the trivia). <name> is an optional schema qualifier
    // (id1 + qual/id2) or a bare identifier (id1). Identifiers may be
    // bracket-quoted or bare; a quoted one may contain doubled brackets, and
    // must, or the match stops halfway through a name like [Ev]]il] and the
    // rewrite splices from the wrong offset. Singleline so block comments span
    // lines.
    [GeneratedRegex(
        @"^(?>(?:\s+|--[^\r\n]*|/\*.*?\*/)*)(?<create>CREATE)(?<oralter>\s+OR\s+ALTER)?\s+(?<type>VIEW|PROCEDURE|PROC|FUNCTION|TRIGGER)\s+(?<id1>\[(?:[^\]]|\]\])+\]|[A-Za-z_]\w*)(?<qual>\s*\.\s*(?<id2>\[(?:[^\]]|\]\])+\]|[A-Za-z_]\w*))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex HeaderRegex();

    /// <summary>
    /// Returns a comparison-canonical form of <paramref name="body"/> in which the
    /// leading <c>CREATE … &lt;name&gt;</c> object name is rewritten to the catalog
    /// identity <c>[schema].[name]</c> and the create verb is reduced to a bare
    /// <c>CREATE &lt;TYPE&gt;</c> (the optional <c>OR ALTER</c> is dropped, the type
    /// keyword upper-cased). Because both sides of a pair share the same catalog
    /// identity, a stale embedded name (or a <c>CREATE</c> vs <c>CREATE OR ALTER</c>
    /// shape difference) collapses to the same header and no longer registers as a
    /// difference. Any comment banner before the header is preserved verbatim —
    /// comment-only differences must still surface. The body is returned unchanged
    /// when no recognised module header is found (e.g. body fragments in tests).
    /// </summary>
    public static string? CanonicalizeObjectName(string? body, string schema, string name)
    {
        if (string.IsNullOrEmpty(body)) { return body; }
        Match m = HeaderRegex().Match(body);
        if (!m.Success) { return body; }
        string type = m.Groups["type"].Value.ToUpperInvariant();
        int headerStart = m.Groups["create"].Index;
        return string.Concat(
            body.AsSpan(0, headerStart),
            $"CREATE {type} {ScriptGen.Sql.Q(schema, name)}",
            body.AsSpan(m.Index + m.Length));
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

        Group nameGroup = m.Groups["qual"].Success ? m.Groups["id2"] : m.Groups["id1"];
        string embedded = Unquote(nameGroup.Value);
        if (string.Equals(embedded, name, StringComparison.OrdinalIgnoreCase))
        {
            return body; // not stale — preserve verbatim
        }

        int nameStart = m.Groups["id1"].Index;
        return string.Concat(
            body.AsSpan(0, nameStart),
            ScriptGen.Sql.Q(schema, name),
            body.AsSpan(m.Index + m.Length));
    }

    /// <summary>
    /// Rewrites the module header's <c>CREATE</c> verb to <c>CREATE OR ALTER</c>
    /// while preserving the rest of the definition verbatim — including any
    /// comment banner before the header and the original spacing between
    /// tokens. Returns the body unchanged when it already reads
    /// <c>CREATE OR ALTER</c> or when no recognised module header is found
    /// (in which case the caller will emit the original text and SQL Server
    /// will surface the real parse error).
    /// </summary>
    public static string EnsureCreateOrAlter(string body)
    {
        if (string.IsNullOrEmpty(body)) { return body; }
        Match m = HeaderRegex().Match(body);
        if (!m.Success || m.Groups["oralter"].Success) { return body; }

        Group create = m.Groups["create"];
        int insertAt = create.Index + create.Length;
        return string.Concat(body.AsSpan(0, insertAt), " OR ALTER", body.AsSpan(insertAt));
    }

    /// <summary>
    /// Produces deploy-ready DDL for a module body: trims leading whitespace,
    /// re-targets a stale embedded name to the catalog identity
    /// (<see cref="AlignNameToCatalog"/>), upgrades the create verb to
    /// <c>CREATE OR ALTER</c> (<see cref="EnsureCreateOrAlter"/>) and guarantees a
    /// trailing <c>;</c>. Shared by the view / procedure / function / trigger
    /// script emitters so the header-rewrite rules live in exactly one place.
    /// </summary>
    public static string ToCreateOrAlterScript(string body, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(body);
        string ddl = EnsureCreateOrAlter(AlignNameToCatalog(body.TrimStart(), schema, name));
        return ddl.EndsWith(';') ? ddl : ddl + ";";
    }

    /// <summary>
    /// Same, wrapped in the SET options the module was compiled under when they
    /// are not the script's own.
    /// </summary>
    /// <remarks>
    /// The settings in force at CREATE time are baked into the module and govern
    /// it forever after, so deploying a QUOTED_IDENTIFIER OFF procedure under the
    /// preamble's blanket ON produces an object that means something else — a
    /// "quoted" token becomes an identifier instead of a string. They are
    /// restored straight after: leaving OFF in force would bake it into every
    /// module the rest of the script creates.
    /// <para>
    /// Each part is its own batch because SET QUOTED_IDENTIFIER only takes effect
    /// from the NEXT batch, and because a module definition must be the first
    /// statement of its own batch anyway.
    /// </para>
    /// </remarks>
    public static string ToCreateOrAlterScript(
        string body,
        string schema,
        string name,
        bool usesQuotedIdentifier,
        bool usesAnsiNulls)
    {
        string ddl = ToCreateOrAlterScript(body, schema, name);
        List<string> off = [];
        if (!usesQuotedIdentifier) { off.Add("QUOTED_IDENTIFIER"); }
        if (!usesAnsiNulls) { off.Add("ANSI_NULLS"); }
        if (off.Count == 0) { return ddl; }

        string options = string.Join(", ", off);
        return $"SET {options} OFF;\nGO\n{ddl}\nGO\nSET {options} ON;";
    }

    /// <summary>
    /// Reverses <see cref="ScriptGen.Sql.Q(string)"/>: strips the outer brackets
    /// and collapses the doubled ones inside. Without the collapse a correctly
    /// quoted <c>[Ev]]il]</c> read back as <c>Ev]]il</c>, so the staleness
    /// comparison below mis-fired and the rewrite spliced a name nobody had.
    /// </summary>
    private static string Unquote(string ident) =>
        ident.Length >= 2 && ident[0] == '[' && ident[^1] == ']'
            ? ident[1..^1].Replace("]]", "]", StringComparison.Ordinal)
            : ident;
}
