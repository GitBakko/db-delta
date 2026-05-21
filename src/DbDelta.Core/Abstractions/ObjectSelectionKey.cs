namespace DbDelta.Core.Abstractions;

/// <summary>
/// Identifies a single database object in the per-project selection table.
/// Used as the key in <see cref="DbDeltaProject.Selections"/>.
/// </summary>
public sealed record ObjectSelectionKey(
    string ObjectType,
    string SchemaName,
    string ObjectName);
