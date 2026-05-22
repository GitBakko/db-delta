using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads SYNONYM objects from sys.synonyms. SQL Server stores the synonym
/// target as a single string (<c>base_object_name</c>) of the form
/// <c>[srv].[db].[schema].[obj]</c>; we keep the raw string for emission
/// and split it best-effort into segments for the diff viewer.
/// </summary>
internal sealed class SynonymReader
{
    private const string Sql = """
        SELECT s.name AS SchemaName, syn.name AS SynName, syn.base_object_name
        FROM sys.synonyms AS syn
        INNER JOIN sys.schemas AS s ON s.schema_id = syn.schema_id
        ORDER BY s.name, syn.name;
        """;

    public async Task<IReadOnlyList<Synonym>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        List<Synonym> result = [];
        await using SqlCommand cmd = new(Sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schemaName = r.GetString(0);
            string synName = r.GetString(1);
            string baseRaw = r.GetString(2);
            (string? srv, string? db, string? sch, string? obj) = SplitBaseObjectName(baseRaw);
            result.Add(new Synonym(schemaName, synName, baseRaw, srv, db, sch, obj));
        }
        return result;
    }

    /// <summary>
    /// Splits SQL Server's <c>base_object_name</c> form into up to four
    /// bracketed segments. Missing leading segments are returned as null —
    /// e.g. <c>[dbo].[Orders]</c> yields (null, null, "dbo", "Orders");
    /// <c>[other_db].[dbo].[Orders]</c> yields (null, "other_db", "dbo", "Orders").
    /// </summary>
    private static (string? Server, string? Database, string? Schema, string? Object) SplitBaseObjectName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return (null, null, null, null); }
        List<string> parts = [];
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '[')
            {
                int end = raw.IndexOf(']', i + 1);
                if (end < 0) { break; }
                parts.Add(raw[(i + 1)..end]);
                i = end + 1;
                if (i < raw.Length && raw[i] == '.') { i++; }
            }
            else { i++; }
        }

        return parts.Count switch
        {
            0 => (null, null, null, null),
            1 => (null, null, null, parts[0]),
            2 => (null, null, parts[0], parts[1]),
            3 => (null, parts[0], parts[1], parts[2]),
            _ => (parts[0], parts[1], parts[2], parts[3]),
        };
    }
}
