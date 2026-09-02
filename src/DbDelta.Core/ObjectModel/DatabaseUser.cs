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
    /// <summary>
    /// The principal is mapped to a server login whose name the reading
    /// connection is not allowed to see. <c>UserReader</c> LEFT JOINs
    /// <c>sys.server_principals</c>, which metadata visibility filters down to
    /// the logins the caller owns, so a least-privilege account reads NULL for
    /// every other one. That NULL is indistinguishable from a user created
    /// WITHOUT LOGIN unless something carries the difference: this flag does.
    /// It is an <c>init</c> property rather than a positional member so every
    /// existing construction still compiles and still means "read in full".
    /// </summary>
    public bool LoginNameIsHidden { get; init; }

    /// <summary>
    /// The mapped login is missing from <c>sys.server_principals</c> AND the
    /// reading connection is allowed to see every login, so the NULL is not
    /// metadata visibility hiding a name — the login is gone and the user is
    /// orphaned. Set only when <c>VIEW ANY DEFINITION</c> is held at server
    /// scope, so the ambiguous case keeps the conservative reading.
    /// <para>
    /// It does not change the verdict: an orphaned user is refused exactly like
    /// a hidden one, because there is no statement that reproduces it —
    /// <c>CREATE USER … WITHOUT LOGIN</c> lands on authentication_type NONE
    /// where the source has INSTANCE, so it would not even converge. What it
    /// changes is the sentence the operator reads. Measured on 2026-09-02
    /// against a real database as sa: the one-flag message blamed a permission
    /// problem that could not exist and named a remedy that could not work.
    /// </para>
    /// </summary>
    public bool LoginIsOrphaned { get; init; }

    /// <summary>
    /// Same login as <paramref name="other"/>, as far as either side was able
    /// to read one. A name hidden from one reader is matched on "is it mapped
    /// to a login at all", never on the NULL it came back as — comparing the
    /// NULL makes every user Different under a least-privilege account, and the
    /// script that follows drops and re-creates principals that were correct.
    /// </summary>
    /// <remarks>
    /// Both structures that compare two users route here so they cannot drift
    /// apart: <c>ComparisonEngine.UsersEqual</c> decides the status,
    /// <c>ScriptGenerator.DefaultSchemaIsOnlyDifference</c> decides whether an
    /// ALTER covers it or the pair needs DROP + CREATE.
    /// </remarks>
    public bool LoginMatches(DatabaseUser other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return LoginNameIsHidden || other.LoginNameIsHidden
            ? IsMappedToALogin == other.IsMappedToALogin
            : string.Equals(LoginName, other.LoginName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A hidden name is still a name: the mapping exists either way.</summary>
    private bool IsMappedToALogin => LoginNameIsHidden || LoginName is not null;

    public ObjectIdentity Identity => new(SchemaName: string.Empty, ObjectName: Name, Kind: "User");
}
