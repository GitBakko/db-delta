#pragma warning disable IDE0005
using System.Collections.Generic;
using System.IO;
using System.Linq;
#pragma warning restore IDE0005
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Json;

/// <summary>
/// Persists the per-user connection list as JSON in
/// <c>%LOCALAPPDATA%\DbDelta\connections.json</c>. Writes are atomic
/// (write-temp + rename) so a crash never corrupts the file.
/// </summary>
public sealed class JsonConnectionStore : IConnectionStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public JsonConnectionStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>
    /// Convenience ctor that resolves the default per-user path
    /// (<c>LocalApplicationData/DbDelta/connections.json</c>).
    /// </summary>
    public static JsonConnectionStore CreateDefault()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DbDelta");
        Directory.CreateDirectory(dir);
        return new JsonConnectionStore(Path.Combine(dir, "connections.json"));
    }

    public async Task<IReadOnlyList<ConnectionEntry>> LoadAsync(CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct, forWrite: false).ConfigureAwait(false);
        return [.. doc.Entries
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.LastUsedUtc)
            .ThenByDescending(e => e.CreatedUtc)];
    }

    public async Task<ConnectionEntry> UpsertAsync(ConnectionEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Document doc = await ReadDocumentAsync(ct, forWrite: true).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Where(e => e.Id != entry.Id), entry];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
        return entry;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct, forWrite: true).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Where(e => e.Id != id)];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
    }

    public async Task TouchUsageAsync(Guid id, CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct, forWrite: true).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Select(e =>
            e.Id == id ? e with { LastUsedUtc = DateTime.UtcNow } : e)];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the backing document.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="forWrite">
    /// Distinguishes the two failure policies for an <em>unreadable</em> file
    /// (locked by a sync client, or a read-only / redirected profile — as
    /// opposed to corrupt content, which is always moved aside).
    /// <see langword="false"/> degrades to an empty list, because
    /// <see cref="LoadAsync"/> runs from app startup and an escaping exception
    /// there kills the process before any window is usable.
    /// <see langword="true"/> rethrows, because a read-modify-write that
    /// silently treated an unreadable file as empty would overwrite every
    /// saved connection with the single entry being upserted.
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
                MoveAside("future-schema");
                return new Document(CurrentSchemaVersion, []);
            }
            return doc;
        }
        catch (JsonException)
        {
            MoveAside("invalid-json");
            return new Document(CurrentSchemaVersion, []);
        }
        catch (Exception ex) when (!forWrite && ex is IOException or UnauthorizedAccessException)
        {
            return new Document(CurrentSchemaVersion, []);
        }
    }

    private void MoveAside(string reason)
    {
        string aside = _filePath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + reason;
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
            // 0600 — owner read/write only.
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
        [property: JsonPropertyName("entries")] IReadOnlyList<ConnectionEntry> Entries);
}
