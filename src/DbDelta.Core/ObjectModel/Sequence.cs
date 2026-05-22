namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server SEQUENCE object (sys.sequences). Number-generator separate
/// from any table. Supports integer / decimal-no-scale data types; the
/// boundary values are stored as <see cref="long"/> here — sufficient for
/// the vast majority of real-world schemas (tinyint / smallint / int /
/// bigint / decimal(p,0)). Sequences with floating-point base types are
/// rare and out of scope for v1.
/// </summary>
public sealed record Sequence(
    string Schema,
    string Name,
    string DataType,
    long StartValue,
    long Increment,
    long? MinValue,
    long? MaxValue,
    bool IsCycling,
    bool IsCached,
    int? CacheSize)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "Sequence");
}
