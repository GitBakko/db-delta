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
    /// <summary>
    /// Static "changed" notification — any caller that writes to the recent
    /// projects file fires this so live subscribers (e.g. the topbar combo
    /// in MainWindowViewModel) can refresh themselves. Async by default
    /// because handlers typically reload the JSON from disk.
    /// </summary>
    public static event EventHandler? RecentProjectsChanged;

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
        Document doc = await ReadDocumentAsync(ct, forWrite: false).ConfigureAwait(false);
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

        Document doc = await ReadDocumentAsync(ct, forWrite: true).ConfigureAwait(false);
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
        RecentProjectsChanged?.Invoke(this, EventArgs.Empty);
        return next;
    }

    /// <summary>
    /// Reads the backing document. See
    /// <see cref="JsonConnectionStore"/> for why an unreadable file degrades on
    /// the load path but rethrows on the read-modify-write path.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="forWrite">
    /// <see langword="false"/> degrades an unreadable file to an empty list;
    /// <see langword="true"/> rethrows so a write never clobbers entries it
    /// merely failed to read.
    /// </param>
    private async Task<Document> ReadDocumentAsync(CancellationToken ct, bool forWrite)
    {
        if (!File.Exists(_filePath))
        {
            return new Document(CurrentSchemaVersion, []);
        }
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
            Document? doc = JsonSerializer.Deserialize<Document>(bytes, s_json);
            if (doc is null)
            {
                return new Document(CurrentSchemaVersion, []);
            }
            if (doc.SchemaVersion > CurrentSchemaVersion)
            {
                // A downgrade — v2 wrote this, v1 is reading it. Keep the file
                // instead of letting the next write flatten it, exactly as
                // JsonConnectionStore does.
                MoveAside("future-schema");
                return new Document(CurrentSchemaVersion, []);
            }
            return doc;
        }
        catch (JsonException)
        {
            // This branch ignored forWrite, so it undercut the guard the branch
            // below applies: a truncated file was read as "no entries" even on
            // the write path, and the next save replaced ten remembered project
            // paths with one. Copy first, then degrade.
            MoveAside("invalid-json");
            return new Document(CurrentSchemaVersion, []);
        }
        catch (Exception ex) when (!forWrite && ex is IOException or UnauthorizedAccessException)
        {
            return new Document(CurrentSchemaVersion, []);
        }
    }

    /// <summary>
    /// Renames the unreadable file out of the way instead of losing it. Same
    /// shape and same suffix as <c>JsonConnectionStore</c>: a user who finds one
    /// <c>.broken-*</c> file should recognise the other.
    /// </summary>
    private void MoveAside(string reason)
    {
        string aside = _filePath + ".broken-"
            + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
            + "-" + reason;
        try
        {
            File.Move(_filePath, aside, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another instance may have moved it already, or the profile is
            // read-only. Best-effort: never let this sink the caller.
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
        if (!OperatingSystem.IsWindows())
        {
            // 0600 — owner read/write only. Matches JsonConnectionStore; the
            // MRU file carries the user's project paths.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
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
