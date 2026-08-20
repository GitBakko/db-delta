using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads table-type user-defined types (sys.types where is_user_defined=1
/// AND is_table_type=1) plus the columns of their underlying type tables
/// (sys.columns JOIN type_table_object_id). Alias UDTs are read separately
/// by <see cref="UserDefinedTypeReader"/>.
/// </summary>
internal sealed class TableTypeUdtReader
{
    // sys.table_types is the canonical catalog view for user-defined table
    // types. It exposes type_table_object_id (the object_id of the hidden
    // type table that backs the column list) — sys.types lacks that column.
    private const string TypesQuery = """
        SELECT
            s.name                    AS SchemaName,
            tt.name                   AS TypeName,
            tt.type_table_object_id   AS TypeTableObjectId
        FROM sys.table_types AS tt
        INNER JOIN sys.schemas AS s ON s.schema_id = tt.schema_id
        WHERE tt.is_user_defined = 1
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
            ty.is_user_defined                   AS IsUserDefinedType
        FROM sys.columns AS c
        INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        INNER JOIN sys.table_types AS tt ON tt.type_table_object_id = c.object_id
        WHERE tt.is_user_defined = 1
        ORDER BY c.object_id, c.column_id;
        """;

    public async Task<IReadOnlyList<TableTypeUdt>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        Dictionary<int, (string Schema, string Name)> shells = [];
        await using (SqlCommand cmd = new(TypesQuery, connection))
        await using (SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                shells[r.GetInt32(2)] = (r.GetString(0), r.GetString(1));
            }
        }

        Dictionary<int, List<Column>> columns = [];
        await using (SqlCommand cmd = new(ColumnsQuery, connection))
        await using (SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
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
                    dataType: FormatDataType(r.GetString(2), r.GetInt16(3), r.GetByte(4), r.GetByte(5)),
                    isNullable: r.GetBoolean(6),
                    ordinal: r.GetInt32(7),
                    collation: r.IsDBNull(8) ? null : r.GetString(8))
                {
                    IsUserDefinedType = r.GetBoolean(9),
                });
            }
        }

        List<TableTypeUdt> result = new(shells.Count);
        foreach (KeyValuePair<int, (string Schema, string Name)> kv in shells)
        {
            columns.TryGetValue(kv.Key, out List<Column>? cols);
            result.Add(new TableTypeUdt(kv.Value.Schema, kv.Value.Name, cols ?? []));
        }
        return result;
    }

    private static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
    {
        return typeName switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{typeName}(max)" : $"{typeName}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1
                ? $"{typeName}(max)"
                : $"{typeName}({maxLength})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
            _ => typeName,
        };
    }
}
