using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Counts what the other readers do NOT read, so the comparison can disclose its
/// own scope instead of implying it looked at everything.
/// </summary>
/// <remarks>
/// <para>
/// One round trip. Every branch is a <c>COUNT(*)</c> over a catalog view, and
/// zero-count families are dropped server-side, so a database holding nothing
/// unusual produces an empty census and no message anywhere.
/// </para>
/// <para>
/// The first branch is deliberately a NOT IN over the modelled
/// <c>sys.objects</c> types rather than a list of the unmodelled ones: a SQL
/// Server version that adds an object type should make this louder, not leave a
/// new blind spot that nothing reports. Unknown <c>type_desc</c> values are
/// labelled from the catalog value itself by <see cref="UnexaminedCensus"/>.
/// </para>
/// </remarks>
internal sealed class UnexaminedReader
{
    private const string Query = """
        SELECT Label, Cnt FROM (
            -- Object types outside the thirteen modelled kinds. The excluded list
            -- IS the modelled set: U/V/P/FN/IF/TF/TR/SO/SN/TT plus the constraint
            -- types, which are modelled as parts of their table.
            SELECT o.type_desc COLLATE DATABASE_DEFAULT AS Label, COUNT(*) AS Cnt
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0
              AND o.type NOT IN ('U','V','P','FN','IF','TF','TR','SO','SN','TT','C','D','F','PK','UQ')
            GROUP BY o.type_desc

            -- Catalogs that are not sys.objects rows at all.
            UNION ALL SELECT 'ASSEMBLY', COUNT(*) FROM sys.assemblies WHERE is_user_defined = 1
            UNION ALL SELECT 'PARTITION_FUNCTION', COUNT(*) FROM sys.partition_functions
            UNION ALL SELECT 'PARTITION_SCHEME', COUNT(*) FROM sys.partition_schemes
            UNION ALL SELECT 'XML_SCHEMA_COLLECTION', COUNT(*) FROM sys.xml_schema_collections
                      WHERE schema_id <> SCHEMA_ID('sys')
            UNION ALL SELECT 'FULLTEXT_CATALOG', COUNT(*) FROM sys.fulltext_catalogs

            -- IndexReader now carries every index type, so a missing columnstore
            -- IS reported as a difference. What it still cannot do is WRITE one:
            -- the type-specific options (column order, archive compression,
            -- partitioning) are neither read nor compared, and no CREATE is ever
            -- emitted. The count stays, narrowed to that. Indexes on indexed
            -- views remain invisible outright — IndexReader joins sys.tables.
            UNION ALL SELECT 'INDEX_NON_ROWSTORE', COUNT(*)
                      FROM sys.indexes AS i
                      INNER JOIN sys.objects AS o ON o.object_id = i.object_id
                      WHERE o.is_ms_shipped = 0 AND i.type NOT IN (0, 1, 2)
            -- INDEX_ON_VIEW is gone from this census as of 2026-08-20: those
            -- indexes are read, compared and emitted like any other. A census
            -- that keeps declaring a blind spot after it closes is as wrong as
            -- one that never declared it.

            -- Read as tables, but these attributes of them are not compared.
            UNION ALL SELECT 'TEMPORAL_TABLE', COUNT(*) FROM sys.tables WHERE temporal_type <> 0
            UNION ALL SELECT 'MEMORY_OPTIMIZED_TABLE', COUNT(*) FROM sys.tables WHERE is_memory_optimized = 1
            UNION ALL SELECT 'MASKED_COLUMN', COUNT(*) FROM sys.masked_columns

            -- ModuleReader takes parent_class = 1 (DML triggers) only.
            UNION ALL SELECT 'DDL_TRIGGER_DATABASE', COUNT(*) FROM sys.triggers WHERE parent_class = 0

            UNION ALL SELECT 'EXTENDED_PROPERTY', COUNT(*) FROM sys.extended_properties
        ) AS census
        WHERE Cnt > 0
        ORDER BY Cnt DESC, Label;
        """;

    public async Task<UnexaminedCensus> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        List<UnexaminedGroup> groups = [];
        await using var command = new SqlCommand(Query, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            groups.Add(new UnexaminedGroup(reader.GetString(0), reader.GetInt32(1)));
        }
        return groups.Count == 0 ? UnexaminedCensus.Empty : new UnexaminedCensus(groups);
    }
}
