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

    /// <summary>
    /// Cap on the sanitised stem. NTFS allows 255 characters in a file name;
    /// this stays far below it so the folder path plus <c>.dbd</c> cannot push
    /// the whole path over the limit either.
    /// </summary>
    private const int MaxStemLength = 100;

    /// <summary>
    /// Names Windows resolves to devices rather than to files, whatever
    /// extension follows them: <c>NUL.dbd</c> is the null device, not a file
    /// called NUL. A project saved under one of these is written nowhere and
    /// reads back empty, with no error at any layer.
    /// </summary>
    private static readonly HashSet<string> ReservedDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    private static string Sanitise(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();

        // Deliberately NOT a stackalloc sized from the input. The name comes
        // straight from a text box, so a long paste sized the stack buffer and
        // raised StackOverflowException — which no catch, anywhere, can handle.
        string result = new string([.. name.Take(MaxStemLength)
            .Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)])
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrEmpty(result)) { return "Progetto"; }

        // Matched on the part before the first dot, because that is what Windows
        // matches: "NUL.dbd" and "NUL.anything" are both the device.
        string stem = result.Split('.', 2)[0];
        return ReservedDeviceNames.Contains(stem) ? '_' + result : result;
    }
}
