using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Thrown when a permission row carries no securable DbDelta can name, so the
/// statement it would emit would apply to a wider scope than the row describes.
/// </summary>
/// <remarks>
/// <para>
/// This is the permission-shaped twin of <see cref="UnscriptableIndexException"/>,
/// and it exists for a sharper reason. A database-scoped grant takes no
/// <c>ON</c> clause by design — <c>GRANT CONNECT TO [app];</c> is correct and
/// portable. An OBJECT_OR_COLUMN row whose object name never resolved produces
/// the *same text*, and that text is a grant over the whole database. The
/// missing clause does not narrow the statement or fail it; it widens it
/// silently, from one object to every object.
/// </para>
/// <para>
/// The name can go missing for an ordinary reason: <c>PermissionReader</c>
/// LEFT JOINs the catalog view that names the securable, and metadata
/// visibility returns NULL for an object the reading login cannot see. So the
/// row that triggers this is precisely the row read under reduced privilege —
/// the case where quietly granting more is least acceptable.
/// </para>
/// <para>
/// Refusing costs little in practice: permissions are emitted only when the
/// caller clears <c>IgnorePermissions</c>, which is set by default for Redgate
/// parity. Generation also runs to completion before a single batch is sent, so
/// a throw here stops the deploy with no SQL executed. Callers surface it as a
/// refusal, not as a crash: the CLI exits 30 and the app shows the error banner.
/// </para>
/// </remarks>
public sealed class UnscriptablePermissionException(Permission permission)
    : Exception($"Refusing to script the {permission.Action} permission for "
              + $"{Sql.Q(permission.GranteeName)}: its class is "
              + $"{permission.ClassDesc} but the securable has no name, so the statement "
              + "would grant over the whole database instead of over one object.")
{
    /// <summary>The row that stopped the run.</summary>
    public Permission Permission { get; } = permission;

    public string GranteeName { get; } = permission.GranteeName;

    public string Action { get; } = permission.Action;

    public string ClassDesc { get; } = permission.ClassDesc;

    /// <summary>
    /// Throws unless the row is genuinely database-scoped. The single guard both
    /// emission paths call, because both route through the same
    /// <c>ON</c>-clause builder.
    /// </summary>
    public static void ThrowIfTargetUnnamed(Permission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (string.Equals(permission.ClassDesc, "DATABASE", StringComparison.Ordinal)) { return; }
        throw new UnscriptablePermissionException(permission);
    }
}
