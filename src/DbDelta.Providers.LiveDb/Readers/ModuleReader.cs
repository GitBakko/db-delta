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
/// <remarks>
/// SQL Server does not expose an <c>is_encrypted</c> column on
/// <c>sys.sql_modules</c>; the canonical signal that a module was created
/// <c>WITH ENCRYPTION</c> is <c>definition IS NULL</c>. We therefore project
/// only the definition and coerce <c>IsEncrypted = true</c> when the body is
/// NULL. The same coercion also captures the rare permission edge case where
/// the caller can see the object metadata but cannot read the module text.
/// </remarks>
internal sealed class ModuleReader
{
    private const string ViewQuery = """
        SELECT s.name AS SchemaName,
               v.name AS Name,
               sm.definition AS Body
        FROM sys.views AS v
        INNER JOIN sys.schemas AS s ON s.schema_id = v.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = v.object_id
        WHERE v.is_ms_shipped = 0
        ORDER BY s.name, v.name;
        """;

    private const string ProcQuery = """
        SELECT s.name AS SchemaName,
               p.name AS Name,
               sm.definition AS Body
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
        await using SqlCommand cmd = new(ViewQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = body is null;
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
        await using SqlCommand cmd = new(ProcQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            bool encrypted = body is null;
            procs.Add(new StoredProcedure(schema, name, body, encrypted));
        }
        return procs;
    }

    private const string FunctionQuery = """
        SELECT s.name AS SchemaName,
               o.name AS Name,
               sm.definition AS Body,
               o.type AS RawType
        FROM sys.objects AS o
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        LEFT JOIN sys.sql_modules AS sm ON sm.object_id = o.object_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('FN', 'IF', 'TF')
        ORDER BY s.name, o.name;
        """;

    /// <summary>
    /// Reads user-defined functions (scalar, inline-TVF, multi-TVF). Same
    /// NULL-body coercion as the view + procedure readers — when the
    /// definition row is missing (encrypted or permission edge case) the
    /// result has <c>Body = null</c> and <c>IsEncrypted = true</c>.
    /// </summary>
    public async Task<IReadOnlyList<Function>> ReadFunctionsAsync(SqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<Function> functions = [];
        await using SqlCommand cmd = new(FunctionQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string name = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            string rawType = r.GetString(3).Trim();
            FunctionKind kind = rawType switch
            {
                "FN" => FunctionKind.Scalar,
                "IF" => FunctionKind.InlineTableValued,
                "TF" => FunctionKind.MultiStatementTableValued,
                _ => FunctionKind.Scalar,
            };
            bool encrypted = body is null;
            functions.Add(new Function(schema, name, body, encrypted, kind));
        }
        return functions;
    }

    private const string TriggerQuery = """
        SELECT ps.name AS TriggerSchemaName,
               tr.name AS TriggerName,
               sm.definition AS Body,
               ts.name AS ParentSchema,
               t.name  AS ParentTable,
               CAST(tr.is_disabled AS BIT)             AS IsDisabled,
               CAST(tr.is_not_for_replication AS BIT)  AS IsNotForReplication
        FROM sys.triggers AS tr
        INNER JOIN sys.objects AS o ON o.object_id = tr.object_id
        INNER JOIN sys.schemas AS ps ON ps.schema_id = o.schema_id
        INNER JOIN sys.tables  AS t  ON t.object_id  = tr.parent_id
        INNER JOIN sys.schemas AS ts ON ts.schema_id = t.schema_id
        LEFT  JOIN sys.sql_modules AS sm ON sm.object_id = tr.object_id
        WHERE tr.parent_class = 1
          AND tr.is_ms_shipped = 0
        ORDER BY ps.name, tr.name;
        """;

    /// <summary>
    /// Reads user DML triggers (INSERT / UPDATE / DELETE) with their parent
    /// table identity and the IsDisabled / IsNotForReplication flags.
    /// Encrypted bodies surface as <c>IsEncrypted = true</c> + <c>Body = null</c>.
    /// </summary>
    public async Task<IReadOnlyList<Trigger>> ReadTriggersAsync(SqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<Trigger> triggers = [];
        await using SqlCommand cmd = new(TriggerQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string triggerSchema = r.GetString(0);
            string triggerName = r.GetString(1);
            string? body = r.IsDBNull(2) ? null : r.GetString(2);
            string parentSchema = r.GetString(3);
            string parentTable = r.GetString(4);
            bool isDisabled = !r.IsDBNull(5) && r.GetBoolean(5);
            bool isNfr = !r.IsDBNull(6) && r.GetBoolean(6);
            bool encrypted = body is null;
            triggers.Add(new Trigger(
                Schema: triggerSchema,
                Name: triggerName,
                Body: body,
                IsEncrypted: encrypted,
                ParentSchema: parentSchema,
                ParentTable: parentTable,
                IsDisabled: isDisabled,
                IsNotForReplication: isNfr));
        }
        return triggers;
    }
}
