using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits GRANT / DENY / REVOKE statements for the M6 Permission row model.
/// Scope is restricted to v1 supported targets:
/// <list type="bullet">
///   <item>OBJECT_OR_COLUMN — schema.object (.column) targets</item>
///   <item>SCHEMA — schema-level grants</item>
///   <item>DATABASE — database-level grants (no target object)</item>
/// </list>
/// </summary>
public sealed class PermissionScriptEmitter
{
    public string EmitGrantOrDeny(Permission p)
    {
        ArgumentNullException.ThrowIfNull(p);
        StringBuilder sb = new();
        sb.Append(p.State switch
        {
            PermissionState.Deny => "DENY ",
            PermissionState.Grant => "GRANT ",
            PermissionState.GrantWithGrantOption => "GRANT ",
            _ => "GRANT ",
        });
        sb.Append(p.Action);
        if (!string.IsNullOrEmpty(p.ColumnName))
        {
            sb.Append(" ([").Append(p.ColumnName).Append("])");
        }
        AppendOnTarget(sb, p);
        sb.Append(" TO [").Append(p.GranteeName).Append(']');
        if (p.State == PermissionState.GrantWithGrantOption)
        {
            sb.Append(" WITH GRANT OPTION");
        }
        sb.Append(';');
        return sb.ToString();
    }

    public string EmitRevoke(Permission p)
    {
        ArgumentNullException.ThrowIfNull(p);
        StringBuilder sb = new();
        sb.Append("REVOKE ").Append(p.Action);
        if (!string.IsNullOrEmpty(p.ColumnName))
        {
            sb.Append(" ([").Append(p.ColumnName).Append("])");
        }
        AppendOnTarget(sb, p);
        sb.Append(" FROM [").Append(p.GranteeName).Append("];");
        return sb.ToString();
    }

    /// <summary>
    /// Appends the <c>ON &lt;securable&gt;</c> clause, or nothing at all for a
    /// database-scoped permission.
    /// </summary>
    /// <remarks>
    /// A database-scoped grant takes NO <c>ON</c> clause — it applies to the
    /// database the batch is executing in (<c>GRANT CONNECT TO [app];</c>).
    /// <c>ON DATABASE</c> is not valid T-SQL; the only <c>ON</c> form is
    /// <c>ON DATABASE::[name]</c>, which would hard-code a database name into a
    /// script that is meant to be portable across target databases. Omitting
    /// the clause is both valid and correct, because the deployment script runs
    /// in the target database's context.
    /// </remarks>
    private static void AppendOnTarget(StringBuilder sb, Permission p)
    {
        string? target = FormatTarget(p);
        if (target is null) { return; }
        sb.Append(" ON ").Append(target);
    }

    private static string? FormatTarget(Permission p) => p.ClassDesc switch
    {
        "DATABASE" => null,
        "SCHEMA" => $"SCHEMA::[{p.ObjectSchema ?? p.ObjectName}]",
        _ when !string.IsNullOrEmpty(p.ObjectSchema) && !string.IsNullOrEmpty(p.ObjectName) =>
            $"[{p.ObjectSchema}].[{p.ObjectName}]",
        _ when !string.IsNullOrEmpty(p.ObjectName) => $"[{p.ObjectName}]",
        _ => null,
    };
}
