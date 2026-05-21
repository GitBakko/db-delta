namespace DbDelta.Core.Abstractions;

/// <summary>
/// Per-user MRU + preset list. Implementation persists to
/// <c>%LOCALAPPDATA%\DbDelta\connections.json</c> on Windows
/// (equivalent paths on macOS / Linux).
/// </summary>
public interface IConnectionStore
{
    Task<IReadOnlyList<ConnectionEntry>> LoadAsync(CancellationToken ct);

    /// <summary>Inserts a new entry or replaces an existing one by <see cref="ConnectionEntry.Id"/>.</summary>
    Task<ConnectionEntry> UpsertAsync(ConnectionEntry entry, CancellationToken ct);

    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Bumps <see cref="ConnectionEntry.LastUsedUtc"/> to <c>UtcNow</c>.</summary>
    Task TouchUsageAsync(Guid id, CancellationToken ct);
}
