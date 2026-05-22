#pragma warning disable IDE0005
using System.IO;
using System.Linq;
#pragma warning restore IDE0005
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDelta.Persistence.Json;

/// <summary>
/// Persists the recent-projects MRU list as JSON in
/// <c>%LOCALAPPDATA%\DbDelta\recent-projects.json</c>. Each entry pairs the
/// project file path with the last time the user opened or saved it; the
/// list is capped and ordered LastUsedUtc-desc on read.
/// </summary>
public sealed class JsonRecentProjectsStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxEntries = 10;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public JsonRecentProjectsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public static JsonRecentProjectsStore CreateDefault()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbDelta");
        Directory.CreateDirectory(dir);
        return new JsonRecentProjectsStore(Path.Combine(dir, "recent-projects.json"));
    }

    /// <summary>Loads the MRU list, ordered most-recent-first. Filters out
    /// entries whose backing files no longer exist on disk.</summary>
    public async Task<IReadOnlyList<RecentProject>> LoadAsync(CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        return [.. doc.Entries
            .Where(e => File.Exists(e.Path))
            .OrderByDescending(e => e.LastUsedUtc)
            .Take(MaxEntries)];
    }

    /// <summary>
    /// Records a project open or save event for <paramref name="path"/>. Any
    /// existing entry with the same path is replaced; the list is capped at
    /// 10 most-recent entries.
    /// </summary>
    public async Task<IReadOnlyList<RecentProject>> AddOrTouchAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        List<RecentProject> next =
        [
            new RecentProject(path, DateTime.UtcNow),
            .. doc.Entries.Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))
        ];

        if (next.Count > MaxEntries)
        {
            next = [.. next.Take(MaxEntries)];
        }

        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
        return next;
    }

    private async Task<Document> ReadDocumentAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Document(CurrentSchemaVersion, []);
        }
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
            Document? doc = JsonSerializer.Deserialize<Document>(bytes, s_json);
            return doc ?? new Document(CurrentSchemaVersion, []);
        }
        catch (JsonException)
        {
            return new Document(CurrentSchemaVersion, []);
        }
    }

    private async Task WriteAtomicAsync(Document doc, CancellationToken ct)
    {
        string tmp = _filePath + ".tmp";
        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        await using (FileStream fs = new(tmp, options))
        {
            await JsonSerializer.SerializeAsync(fs, doc, s_json, ct).ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    private sealed record Document(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("entries")] IReadOnlyList<RecentProject> Entries);
}

/// <summary>One MRU entry — file path + last-used timestamp.</summary>
public sealed record RecentProject(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("lastUsedUtc")] DateTime LastUsedUtc);
