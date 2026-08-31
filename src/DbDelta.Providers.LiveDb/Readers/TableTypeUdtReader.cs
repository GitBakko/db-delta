using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads table-type user-defined types (sys.types where is_user_defined=1
/// AND is_table_type=1) plus the columns of their underlying type tables
/// (sys.columns JOIN type_table_object_id). Alias UDTs are read separately
/// by <see cref="UserDefinedTypeReader"/>.
/// </summary>
/// <remarks>
/// The keys, checks, defaults, identity and inline indexes are read for one
/// reason: SQL Server has no ALTER for a table type, so a change is DROP +
/// CREATE and whatever is missing here is dropped by the deploy. Reading them
/// is also what stops the comparison reporting Identical afterwards — the
/// failure mode that hid this for good, see docs/parity/redgate-2026-08-31.md.
/// </remarks>
internal sealed class TableTypeUdtReader
{
    // sys.table_types is the canonical catalog view for user-defined table
    // types. It exposes type_table_object_id (the object_id of the hidden
    // type table that backs the column list) — sys.types lacks that column.
    // is_memory_optimized is read here and nowhere else: it is a property of
    // the TYPE, not of its indexes, and it is the only thing that separates a
    // memory-optimized table type from a disk-based one — measured, because a
    // memory-optimized type may key itself on a plain range index whose
    // sys.indexes row is identical to a disk-based type's. Present since
    // SQL Server 2014, below DbDelta's 2016 compat floor.
    private const string TypesQuery = """
        SELECT
            s.name                    AS SchemaName,
            tt.name                   AS TypeName,
            tt.type_table_object_id   AS TypeTableObjectId,
            tt.is_memory_optimized    AS IsMemoryOptimized
        FROM sys.table_types AS tt
        INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
        WHERE tt.is_user_defined = 1
          AND (@schema IS NULL OR (s.name = @schema AND tt.name = @name))
        ORDER BY s.name, tt.name;
        """;

    private const string ColumnsQuery = """
        SELECT
            c.object_id                          AS TypeTableObjectId,
            c.name                               AS ColumnName,
            TYPE_NAME(c.user_type_id)            AS TypeName,
            c.max_length                         AS MaxLength,
            c.precision                          AS [Precision],
            c.scale                              AS Scale,
            c.is_nullable                        AS IsNullable,
            c.column_id                          AS Ordinal,
            c.collation_name                     AS CollationName,
            ty.is_user_defined                   AS IsUserDefinedType,
            c.is_identity                        AS IsIdentity,
            CAST(idc.seed_value AS bigint)       AS IdentitySeed,
            CAST(idc.increment_value AS bigint)  AS IdentityIncrement,
            dc.definition                        AS DefaultExpression,
            cmp.definition                       AS ComputedExpression,
            -- The schema is recoverable ONLY from this join: TYPE_NAME() returns the
            -- bare name whatever the type's schema, measured. Null for a built-in
            -- type, so the emitters can tell "dbo" from "not a user type".
            CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) END AS TypeSchema
        FROM sys.columns AS c
        INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        INNER JOIN sys.table_types AS tt ON tt.type_table_object_id = c.object_id
        LEFT JOIN sys.identity_columns AS idc ON idc.object_id = c.object_id
                                              AND idc.column_id = c.column_id
        LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id
                                                AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.computed_columns AS cmp ON cmp.object_id = c.object_id
                                              AND cmp.column_id = c.column_id
        WHERE tt.is_user_defined = 1
          AND (@oid IS NULL OR c.object_id = @oid)
        ORDER BY c.object_id, c.column_id;
        """;

    // One query for all three index-shaped members: the PRIMARY KEY, the
    // UNIQUE constraints and the inline INDEXes are all rows of sys.indexes on
    // the type table, told apart by is_primary_key / is_unique_constraint.
    // A heap type table has an index_id 0 row with no index_columns, and the
    // inner join drops it. INCLUDE columns ARE legal on a table type's inline
    // index (measured), so they are read rather than filtered out; a FILTERED
    // one is not — SQL Server rejects it — so filter_definition is not read.
    private const string KeysQuery = """
        SELECT
            i.object_id                          AS TypeTableObjectId,
            i.index_id                           AS IndexId,
            i.name                               AS IndexName,
            i.is_unique                          AS IsUnique,
            i.is_primary_key                     AS IsPrimaryKey,
            i.is_unique_constraint               AS IsUniqueConstraint,
            i.type_desc                          AS TypeDesc,
            kc.name                              AS ConstraintName,
            ISNULL(kc.is_system_named, 0)        AS IsSystemNamed,
            c.name                               AS ColumnName,
            ic.is_descending_key                 AS IsDescending,
            ic.is_included_column                AS IsIncluded
        FROM sys.table_types AS tt
        INNER JOIN sys.indexes AS i ON i.object_id = tt.type_table_object_id
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                           AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c ON c.object_id = ic.object_id
                                    AND c.column_id = ic.column_id
        LEFT JOIN sys.key_constraints AS kc ON kc.parent_object_id = i.object_id
                                            AND kc.unique_index_id = i.index_id
        WHERE tt.is_user_defined = 1
          AND (@oid IS NULL OR i.object_id = @oid)
        ORDER BY i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
        """;

    private const string ChecksQuery = """
        SELECT
            cc.parent_object_id                  AS TypeTableObjectId,
            cc.name                              AS ConstraintName,
            cc.definition                        AS Definition,
            cc.is_system_named                   AS IsSystemNamed
        FROM sys.table_types AS tt
        INNER JOIN sys.check_constraints AS cc ON cc.parent_object_id = tt.type_table_object_id
        WHERE tt.is_user_defined = 1
          AND (@oid IS NULL OR cc.parent_object_id = @oid)
        ORDER BY cc.parent_object_id, cc.name;
        """;

    /// <param name="connection"></param>

    /// <param name="ct"></param>    /// <param name="schema">
    /// When given with <paramref name="name"/>, reads only that one table type,
    /// and does the matching <b>on the server</b> so the database's own
    /// collation decides — the diff pane needs one type, and picking it in
    /// memory would both read the whole catalog and answer with the wrong type
    /// on a case-sensitive database holding <c>dbo.T</c> and <c>dbo.t</c>.
    /// </param>
    /// <param name="name"></param>
    public async Task<IReadOnlyList<TableTypeUdt>> ReadAsync(
        SqlConnection connection, CancellationToken ct, string? schema = null, string? name = null)
    {
        Dictionary<int, (string Schema, string Name, bool IsMemoryOptimized)> shells = [];
        await using (SqlCommand cmd = new(TypesQuery, connection))
        {
            cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
            await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                shells[r.GetInt32(2)] = (r.GetString(0), r.GetString(1), r.GetBoolean(3));
            }
        }

        if (shells.Count == 0) { return []; }

        // Narrows the three follow-up queries to the single type when the
        // caller asked for one; null means "every table type", as before.
        int? oid = schema is null ? null : shells.Keys.First();

        Dictionary<int, List<Column>> columns = await ReadColumnsAsync(connection, shells, oid, ct).ConfigureAwait(false);
        Dictionary<int, List<TableIndex>> keys = await ReadKeysAsync(connection, shells, oid, ct).ConfigureAwait(false);
        Dictionary<int, List<CheckConstraint>> checks = await ReadChecksAsync(connection, shells, oid, ct).ConfigureAwait(false);

        List<TableTypeUdt> result = new(shells.Count);
        foreach (KeyValuePair<int, (string Schema, string Name, bool IsMemoryOptimized)> kv in shells)
        {
            columns.TryGetValue(kv.Key, out List<Column>? cols);
            keys.TryGetValue(kv.Key, out List<TableIndex>? key);
            checks.TryGetValue(kv.Key, out List<CheckConstraint>? ck);
            result.Add(new TableTypeUdt(kv.Value.Schema, kv.Value.Name, cols ?? [])
            {
                Keys = key ?? [],
                CheckConstraints = ck ?? [],
                IsMemoryOptimized = kv.Value.IsMemoryOptimized,
            });
        }
        return result;
    }

    private static void AddOid(SqlCommand cmd, int? oid) =>
        cmd.Parameters.AddWithValue("@oid", (object?)oid ?? DBNull.Value);

    private static async Task<Dictionary<int, List<Column>>> ReadColumnsAsync(
        SqlConnection connection,
        Dictionary<int, (string Schema, string Name, bool IsMemoryOptimized)> shells,
        int? oid,
        CancellationToken ct)
    {
        Dictionary<int, List<Column>> columns = [];
        await using SqlCommand cmd = new(ColumnsQuery, connection);
        AddOid(cmd, oid);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            if (!shells.ContainsKey(objectId)) { continue; }

            if (!columns.TryGetValue(objectId, out List<Column>? list))
            {
                list = [];
                columns[objectId] = list;
            }

            list.Add(new Column(
                name: r.GetString(1),
                dataType: CatalogDataType.Format(r.GetString(2), r.GetInt16(3), r.GetByte(4), r.GetByte(5)),
                isNullable: r.GetBoolean(6),
                ordinal: r.GetInt32(7),
                defaultExpression: r.IsDBNull(13) ? null : r.GetString(13),
                isIdentity: r.GetBoolean(10),
                identitySeed: r.IsDBNull(11) ? null : r.GetInt64(11),
                identityIncrement: r.IsDBNull(12) ? null : r.GetInt64(12),
                computedExpression: r.IsDBNull(14) ? null : r.GetString(14),
                collation: r.IsDBNull(8) ? null : r.GetString(8))
            {
                IsUserDefinedType = r.GetBoolean(9),
                TypeSchema = r.IsDBNull(15) ? null : r.GetString(15),
            });
        }
        return columns;
    }

    private static async Task<Dictionary<int, List<TableIndex>>> ReadKeysAsync(
        SqlConnection connection,
        Dictionary<int, (string Schema, string Name, bool IsMemoryOptimized)> shells,
        int? oid,
        CancellationToken ct)
    {
        // (objectId, indexId) → the row's flags plus its columns, key columns
        // first because the query orders is_included_column ascending.
        Dictionary<(int ObjectId, int IndexId), IndexRow> rows = [];
        await using (SqlCommand cmd = new(KeysQuery, connection))
        {
            AddOid(cmd, oid);
            await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                int objectId = r.GetInt32(0);
                if (!shells.ContainsKey(objectId)) { continue; }

                (int, int) key = (objectId, r.GetInt32(1));
                if (!rows.TryGetValue(key, out IndexRow? row))
                {
                    row = new IndexRow(
                        IndexName: r.IsDBNull(2) ? null : r.GetString(2),
                        IsUnique: r.GetBoolean(3),
                        IsPrimaryKey: r.GetBoolean(4),
                        IsUniqueConstraint: r.GetBoolean(5),
                        IsClustered: r.GetString(6).StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase),
                        ConstraintName: r.IsDBNull(7) ? null : r.GetString(7),
                        KeyColumns: [],
                        IncludedColumns: []);
                    rows[key] = row;
                }

                if (r.GetBoolean(11)) { row.IncludedColumns.Add(r.GetString(9)); }
                else { row.KeyColumns.Add(new IndexColumn(r.GetString(9), r.GetBoolean(10))); }
            }
        }

        Dictionary<int, List<TableIndex>> result = [];
        foreach (KeyValuePair<(int ObjectId, int IndexId), IndexRow> kv in rows.OrderBy(kv => kv.Key.IndexId))
        {
            IndexRow row = kv.Value;
            if (!result.TryGetValue(kv.Key.ObjectId, out List<TableIndex>? list))
            {
                list = [];
                result[kv.Key.ObjectId] = list;
            }

            // A PK's and a UNIQUE's name is the server's — CREATE TYPE refuses a
            // CONSTRAINT clause — so it is carried for diagnostics only and
            // TableIndex.IsSystemNamed keeps it out of both the comparison and
            // the emitted text. An inline INDEX's name IS the user's.
            list.Add(new TableIndex(
                row.ConstraintName ?? row.IndexName ?? string.Empty,
                row.IsUnique,
                row.IsClustered,
                FilterExpression: null,
                KeyColumns: row.KeyColumns,
                IncludedColumns: row.IncludedColumns)
            {
                IsPrimaryKey = row.IsPrimaryKey,
                IsUniqueConstraint = row.IsUniqueConstraint,
            });
        }
        return result;
    }

    private static async Task<Dictionary<int, List<CheckConstraint>>> ReadChecksAsync(
        SqlConnection connection,
        Dictionary<int, (string Schema, string Name, bool IsMemoryOptimized)> shells,
        int? oid,
        CancellationToken ct)
    {
        Dictionary<int, List<CheckConstraint>> checks = [];
        await using SqlCommand cmd = new(ChecksQuery, connection);
        AddOid(cmd, oid);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            if (!shells.ContainsKey(objectId)) { continue; }

            if (!checks.TryGetValue(objectId, out List<CheckConstraint>? list))
            {
                list = [];
                checks[objectId] = list;
            }

            list.Add(new CheckConstraint(
                r.GetString(1),
                r.GetString(2),
                IsDisabled: false,
                IsNotForReplication: false)
            {
                IsSystemNamed = r.GetBoolean(3),
            });
        }
        return checks;
    }

    private sealed record IndexRow(
        string? IndexName,
        bool IsUnique,
        bool IsPrimaryKey,
        bool IsUniqueConstraint,
        bool IsClustered,
        string? ConstraintName,
        List<IndexColumn> KeyColumns,
        List<string> IncludedColumns);

}
