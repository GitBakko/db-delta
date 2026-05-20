using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads code modules (views and stored procedures) via <c>sys.sql_modules</c>.
/// Encrypted modules — and the rare permission-edge case where the catalog
/// surfaces a NULL definition — both arrive with <c>Body = null</c> and
/// <c>IsEncrypted = true</c> so the comparison engine can flag them without
/// attempting a body diff.
/// </summary>
public sealed class ModuleReader
{
    private const string ViewSql = """
        SELECT s.name AS SchemaName,
               v.name AS Name,
               sm.definition AS Body,
               CAST(sm.is_encrypted AS BIT) AS IsEncrypted
        FROM sys.views AS v
        INNER JOIN sys.schemas AS s ON s.schema_id = v.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = v.object_id
        WHERE v.is_ms_shipped = 0
        ORDER BY s.name, v.name;
        """;

    private const string ProcSql = """
        SELECT s.name AS SchemaName,
               p.name AS Name,
               sm.definition AS Body,
               CAST(sm.is_encrypted AS BIT) AS IsEncrypted
        FROM sys.procedures AS p
        INNER JOIN sys.schemas AS s ON s.schema_id = p.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = p.object_id
        WHERE p.is_ms_shipped = 0
        ORDER BY s.name, p.name;
        """;

    /// <summary>
    /// Reads user views with their definitions. Encrypted views (or those whose
    /// definition came back NULL for permission reasons) round-trip with
    /// <c>Body = null</c> + <c>IsEncrypted = true</c>.
    /// </summary>
    public async Task<IReadOnlyList<View>> ReadViewsAsync(SqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<View> views = [];
        await using SqlCommand cmd = new(ViewSql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = (!r.IsDBNull(3) && r.GetBoolean(3)) || body is null;
            views.Add(new View(schema, name, body, encrypted));
        }
        return views;
    }

    /// <summary>
    /// Reads user stored procedures with their definitions. Same encryption /
    /// NULL-body coercion as <see cref="ReadViewsAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<StoredProcedure>> ReadProceduresAsync(SqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<StoredProcedure> procs = [];
        await using SqlCommand cmd = new(ProcSql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = (!r.IsDBNull(3) && r.GetBoolean(3)) || body is null;
            procs.Add(new StoredProcedure(schema, name, body, encrypted));
        }
        return procs;
    }
}
