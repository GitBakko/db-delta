namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A user-defined database role (sys.database_principals where type='R'
/// AND is_fixed_role=0). Carries the owner principal name + the membership
/// list (names of users / other roles that are members). Built-in
/// db_owner, db_datareader, etc. are filtered out by the reader.
/// </summary>
public sealed record DatabaseRole(
    string Name,
    string OwnerName,
    IReadOnlyList<string> Members)
{
    public ObjectIdentity Identity => new(SchemaName: string.Empty, ObjectName: Name, Kind: "Role");
}
