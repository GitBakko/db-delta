using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP USER + ALTER USER for default schema. The CREATE
/// form depends on the principal type:
/// <list type="bullet">
///   <item><c>'S'</c> (SQL user) → <c>FROM LOGIN [&lt;login&gt;]</c> or
///       <c>WITHOUT LOGIN</c> when no login is mapped</item>
///   <item><c>'U' / 'G'</c> (Windows user/group) → <c>FROM LOGIN [domain\name]</c></item>
///   <item><c>'E' / 'X'</c> (Azure AD user/group) → <c>FROM EXTERNAL PROVIDER</c></item>
/// </list>
/// </summary>
public sealed class UserScriptEmitter
{
    public string EmitCreate(DatabaseUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        StringBuilder sb = new();
        sb.Append("CREATE USER ").Append(Sql.Q(user.Name));
        switch (user.TypeCode)
        {
            case "S":
                sb.Append(string.IsNullOrEmpty(user.LoginName)
                    ? " WITHOUT LOGIN"
                    : $" FOR LOGIN {Sql.Q(user.LoginName)}");
                break;
            case "U" or "G":
                if (!string.IsNullOrEmpty(user.LoginName))
                {
                    sb.Append(" FOR LOGIN ").Append(Sql.Q(user.LoginName));
                }
                break;
            case "E" or "X":
                sb.Append(" FROM EXTERNAL PROVIDER");
                break;
            default:
                break;
        }
        if (!string.IsNullOrEmpty(user.DefaultSchema)
            && !string.Equals(user.DefaultSchema, "dbo", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" WITH DEFAULT_SCHEMA = ").Append(Sql.Q(user.DefaultSchema));
        }
        sb.Append(';');
        return sb.ToString();
    }

    public string EmitAlterDefaultSchema(DatabaseUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return $"ALTER USER {Sql.Q(user.Name)} WITH DEFAULT_SCHEMA = {Sql.Q(user.DefaultSchema)};";
    }

    public string EmitDrop(DatabaseUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return $"DROP USER {Sql.Q(user.Name)};";
    }
}
