#pragma warning disable IDE0005
using System.IO;
#pragma warning restore IDE0005
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDelta.Persistence.Json;

/// <summary>
/// Which theme the user picked for the app shell.
/// </summary>
public enum AppTheme
{
    /// <summary>Follow the operating system's light/dark setting.</summary>
    System = 0,

    /// <summary>Always light, whatever the OS is set to.</summary>
    Light = 1,

    /// <summary>Always dark, whatever the OS is set to.</summary>
    Dark = 2,
}

/// <summary>
/// Persists per-user shell preferences as JSON in
/// <c>%LOCALAPPDATA%\DbDelta\ui-settings.json</c>. Only the theme lives here
/// today; the file carries a schema version so later preferences can join it.
/// </summary>
/// <remarks>
/// Every read path degrades to <see cref="AppTheme.System"/> instead of
/// throwing. This file is written on a UI click and read during startup, so a
/// truncated or hand-edited file must cost the user their theme, never their
/// ability to launch the app.
/// </remarks>
public sealed class JsonUiSettingsStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public JsonUiSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public static JsonUiSettingsStore CreateDefault()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbDelta");
        Directory.CreateDirectory(dir);
        return new JsonUiSettingsStore(Path.Combine(dir, "ui-settings.json"));
    }

    /// <summary>
    /// Reads the stored theme, or <see cref="AppTheme.System"/> when the file
    /// is absent, unreadable, unparseable, or written by a newer schema.
    /// </summary>
    public async Task<AppTheme> LoadThemeAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return AppTheme.System;
        }
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
            Document? doc = JsonSerializer.Deserialize<Document>(bytes, s_json);
            return doc is null || doc.SchemaVersion > CurrentSchemaVersion
                ? AppTheme.System
                : doc.Theme;
        }
        catch (JsonException)
        {
            return AppTheme.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AppTheme.System;
        }
    }

    /// <summary>Writes <paramref name="theme"/>, replacing any stored value.</summary>
    public async Task SaveThemeAsync(AppTheme theme, CancellationToken ct)
    {
        string tmp = _filePath + ".tmp";
        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            // 0600 — owner read/write only, same as the sibling stores.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        await using (FileStream fs = new(tmp, options))
        {
            await JsonSerializer.SerializeAsync(fs, new Document(CurrentSchemaVersion, theme), s_json, ct)
                                .ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    private sealed record Document(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("theme")] AppTheme Theme);
}
