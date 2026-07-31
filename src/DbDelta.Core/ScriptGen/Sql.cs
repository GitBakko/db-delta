namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Identifier quoting for emitted T-SQL. Every bracketed name in a generated
/// script goes through here.
/// </summary>
/// <remarks>
/// <para>
/// Every emitter used to write <c>[{schema}].[{name}]</c> straight into the
/// script. A catalog name containing a closing bracket therefore terminated its
/// own identifier and the rest of the name became script text: at best a syntax
/// error, at worst a statement of the name-holder's choosing running with the
/// deploy's privileges. The project already treats catalog values as untrusted
/// when rendering the HTML report; the SQL sink was the one left raw.
/// </para>
/// <para>
/// The rule SQL Server itself uses (QUOTENAME): double any <c>]</c>, then wrap
/// the result in brackets.
/// </para>
/// </remarks>
public static class Sql
{
    /// <summary>Quotes one identifier — <c>Ev]il</c> becomes <c>[Ev]]il]</c>.</summary>
    public static string Q(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    /// <summary>Quotes a schema-qualified name — the shape almost every call site wants.</summary>
    public static string Q(string schema, string name) => $"{Q(schema)}.{Q(name)}";
}
