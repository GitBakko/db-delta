namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A row from sys.database_permissions describing a GRANT / DENY on an
/// object-class target.  Identity for diffing = the tuple
/// (Grantee, Action, ClassDesc, ObjectSchema, ObjectName, ColumnName).
/// REVOKE is not modelled here — it's the absence of a GRANT/DENY row.
/// </summary>
public sealed record Permission(
    string GranteeName,
    string Action,
    PermissionState State,
    string ClassDesc,
    string? ObjectSchema,
    string? ObjectName,
    string? ColumnName)
{
    /// <summary>
    /// Stable, schema-qualified, opaque identifier for the row used as the
    /// diff identity. Format:
    /// <c>{State}|{Action}|{Grantee}|{ClassDesc}|{Schema}.{Name}.{Column}</c>.
    /// </summary>
    public string DiffKey =>
        $"{State}|{Action}|{GranteeName}|{ClassDesc}|{ObjectSchema}.{ObjectName}.{ColumnName}";

    public ObjectIdentity Identity => new(
        SchemaName: ObjectSchema ?? string.Empty,
        ObjectName: $"{State} {Action} TO [{GranteeName}] ON {ObjectName ?? "DATABASE"}"
                    + (ColumnName is null ? string.Empty : $"({ColumnName})"),
        Kind: "Permission");
}

/// <summary>State of a permission row.</summary>
public enum PermissionState
{
    /// <summary>GRANT.</summary>
    Grant,
    /// <summary>GRANT WITH GRANT OPTION.</summary>
    GrantWithGrantOption,
    /// <summary>DENY.</summary>
    Deny,
}
