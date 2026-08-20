using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads SYNONYM objects from sys.synonyms. SQL Server stores the synonym
/// target as a single string (<c>base_object_name</c>) of the form
/// <c>[srv].[db].[schema].[obj]</c>, which we keep verbatim: it is what the
/// emitter writes and what the comparison reads.
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
            result.Add(new Synonym(schemaName, synName, baseRaw));
        }
        return result;
    }
}
