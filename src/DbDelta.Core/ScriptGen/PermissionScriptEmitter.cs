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
        sb.Append(" ON ").Append(FormatTarget(p));
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
        sb.Append(" ON ").Append(FormatTarget(p))
          .Append(" FROM [").Append(p.GranteeName).Append("];");
        return sb.ToString();
    }

    private static string FormatTarget(Permission p) => p.ClassDesc switch
    {
        "DATABASE" => "DATABASE",
        "SCHEMA" => $"SCHEMA::[{p.ObjectSchema ?? p.ObjectName}]",
        _ when !string.IsNullOrEmpty(p.ObjectSchema) && !string.IsNullOrEmpty(p.ObjectName) =>
            $"[{p.ObjectSchema}].[{p.ObjectName}]",
        _ when !string.IsNullOrEmpty(p.ObjectName) => $"[{p.ObjectName}]",
        _ => "DATABASE",
    };
}
