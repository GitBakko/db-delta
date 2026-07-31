namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Formats a column data-type token to match Redgate SQL Compare's style: the
/// type name is bracket-quoted and a single space precedes the size/precision
/// arguments, whose internal commas are spaced. For example
/// <c>nvarchar(200)</c> becomes <c>[nvarchar] (200)</c> and
/// <c>decimal(18,2)</c> becomes <c>[decimal] (18, 2)</c>. Already-bracketed or
/// schema-qualified type names (e.g. a user-defined type) are left untouched.
/// </summary>
internal static class SqlTypeFormatter
{
    public static string FormatColumnType(string dataType)
    {
        string t = dataType.Trim();
        int paren = t.IndexOf('(');
        string name = (paren < 0 ? t : t[..paren]).TrimEnd();
        string bracketedName = name.StartsWith('[') || name.Contains('.') ? name : $"{Sql.Q(name)}";
        if (paren < 0)
        {
            return bracketedName;
        }

        string inner = t[(paren + 1)..].Trim().TrimEnd(')').Trim();
        string spacedArgs = string.Join(", ", inner.Split(',').Select(a => a.Trim()));
        return $"{bracketedName} ({spacedArgs})";
    }
}
