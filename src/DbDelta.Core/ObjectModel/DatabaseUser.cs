namespace DbDelta.Core.ObjectModel;

/// <summary>
/// A SQL Server database user (sys.database_principals). M6 covers the
/// principal kinds compared in v1:
/// <list type="bullet">
///   <item><c>'S'</c> — SQL user with password / mapped to SQL login</item>
///   <item><c>'U'</c> — Windows user</item>
///   <item><c>'G'</c> — Windows group</item>
///   <item><c>'E'</c> — external user (Azure AD)</item>
///   <item><c>'X'</c> — external group (Azure AD)</item>
/// </list>
/// Asymmetric-key / certificate-based users ('K', 'C') and the built-in
/// 'dbo' / 'guest' / fixed-role-owned principals are filtered out by the
/// reader.
/// </summary>
public sealed record DatabaseUser(
    string Name,
    string TypeCode,
    string? LoginName,
    string DefaultSchema)
{
    public ObjectIdentity Identity => new(SchemaName: string.Empty, ObjectName: Name, Kind: "User");
}
