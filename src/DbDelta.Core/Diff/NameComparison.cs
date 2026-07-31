using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Diff;

/// <summary>
/// Decides how identifier case is treated when pairing two schemas.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server resolves object and column names using the database collation,
/// which for the overwhelming majority of installations is case-insensitive.
/// <see cref="ObjectIdentity"/> is a record struct of strings, so its
/// compiler-generated equality is ordinal: without an explicit comparer the
/// engine sees <c>dbo.CLIENTI</c> and <c>dbo.Clienti</c> as two different
/// objects, reports them as target-only plus source-only, and the generated
/// script drops the production table with its data and re-creates it empty.
/// </para>
/// <para>
/// When the collation is unknown the answer is case-INSENSITIVE, and the
/// asymmetry is deliberate: assuming case-sensitivity on a case-insensitive
/// server produces a DROP of live data, whereas assuming case-insensitivity on
/// a genuinely case-sensitive server at worst pairs two objects that should
/// have stayed apart — an ALTER, never a DROP. When it cannot even do that
/// (both spellings present in one database) the engine refuses outright rather
/// than pick one; see <c>ComparisonEngine.MapByIdentity</c>.
/// </para>
/// </remarks>
public static class NameComparison
{
    /// <summary>
    /// The name comparer implied by a <c>sys.databases.collation_name</c>
    /// value. Null or unrecognised ⇒ case-insensitive.
    /// </summary>
    public static StringComparer ForCollation(string? collation) =>
        IsCaseSensitive(collation) ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Collation names are underscore-separated token lists
    /// (<c>SQL_Latin1_General_CP1_CI_AS</c>, <c>Latin1_General_BIN2</c>).
    /// Only the <c>CS</c> token and the binary orderings are case-sensitive —
    /// matched as whole tokens so that neighbours like <c>SC</c>
    /// (supplementary characters) cannot be mistaken for one.
    /// </summary>
    private static bool IsCaseSensitive(string? collation) =>
        collation is not null
        && collation.Split('_').Any(token =>
            token.Equals("CS", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("BIN", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Pairs <see cref="ObjectIdentity"/> values the way the server that will
/// receive the DDL resolves names. See <see cref="NameComparison"/> for why the
/// struct's own ordinal equality is not good enough.
/// </summary>
public sealed class ObjectIdentityComparer(StringComparer names) : IEqualityComparer<ObjectIdentity>
{
    /// <summary>
    /// The comparer applied to schema and object names. Exposed so callers that
    /// pair columns, constraints and indexes inside a matched object use the
    /// same rule the object itself was matched with.
    /// </summary>
    public StringComparer Names { get; } = names;

    public bool Equals(ObjectIdentity x, ObjectIdentity y) =>
        Names.Equals(x.SchemaName, y.SchemaName)
        && Names.Equals(x.ObjectName, y.ObjectName)
        // Kind is our own discriminator from KindCatalog, never a server
        // identifier, so it is always compared ordinally.
        && string.Equals(x.Kind, y.Kind, StringComparison.Ordinal);

    public int GetHashCode(ObjectIdentity obj) => HashCode.Combine(
        Names.GetHashCode(obj.SchemaName),
        Names.GetHashCode(obj.ObjectName),
        obj.Kind);
}
