using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads object / schema / database scope GRANT and DENY rows from
/// sys.database_permissions, joining on sys.objects, sys.schemas and
/// sys.columns to resolve the target name. REVOKE entries are absent by
/// definition (REVOKE removes the row).
/// </summary>
internal sealed class PermissionReader
{
    private const string Sql = """
        SELECT
            p.state                 AS StateCode,
            p.permission_name       AS Action,
            grantee.name            AS GranteeName,
            p.class_desc            AS ClassDesc,
            sch.name                AS ObjectSchema,
            obj.name                AS ObjectName,
            col.name                AS ColumnName
        FROM sys.database_permissions AS p
        INNER JOIN sys.database_principals AS grantee
            ON grantee.principal_id = p.grantee_principal_id
        LEFT JOIN sys.objects AS obj
            ON p.class_desc = 'OBJECT_OR_COLUMN'
           AND obj.object_id = p.major_id
        LEFT JOIN sys.schemas AS sch
            ON (p.class_desc = 'OBJECT_OR_COLUMN' AND sch.schema_id = obj.schema_id)
            OR (p.class_desc = 'SCHEMA' AND sch.schema_id = p.major_id)
        LEFT JOIN sys.columns AS col
            ON p.class_desc = 'OBJECT_OR_COLUMN'
           AND col.object_id = p.major_id
           AND col.column_id = p.minor_id
           AND p.minor_id > 0
        WHERE grantee.is_fixed_role = 0
          AND grantee.principal_id > 4
          AND p.class_desc IN ('DATABASE','SCHEMA','OBJECT_OR_COLUMN')
        ORDER BY grantee.name, p.permission_name;
        """;

    public async Task<IReadOnlyList<Permission>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        List<Permission> result = [];
        await using SqlCommand cmd = new(Sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string stateCode = r.GetString(0).Trim();
            PermissionState state = stateCode switch
            {
                "G" => PermissionState.Grant,
                "W" => PermissionState.GrantWithGrantOption,
                "D" => PermissionState.Deny,
                _ => PermissionState.Grant,
            };
            result.Add(new Permission(
                GranteeName: r.GetString(2),
                Action: r.GetString(1),
                State: state,
                ClassDesc: r.GetString(3),
                ObjectSchema: r.IsDBNull(4) ? null : r.GetString(4),
                ObjectName: r.IsDBNull(5) ? null : r.GetString(5),
                ColumnName: r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return result;
    }
}
