using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads non-system schemas from <c>sys.schemas</c>.
/// </summary>
internal sealed class SchemaReader
{
    private const string Query = """
        SELECT s.name AS SchemaName
        FROM sys.schemas s
        WHERE s.principal_id != 4 -- exclude sys
          AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
          AND s.name NOT LIKE 'db[_]%'
        ORDER BY s.name;
        """;

    public async Task<IReadOnlyList<Schema>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        List<Schema> schemas = [];
        await using SqlCommand command = new SqlCommand(Query, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            schemas.Add(new Schema(reader.GetString(0)));
        }
        return schemas;
    }
}
