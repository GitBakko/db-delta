using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads CUSTOM database roles (sys.database_principals where type='R' AND
/// is_fixed_role=0) plus their memberships from sys.database_role_members.
/// Built-in db_owner / db_datareader / etc. are skipped — they exist on
/// every database by design and aren't user-managed.
/// </summary>
internal sealed class RoleReader
{
    private const string RolesSql = """
        SELECT
            r.name      AS RoleName,
            o.name      AS OwnerName
        FROM sys.database_principals AS r
        LEFT JOIN sys.database_principals AS o ON o.principal_id = r.owning_principal_id
        WHERE r.type = 'R'
          AND r.is_fixed_role = 0
          AND r.principal_id > 4
        ORDER BY r.name;
        """;

    private const string MembersSql = """
        SELECT
            r.name AS RoleName,
            m.name AS MemberName
        FROM sys.database_role_members AS rm
        INNER JOIN sys.database_principals AS r ON r.principal_id = rm.role_principal_id
        INNER JOIN sys.database_principals AS m ON m.principal_id = rm.member_principal_id
        WHERE r.is_fixed_role = 0
        ORDER BY r.name, m.name;
        """;

    public async Task<IReadOnlyList<DatabaseRole>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        // 1) collect role shells
        Dictionary<string, (string Owner, List<string> Members)> shells = new(StringComparer.OrdinalIgnoreCase);
        await using (SqlCommand cmd = new(RolesSql, connection))
        await using (SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string name = r.GetString(0);
                string owner = r.IsDBNull(1) ? "dbo" : r.GetString(1);
                shells[name] = (owner, []);
            }
        }

        // 2) attach members
        await using (SqlCommand cmd = new(MembersSql, connection))
        await using (SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string roleName = r.GetString(0);
                string memberName = r.GetString(1);
                if (shells.TryGetValue(roleName, out (string Owner, List<string> Members) shell))
                {
                    shell.Members.Add(memberName);
                }
            }
        }

        return [.. shells.Select(kv =>
            new DatabaseRole(kv.Key, kv.Value.Owner, kv.Value.Members))];
    }
}
