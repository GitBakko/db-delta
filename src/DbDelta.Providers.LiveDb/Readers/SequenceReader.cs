using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads SEQUENCE objects from sys.sequences. Joins on sys.types so we get
/// the friendly type name (int, bigint, decimal …). Cache size is null when
/// the sequence uses the default; we surface that as <c>CacheSize=null</c>
/// and let the script emitter render <c>CACHE</c> without a number.
/// </summary>
internal sealed class SequenceReader
{
    private const string Sql = """
        SELECT
            s.name                         AS SchemaName,
            seq.name                       AS SeqName,
            TYPE_NAME(seq.user_type_id)    AS BaseType,
            CAST(seq.start_value AS bigint)     AS StartValue,
            CAST(seq.increment   AS bigint)     AS Increment,
            CAST(seq.minimum_value AS bigint)   AS MinValue,
            CAST(seq.maximum_value AS bigint)   AS MaxValue,
            seq.is_cycling                 AS IsCycling,
            seq.is_cached                  AS IsCached,
            seq.cache_size                 AS CacheSize
        FROM sys.sequences AS seq
        INNER JOIN sys.schemas AS s ON s.schema_id = seq.schema_id
        ORDER BY s.name, seq.name;
        """;

    public async Task<IReadOnlyList<Sequence>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        List<Sequence> result = [];
        await using SqlCommand cmd = new(Sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new Sequence(
                Schema: r.GetString(0),
                Name: r.GetString(1),
                DataType: r.GetString(2),
                StartValue: r.GetInt64(3),
                Increment: r.GetInt64(4),
                MinValue: r.IsDBNull(5) ? null : r.GetInt64(5),
                MaxValue: r.IsDBNull(6) ? null : r.GetInt64(6),
                IsCycling: r.GetBoolean(7),
                IsCached: r.GetBoolean(8),
                CacheSize: r.IsDBNull(9) ? null : r.GetInt32(9)));
        }
        return result;
    }
}
