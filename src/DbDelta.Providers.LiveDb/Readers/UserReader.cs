using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads database users from sys.database_principals.  Excludes:
/// <list type="bullet">
///   <item><c>is_fixed_role=1</c> — built-in fixed db_ roles (read as Role)</item>
///   <item>Built-in principals (<c>principal_id &lt; 5</c>: dbo / guest /
///       INFORMATION_SCHEMA / sys)</item>
///   <item>Asymmetric-key / certificate-based users (<c>type IN ('K','C')</c>)
///       — out of scope for v1 (require key/cert deployment first)</item>
/// </list>
/// <para>
/// The LEFT JOIN onto <c>sys.server_principals</c> reads NULL twice over: for a
/// user created WITHOUT LOGIN, and for a login the connection is not allowed to
/// see — that view is filtered by metadata visibility, so a least-privilege
/// account gets NULL for every login it does not own.
/// <c>authentication_type</c> is not filtered and separates the two:
/// 1 (INSTANCE) and 3 (WINDOWS) mean a server login exists, whatever we can
/// read of its name. See <see cref="DatabaseUser.LoginNameIsHidden"/>.
/// </para>
/// </summary>
internal sealed class UserReader
{
    private const string Sql = """
        SELECT
            p.name             AS PrincipalName,
            p.type             AS TypeCode,
            sp.name             AS LoginName,
            p.default_schema_name AS DefaultSchema,
            CAST(CASE WHEN sp.name IS NULL AND p.authentication_type IN (1, 3)
                      THEN 1 ELSE 0 END AS bit) AS LoginNameIsHidden
        FROM sys.database_principals AS p
        LEFT JOIN sys.server_principals AS sp ON sp.sid = p.sid
        WHERE p.type IN ('S','U','G','E','X')
          AND p.principal_id > 4
          AND p.is_fixed_role = 0
        ORDER BY p.name;
        """;

    public async Task<IReadOnlyList<DatabaseUser>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        List<DatabaseUser> result = [];
        await using SqlCommand cmd = new(Sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = r.GetString(0);
            string type = r.GetString(1).Trim();
            string? login = r.IsDBNull(2) ? null : r.GetString(2);
            string defaultSchema = r.IsDBNull(3) ? "dbo" : r.GetString(3);
            result.Add(new DatabaseUser(name, type, login, defaultSchema)
            {
                LoginNameIsHidden = r.GetBoolean(4),
            });
        }
        return result;
    }
}
