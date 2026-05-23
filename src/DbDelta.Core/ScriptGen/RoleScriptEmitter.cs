using System.Text;
using DbDelta.Core.ObjectModel;

namespace DbDelta.Core.ScriptGen;

/// <summary>
/// Emits CREATE / DROP ROLE plus <c>ALTER ROLE … ADD MEMBER</c> statements
/// for each member of the role.
/// </summary>
public sealed class RoleScriptEmitter
{
    public string EmitCreate(DatabaseRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        StringBuilder sb = new();
        sb.Append("CREATE ROLE [").Append(role.Name).Append(']');
        if (!string.IsNullOrEmpty(role.OwnerName)
            && !string.Equals(role.OwnerName, "dbo", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" AUTHORIZATION [").Append(role.OwnerName).Append(']');
        }
        sb.AppendLine(";");

        foreach (string member in role.Members.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("ALTER ROLE [").Append(role.Name).Append("] ADD MEMBER [").Append(member).AppendLine("];");
        }

        return sb.ToString().TrimEnd();
    }

    public string EmitDrop(DatabaseRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return $"DROP ROLE [{role.Name}];";
    }

    public string EmitAddMember(string roleName, string memberName) =>
        $"ALTER ROLE [{roleName}] ADD MEMBER [{memberName}];";

    public string EmitDropMember(string roleName, string memberName) =>
        $"ALTER ROLE [{roleName}] DROP MEMBER [{memberName}];";
}
