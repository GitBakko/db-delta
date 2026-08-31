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
            seq.cache_size                 AS CacheSize,
            -- A sequence over an ALIAS type is legal and TYPE_NAME hands back the
            -- bare name, same as for a column. Null for a built-in base type.
            CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) END AS TypeSchema
        FROM sys.sequences AS seq
        INNER JOIN sys.schemas AS s ON s.schema_id = seq.schema_id
        -- LEFT, never INNER, and it is defence in depth rather than a live
        -- fix: sys.* is filtered by metadata visibility, so an INNER join to
        -- sys.types would DROP the sequence for a principal that cannot see
        -- its type — and a dropped row reads as OnlyInB on the other side,
        -- which the script turns into DROP SEQUENCE. Unreachable today
        -- because LiveDbSource refuses to read at all without VIEW DEFINITION
        -- at DATABASE scope, which lifts the filter for sys.types too. LEFT
        -- costs nothing and keeps this reader from being the thing that loses
        -- an object if that guard is ever relaxed.
        LEFT JOIN sys.types AS ty ON ty.user_type_id = seq.user_type_id
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
                CacheSize: r.IsDBNull(9) ? null : r.GetInt32(9))
            {
                TypeSchema = r.IsDBNull(10) ? null : r.GetString(10),
            });
        }
        return result;
    }
}
