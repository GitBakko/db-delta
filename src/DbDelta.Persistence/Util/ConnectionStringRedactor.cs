using System.Text.RegularExpressions;

namespace DbDelta.Persistence.Util;

/// <summary>
/// Pure helper that masks <c>password=...</c> / <c>pwd=...</c> values
/// inside a connection string so the result can be safely shown to the
/// user or written to a log. Case-insensitive; preserves the original
/// keyword's case for cosmetics.
/// </summary>
/// <remarks>
/// A value is bare, or quoted the way <c>DbConnectionStringBuilder</c> quotes
/// it: in double quotes when it contains <c>';'</c> or padding (an inner
/// <c>'"'</c> doubled), or in single quotes when it contains a <c>'"'</c> and no
/// <c>'\''</c>. Stopping at the first <c>';'</c> — all this did until
/// 2026-09-05 — left <c>Password="a;b=c"</c> on screen as
/// <c>Password=***;b=c"</c>: the tail of the real password, in the header strip
/// and in the confirm dialog, the moment such passwords became connectable.
/// </remarks>
public static partial class ConnectionStringRedactor
{
    [GeneratedRegex(
        @"(?i)(password|pwd)\s*=\s*(?:""(?:[^""]|"""")*""|'(?:[^']|'')*'|[^;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PasswordPattern();

    public static string Redact(string? value) =>
        value is null ? string.Empty : PasswordPattern().Replace(value, "$1=***");
}
