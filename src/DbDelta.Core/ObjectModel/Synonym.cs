namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server SYNONYM object (sys.synonyms). Lightweight alias to another
/// database object — the "base_object_name" stored as a single string per
/// SQL Server's persisted form: <c>[server].[database].[schema].[object]</c>,
/// each segment optional, kept verbatim.
/// </summary>
/// <remarks>
/// It used to carry the four segments parsed out of that string as well, for a
/// diff viewer that never asked for them: nothing read them, the emitter writes
/// <see cref="BaseObjectName"/> as it found it, and the comparison is on that
/// same string. The parser behind them did not un-escape a doubled <c>]]</c>,
/// which was filed as a defect — of four dead fields. Deleting them closed it.
/// </remarks>
public sealed record Synonym(
    string Schema,
    string Name,
    string BaseObjectName)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Synonym");
}
