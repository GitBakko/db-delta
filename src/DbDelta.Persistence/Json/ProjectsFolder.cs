#pragma warning disable IDE0005
using System.IO;
#pragma warning restore IDE0005

namespace DbDelta.Persistence.Json;

/// <summary>
/// Canonical on-disk location for user-saved DbDelta projects:
/// <c>%LOCALAPPDATA%\DbDelta\Projects\</c>. Centralised here so the
/// save dialog, the MRU store, and any future settings page agree on a
/// single source of truth.
/// </summary>
public static class ProjectsFolder
{
    /// <summary>Absolute path of the per-user projects folder. Created if
    /// missing. Returned with a trailing separator stripped.</summary>
    public static string GetOrCreate()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbDelta",
            "Projects");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// Returns the .dbd path for a given project name (sanitised against
    /// path-separator and reserved characters). Always lands inside
    /// <see cref="GetOrCreate"/>.
    /// </summary>
    public static string ResolvePath(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        string safe = Sanitise(projectName);
        return Path.Combine(GetOrCreate(), safe + ".dbd");
    }

    private static string Sanitise(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        int len = 0;
        foreach (char c in name)
        {
            buffer[len++] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }
        string result = new string(buffer[..len]).Trim();
        return string.IsNullOrEmpty(result) ? "Progetto" : result;
    }
}
