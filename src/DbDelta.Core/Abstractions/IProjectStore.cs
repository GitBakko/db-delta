namespace DbDelta.Core.Abstractions;

/// <summary>
/// Reads / writes <c>.dbd</c> project files (XML).
/// </summary>
public interface IProjectStore
{
    Task<DbDeltaProject> LoadAsync(string filePath, CancellationToken ct);
    Task SaveAsync(string filePath, DbDeltaProject project, CancellationToken ct);
}
