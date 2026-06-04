using System.Reflection;

namespace DbDelta.App;

/// <summary>
/// Resolves the running app version (stamped at publish time via
/// <c>-p:Version</c> in <c>.github/workflows/release.yml</c>; local builds fall
/// back to <c>0.0.0-dev</c> from the csproj) and the deep-link to that
/// version's anchor on the online version-history page.
/// </summary>
public static class AppVersionInfo
{
    private const string HistoryPageUrl =
        "https://gitbakko.github.io/db-delta/articles/version-history.html";

    /// <summary><c>v1.0.0-rc1</c> — or plain <c>dev</c> when no version attribute is present.</summary>
    public static string Display { get; }

    /// <summary>Version-history page URL, anchored at the running version.</summary>
    public static string HistoryUrl { get; }

    static AppVersionInfo()
    {
        string? raw = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        (Display, HistoryUrl) = FromRaw(raw);
    }

    /// <summary>
    /// Pure mapping from a raw <c>InformationalVersion</c> to (display, url).
    /// The SDK appends <c>+&lt;commit-sha&gt;</c> build metadata when building
    /// inside a git repo — everything from the first <c>+</c> is stripped. The
    /// anchor id derives only from the version token and must stay in sync with
    /// <c>scripts/docs/build-version-history.ps1</c> (anchor = <c>v&lt;version&gt;</c>).
    /// </summary>
    public static (string Display, string HistoryUrl) FromRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("dev", HistoryPageUrl);
        }
        string version = raw.Split('+')[0];
        return ($"v{version}", $"{HistoryPageUrl}#v{version}");
    }
}
