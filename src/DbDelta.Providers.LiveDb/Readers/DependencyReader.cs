using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Builds object-level dependency edges (#24) from
/// sys.sql_expression_dependencies. Every row is emitted as
/// <see cref="EdgeKind.ModuleReference"/> — the resolver only distinguishes
/// foreign-key edges (which this reader does not produce), so no finer
/// classification is needed here. Module bodies (views, functions, procedures,
/// triggers) surface as expected; computed-column/check/default expressions
/// that reference a function or sequence happen to surface here too, because
/// SQL Server records them in sys.sql_expression_dependencies with the owning
/// table as the referencing object. Unresolved references (cross-db, dynamic
/// SQL, NULL referenced_id) yield no edge — the affected node then falls back
/// to its stable kind/alphabetical slot in the resolver.
/// </summary>
internal sealed class DependencyReader
{
    private const string Sql = """
        SELECT
            referencing_schema = OBJECT_SCHEMA_NAME(d.referencing_id),
            referencing_name   = OBJECT_NAME(d.referencing_id),
            referencing_type   = ro.type,
            referenced_schema  = ISNULL(d.referenced_schema_name, OBJECT_SCHEMA_NAME(d.referenced_id)),
            referenced_name    = ISNULL(d.referenced_entity_name, OBJECT_NAME(d.referenced_id)),
            referenced_type    = eo.type
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS ro ON ro.object_id = d.referencing_id AND ro.is_ms_shipped = 0
        LEFT  JOIN sys.objects AS eo ON eo.object_id = d.referenced_id
        WHERE d.referenced_id IS NOT NULL;
        """;

    public async Task<IReadOnlyList<DependencyEdge>> ReadAsync(
        SqlConnection connection, CancellationToken ct)
    {
        List<DependencyEdge> edges = [];
        await using SqlCommand cmd = new(Sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string? depSchema = r.IsDBNull(0) ? null : r.GetString(0);
            string? depName = r.IsDBNull(1) ? null : r.GetString(1);
            string? depType = r.IsDBNull(2) ? null : r.GetString(2).Trim();
            string? refSchema = r.IsDBNull(3) ? null : r.GetString(3);
            string? refName = r.IsDBNull(4) ? null : r.GetString(4);
            string? refType = r.IsDBNull(5) ? null : r.GetString(5).Trim();

            if (depSchema is null || depName is null || depType is null || refSchema is null || refName is null || refType is null)
            {
                continue;
            }

            string? depKind = MapKind(depType);
            string? refKind = MapKind(refType);
            if (depKind is null || refKind is null) { continue; }

            edges.Add(new DependencyEdge(
                new ObjectIdentity(depSchema, depName, depKind),
                new ObjectIdentity(refSchema, refName, refKind),
                EdgeKind.ModuleReference));
        }
        return edges;
    }

    private static string? MapKind(string type) => type switch
    {
        "U" => "Table",
        "V" => "View",
        "P" => "Procedure",
        "FN" or "IF" or "TF" or "FS" or "FT" => "Function",
        "TR" => "Trigger",
        "SN" => "Synonym",
        "SO" => "Sequence",
        _ => null,
    };
}
