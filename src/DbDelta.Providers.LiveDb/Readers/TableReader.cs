using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads user tables and their columns in a single round-trip per server.
/// </summary>
internal sealed class TableReader
{
    private const string TablesQuery = """
        SELECT
            s.name      AS SchemaName,
            t.name      AS TableName,
            t.object_id AS ObjectId
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name;
        """;

    private const string ColumnsQuery = """
        SELECT
            c.object_id        AS ObjectId,
            c.name             AS ColumnName,
            TYPE_NAME(c.user_type_id) AS TypeName,
            c.max_length       AS MaxLength,
            c.precision        AS Precision,
            c.scale            AS Scale,
            c.is_nullable      AS IsNullable,
            c.is_identity      AS IsIdentity,
            dc.definition      AS DefaultExpression,
            c.column_id        AS Ordinal
        FROM sys.columns AS c
        INNER JOIN sys.tables AS t ON t.object_id = c.object_id
        LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id
                                              AND dc.parent_column_id = c.column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;
        """;

    public async Task<IReadOnlyList<Table>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        // 1. Build a map of objectId -> (schema, name)
        Dictionary<int, (string Schema, string Name)> tableShells = [];
        await using (SqlCommand tablesCmd = new SqlCommand(TablesQuery, connection))
        await using (SqlDataReader tablesReader = await tablesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await tablesReader.ReadAsync(ct).ConfigureAwait(false))
            {
                string schemaName = tablesReader.GetString(0);
                string tableName = tablesReader.GetString(1);
                int objectId = tablesReader.GetInt32(2);
                tableShells[objectId] = (schemaName, tableName);
            }
        }

        // 2. Fetch all columns and group by object id
        Dictionary<int, List<Column>> columnsByObjectId = [];
        await using (SqlCommand columnsCmd = new SqlCommand(ColumnsQuery, connection))
        await using (SqlDataReader columnsReader = await columnsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await columnsReader.ReadAsync(ct).ConfigureAwait(false))
            {
                int objectId = columnsReader.GetInt32(0);
                string columnName = columnsReader.GetString(1);
                string typeName = columnsReader.GetString(2);
                short maxLength = columnsReader.GetInt16(3);
                byte precision = columnsReader.GetByte(4);
                byte scale = columnsReader.GetByte(5);
                bool isNullable = columnsReader.GetBoolean(6);
                bool isIdentity = columnsReader.GetBoolean(7);
                string? defaultExpr = columnsReader.IsDBNull(8) ? null : columnsReader.GetString(8);
                int ordinal = columnsReader.GetInt32(9);

                if (!tableShells.ContainsKey(objectId))
                {
                    continue;
                }

                if (!columnsByObjectId.TryGetValue(objectId, out List<Column>? list))
                {
                    list = [];
                    columnsByObjectId[objectId] = list;
                }
                list.Add(new Column(
                    name: columnName,
                    dataType: FormatDataType(typeName, maxLength, precision, scale),
                    isNullable: isNullable,
                    ordinal: ordinal,
                    defaultExpression: defaultExpr,
                    isIdentity: isIdentity));
            }
        }

        // 3. Build the table list
        List<Table> tables = new List<Table>(tableShells.Count);
        foreach (KeyValuePair<int, (string Schema, string Name)> kv in tableShells)
        {
            columnsByObjectId.TryGetValue(kv.Key, out List<Column>? cols);
            tables.Add(new Table(kv.Value.Schema, kv.Value.Name, cols ?? []));
        }
        return tables;
    }

    private static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
    {
        return typeName switch
        {
            "nvarchar" or "nchar" => maxLength == -1
                ? $"{typeName}(max)"
                : $"{typeName}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1
                ? $"{typeName}(max)"
                : $"{typeName}({maxLength})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
            _ => typeName,
        };
    }
}
