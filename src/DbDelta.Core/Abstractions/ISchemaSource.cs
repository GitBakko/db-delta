using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.Abstractions;

/// <summary>
/// Port for reading a complete <see cref="Database"/> object graph from
/// any source (live DB, scripts folder, snapshot, ...).
/// </summary>
public interface ISchemaSource
{
    string DisplayName { get; }
    Task<Result<Database>> LoadAsync(CancellationToken cancellationToken);
}
