using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads non-PK / non-UQ indexes (those are modeled as constraints). Picks up
/// clustered/non-clustered key indexes, unique indexes, filtered indexes, and
/// included columns.
/// </summary>
/// <remarks>
/// Every index TYPE comes back, not just rowstore. The filter used to be
/// <c>AND i.type IN (1, 2)</c>, and the cost was not a missing feature but
/// silent destruction: a columnstore index was absent from BOTH models, so two
/// databases differing only by one compared Identical, and an identity-change
/// rebuild — CREATE <c>_tmp</c>, copy, DROP the original — dropped the index
/// with the table and nothing put it back, under a green banner.
/// The emitter still cannot WRITE the non-rowstore types; carrying
/// <see cref="TableIndex.TypeDesc"/> is what lets it refuse instead.
/// </remarks>
internal sealed class IndexReader
{
    private const string IndexesQuery = """
        SELECT
            i.object_id          AS ObjectId,
            i.name               AS IndexName,
            i.is_unique          AS IsUnique,
            i.type               AS IndexType,
            i.has_filter         AS HasFilter,
            i.filter_definition  AS FilterDefinition,
            ic.index_id          AS IndexId,
            ic.key_ordinal       AS KeyOrdinal,
            ic.is_descending_key AS IsDescending,
            ic.is_included_column AS IsIncluded,
            c.name               AS ColumnName,
            -- First partition only: DbDelta does not model per-partition
            -- compression, so an unevenly compressed index is scripted as its
            -- first partition.
            (SELECT TOP 1 p.data_compression_desc
             FROM sys.partitions AS p
             WHERE p.object_id = i.object_id AND p.index_id = i.index_id
             ORDER BY p.partition_number) AS DataCompression,
            i.type_desc          AS IndexTypeDesc
        FROM sys.indexes AS i
        INNER JOIN sys.tables AS t ON t.object_id = i.object_id
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                           AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c ON c.object_id = ic.object_id
                                    AND c.column_id = ic.column_id
        WHERE t.is_ms_shipped = 0
          AND i.is_primary_key = 0
          AND i.is_unique_constraint = 0
          -- No type filter. The only shape excluded here is the heap (type 0),
          -- which has no name and is not an index: the table's own rows are
          -- modelled by Table.DataCompression instead.
          AND i.name IS NOT NULL
        -- index_column_id is the tiebreak that makes INCLUDE order deterministic:
        -- key_ordinal is 1..n for key columns but ZERO for every included one, so
        -- ordering by it alone left the INCLUDE list in whatever order the engine
        -- felt like returning. IndexesEqual compares that list as a SEQUENCE, so
        -- two reads of one unchanged index could disagree and report a rebuild of
        -- an index nobody touched.
        ORDER BY i.object_id, i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
        """;

    public async Task<IReadOnlyDictionary<int, List<TableIndex>>> ReadAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        Dictionary<int, List<TableIndex>> byObject = [];

        int? currentObjectId = null;
        int? currentIndexId = null;
        string? currentName = null;
        bool isUnique = false;
        bool isClustered = false;
        string? filter = null;
        string? currentCompression = null;
        string? currentTypeDesc = null;
        List<IndexColumn> keys = [];
        List<string> included = [];

        await using SqlCommand cmd = new(IndexesQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            string name = r.GetString(1);
            bool isUq = r.GetBoolean(2);
            byte indexType = r.GetByte(3);
            bool hasFilter = r.GetBoolean(4);
            string? filterDef = r.IsDBNull(5) ? null : r.GetString(5);
            int indexId = r.GetInt32(6);
            bool isDesc = r.GetBoolean(8);
            bool isIncl = r.GetBoolean(9);
            string column = r.GetString(10);
            string? compression = r.IsDBNull(11) ? null : r.GetString(11);
            string? typeDesc = r.IsDBNull(12) ? null : r.GetString(12);

            if (currentIndexId is not null && (currentIndexId != indexId || currentObjectId != objectId))
            {
                Flush(byObject, currentObjectId!.Value, currentName!, isUnique, isClustered, filter, currentCompression, currentTypeDesc, keys, included);
                keys = [];
                included = [];
            }

            currentObjectId = objectId;
            currentIndexId = indexId;
            currentName = name;
            isUnique = isUq;
            isClustered = indexType == 1;
            filter = hasFilter ? filterDef : null;
            currentCompression = compression;
            currentTypeDesc = typeDesc;

            if (isIncl)
            {
                included.Add(column);
            }
            else
            {
                keys.Add(new IndexColumn(column, isDesc));
            }
        }

        if (currentIndexId is not null)
        {
            Flush(byObject, currentObjectId!.Value, currentName!, isUnique, isClustered, filter, currentCompression, currentTypeDesc, keys, included);
        }

        return byObject;
    }

    private static void Flush(
        Dictionary<int, List<TableIndex>> byObject,
        int objectId,
        string name,
        bool isUnique,
        bool isClustered,
        string? filter,
        string? compression,
        string? typeDesc,
        List<IndexColumn> keys,
        List<string> included)
    {
        TableIndex ix = new(
            Name: name,
            IsUnique: isUnique,
            IsClustered: isClustered,
            FilterExpression: filter,
            KeyColumns: [.. keys],
            IncludedColumns: [.. included],
            DataCompression: compression,
            TypeDesc: typeDesc);
        if (!byObject.TryGetValue(objectId, out List<TableIndex>? list))
        {
            list = [];
            byObject[objectId] = list;
        }
        list.Add(ix);
    }
}
