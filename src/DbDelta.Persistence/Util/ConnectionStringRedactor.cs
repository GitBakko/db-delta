using System.Text.RegularExpressions;

namespace DbDelta.Persistence.Util;

/// <summary>
/// Pure helper that masks <c>password=...</c> / <c>pwd=...</c> values
/// inside a connection string so the result can be safely shown to the
/// user or written to a log. Case-insensitive; preserves the original
/// keyword's case for cosmetics.
/// </summary>
public static partial class ConnectionStringRedactor
{
    [GeneratedRegex(@"(?i)(password|pwd)\s*=\s*[^;]+", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    public static string Redact(string? value) =>
        value is null ? string.Empty : PasswordPattern().Replace(value, "$1=***");
}
