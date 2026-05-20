namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A user table (sys.tables row).
/// </summary>
public sealed record Table(
    string Schema,
    string Name,
    IReadOnlyList<Column> Columns)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Table");
}

/// <summary>
/// Tuple identifying an object across two schemas being compared.
/// </summary>
public readonly record struct ObjectIdentity(string SchemaName, string ObjectName, string Kind);
