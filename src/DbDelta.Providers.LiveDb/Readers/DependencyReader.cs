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
/// triggers) surface as expected, and so do computed columns — a computed
/// column is not an object, so SQL Server records the TABLE as the referencing
/// entity. Unresolved references (cross-db, dynamic SQL, NULL referenced_id)
/// yield no edge — the affected node then falls back to its stable
/// kind/alphabetical slot in the resolver.
/// </summary>
/// <remarks>
/// A CHECK or DEFAULT constraint is a different matter, and this comment used
/// to claim otherwise. Those ARE objects, so the referencing entity is the
/// constraint itself, of type <c>C</c> or <c>D</c> — kinds DbDelta does not
/// model, so the edge was dropped and the dependency vanished. A CHECK calling
/// a scalar function then produced a script that created the table BEFORE the
/// function, and the deploy died on Msg 4121: "Cannot find either column dbo or
/// the user-defined function or aggregate". Found on 2026-08-20 by parity
/// scenario 20, which exists for exactly this shape.
/// <para>
/// Such a row is attributed to the constraint's PARENT TABLE, which is the
/// object DbDelta orders. A TRIGGER also carries a parent_object_id and is
/// deliberately NOT remapped: it is modelled in its own right and its
/// dependencies belong to it.
/// </para>
/// </remarks>
internal sealed class DependencyReader
{
    private const string Sql = """
        SELECT
            referencing_schema = OBJECT_SCHEMA_NAME(x.OwnerId),
            referencing_name   = OBJECT_NAME(x.OwnerId),
            referencing_type   = oo.type,
            referenced_schema  = ISNULL(x.referenced_schema_name, OBJECT_SCHEMA_NAME(x.referenced_id)),
            referenced_name    = ISNULL(x.referenced_entity_name, OBJECT_NAME(x.referenced_id)),
            referenced_type    = eo.type,
            -- Not an ordering input: a schemabound edge points the same way as
            -- any other. It says the server ENFORCES this reference, so the
            -- referenced object cannot be dropped (Msg 3729) or renamed
            -- (Msg 15336) while it stands — which is what makes an identity
            -- rebuild of that table unwritable.
            is_schema_bound    = x.is_schema_bound_reference
        FROM (
            SELECT
                d.referenced_id,
                d.referenced_schema_name,
                d.referenced_entity_name,
                d.is_schema_bound_reference,
                -- A CHECK or DEFAULT constraint is an object of its own, and
                -- not one DbDelta models: attribute its references to the table
                -- that carries it, which is the node the resolver orders. A
                -- trigger also has a parent and keeps its own edges.
                OwnerId = CASE WHEN ro.type IN ('C', 'D') THEN ro.parent_object_id
                               ELSE d.referencing_id END
            FROM sys.sql_expression_dependencies AS d
            INNER JOIN sys.objects AS ro ON ro.object_id = d.referencing_id AND ro.is_ms_shipped = 0
            WHERE d.referenced_id IS NOT NULL
        ) AS x
        INNER JOIN sys.objects AS oo ON oo.object_id = x.OwnerId
        LEFT  JOIN sys.objects AS eo ON eo.object_id = x.referenced_id;
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
            bool isSchemaBound = !r.IsDBNull(6) && r.GetBoolean(6);

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
                EdgeKind.ModuleReference,
                isSchemaBound));
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
