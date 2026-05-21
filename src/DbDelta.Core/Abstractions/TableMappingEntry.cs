namespace DbDelta.Core.Abstractions;

/// <summary>
/// Maps a specific table in the source database to a corresponding table
/// in the target database, allowing for schema and name differences.
/// </summary>
public sealed record TableMappingEntry(
    string SourceSchema,
    string SourceName,
    string TargetSchema,
    string TargetName);
