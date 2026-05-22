namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server SYNONYM object (sys.synonyms). Lightweight alias to another
/// database object — the "base_object_name" stored as a single string per
/// SQL Server's persisted form: <c>[server].[database].[schema].[object]</c>
/// (each segment optional). We expose it verbatim plus best-effort parsed
/// segments so the diff viewer can highlight which segment changed.
/// </summary>
public sealed record Synonym(
    string Schema,
    string Name,
    string BaseObjectName,
    string? TargetServer,
    string? TargetDatabase,
    string? TargetSchema,
    string? TargetObject)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Synonym");
}
