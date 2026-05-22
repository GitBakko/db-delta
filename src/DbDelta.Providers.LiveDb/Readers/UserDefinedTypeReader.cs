using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads alias user-defined types from sys.types.  Filters out CLR UDTs
/// (<c>is_assembly_type=1</c>) since v1 does not deploy CLR assemblies.
/// </summary>
internal sealed class UserDefinedTypeReader
{
    private const string Sql = """
        SELECT
            s.name                              AS SchemaName,
            t.name                              AS TypeName,
            TYPE_NAME(t.system_type_id)         AS BaseType,
            t.max_length                        AS MaxLength,
            t.precision                         AS [Precision],
            t.scale                             AS Scale,
            t.is_nullable                       AS IsNullable
        FROM sys.types AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.is_user_defined = 1
          AND t.is_assembly_type = 0
          AND t.is_table_type = 0
        ORDER BY s.name, t.name;
        """;

    public async Task<IReadOnlyList<UserDefinedType>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        List<UserDefinedType> result = [];
        await using SqlCommand cmd = new(Sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new UserDefinedType(
                Schema: r.GetString(0),
                Name: r.GetString(1),
                BaseTypeName: r.GetString(2),
                MaxLength: r.GetInt16(3),
                Precision: r.GetByte(4),
                Scale: r.GetByte(5),
                IsNullable: r.GetBoolean(6)));
        }
        return result;
    }
}
