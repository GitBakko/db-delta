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
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        return [.. doc.Entries
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.LastUsedUtc)
            .ThenByDescending(e => e.CreatedUtc)];
    }

    public async Task<ConnectionEntry> UpsertAsync(ConnectionEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Where(e => e.Id != entry.Id), entry];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
        return entry;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Where(e => e.Id != id)];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
    }

    public async Task TouchUsageAsync(Guid id, CancellationToken ct)
    {
        Document doc = await ReadDocumentAsync(ct).ConfigureAwait(false);
        List<ConnectionEntry> next = [.. doc.Entries.Select(e =>
            e.Id == id ? e with { LastUsedUtc = DateTime.UtcNow } : e)];
        await WriteAtomicAsync(new Document(CurrentSchemaVersion, next), ct).ConfigureAwait(false);
    }

    private async Task<Document> ReadDocumentAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Document(CurrentSchemaVersion, []);
        }
        await using FileStream fs = File.OpenRead(_filePath);
        Document? doc = await JsonSerializer.DeserializeAsync<Document>(fs, s_json, ct).ConfigureAwait(false);
        return doc ?? new Document(CurrentSchemaVersion, []);
    }

    private async Task WriteAtomicAsync(Document doc, CancellationToken ct)
    {
        string tmp = _filePath + ".tmp";
        await using (FileStream fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, doc, s_json, ct).ConfigureAwait(false);
        }
        File.Move(tmp, _filePath, overwrite: true);
    }

    private sealed record Document(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("entries")] IReadOnlyList<ConnectionEntry> Entries);
}
