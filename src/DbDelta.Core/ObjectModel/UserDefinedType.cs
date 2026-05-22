namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server alias user-defined type (sys.types where is_user_defined=1
/// AND is_assembly_type=0). CLR UDTs (is_assembly_type=1) are intentionally
/// out of scope for v1 (per the M5 spec §6.2): they require deploying the
/// underlying assembly which is a separate problem.
///
/// <para>
/// <see cref="MaxLength"/> follows sys.types' convention for character / binary
/// base types (byte count, with -1 meaning MAX). <see cref="Precision"/> /
/// <see cref="Scale"/> are populated for decimal / numeric base types.
/// </para>
/// </summary>
public sealed record UserDefinedType(
    string Schema,
    string Name,
    string BaseTypeName,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable)
{
    public ObjectIdentity Identity => new(SchemaName: Schema, ObjectName: Name, Kind: "UserDefinedType");
}
