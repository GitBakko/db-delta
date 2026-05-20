namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server schema (namespace for objects).
/// </summary>
public sealed record Schema(string Name)
{
    public ObjectIdentity Identity => new(SchemaName: Name, ObjectName: string.Empty, Kind: "Schema");
}
