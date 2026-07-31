namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Formats a column data-type token to match Redgate SQL Compare's style: the
/// type name is bracket-quoted and a single space precedes the size/precision
/// arguments, whose internal commas are spaced. For example
/// <c>nvarchar(200)</c> becomes <c>[nvarchar] (200)</c> and
/// <c>decimal(18,2)</c> becomes <c>[decimal] (18, 2)</c>.
/// </summary>
/// <remarks>
/// The name used to be passed through unquoted when it already started with
/// <c>[</c> or held a <c>.</c>, on the theory that it was an alias or a
/// schema-qualified user-defined type someone had already quoted. No producer
/// in this repo ever emits either shape: every <c>DataType</c> reaching here is
/// a bare <c>sys.types.name</c> with an optional length, from
/// <c>TableReader</c>, <c>TableTypeUdtReader</c>, or the body resolver. So the
/// branch fired for exactly one input — a catalog type name holding a bracket
/// or a dot — and handed it to the script raw, which is the one sink S11 set
/// out to close.
/// </remarks>
internal static class SqlTypeFormatter
{
    public static string FormatColumnType(string dataType)
    {
        string t = dataType.Trim();
        int paren = t.IndexOf('(');
        string name = (paren < 0 ? t : t[..paren]).TrimEnd();
        string bracketedName = Sql.Q(name);
        if (paren < 0)
        {
            return bracketedName;
        }

        string inner = t[(paren + 1)..].Trim().TrimEnd(')').Trim();
        string spacedArgs = string.Join(", ", inner.Split(',').Select(a => a.Trim()));
        return $"{bracketedName} ({spacedArgs})";
    }
}
