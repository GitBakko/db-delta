using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads user tables and their columns (including identity seed/increment +
/// persisted-computed expressions) in a small number of round-trips.
/// </summary>
internal sealed class TableReader
{
    private const string TablesQuery = """
        SELECT
            s.name        AS SchemaName,
            t.name        AS TableName,
            t.object_id   AS ObjectId,
            t.modify_date AS ModifyDate
        FROM sys.tables AS t
        INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name;
        """;

    private const string ColumnsQuery = """
        SELECT
            c.object_id            AS ObjectId,
            c.name                 AS ColumnName,
            TYPE_NAME(c.user_type_id) AS TypeName,
            c.max_length           AS MaxLength,
            c.precision            AS [Precision],
            c.scale                AS Scale,
            c.is_nullable          AS IsNullable,
            c.is_identity          AS IsIdentity,
            CAST(ic.seed_value AS bigint)      AS IdentitySeed,
            CAST(ic.increment_value AS bigint) AS IdentityIncrement,
            dc.definition          AS DefaultExpression,
            cc.definition          AS ComputedExpression,
            ISNULL(cc.is_persisted, 0)         AS IsPersistedComputed,
            c.column_id            AS Ordinal
        FROM sys.columns AS c
        INNER JOIN sys.tables AS t ON t.object_id = c.object_id
        LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id
                                             AND ic.column_id = c.column_id
        LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id
                                                AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id
                                              AND cc.column_id = c.column_id
        WHERE t.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;
        """;

    public async Task<IReadOnlyList<Table>> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        Dictionary<int, (string Schema, string Name, DateTime? ModifyDate)> tableShells = [];
        await using (var tablesCmd = new SqlCommand(TablesQuery, connection))
        await using (SqlDataReader tablesReader = await tablesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await tablesReader.ReadAsync(ct).ConfigureAwait(false))
            {
                string schemaName = tablesReader.GetString(0);
                string tableName = tablesReader.GetString(1);
                int objectId = tablesReader.GetInt32(2);
                // sys.tables.modify_date is server local time; treat as UTC for
                // display consistency with sys.sql_modules pipeline (same caveat).
                DateTime? modifyDate = tablesReader.IsDBNull(3)
                    ? null
                    : DateTime.SpecifyKind(tablesReader.GetDateTime(3), DateTimeKind.Utc);
                tableShells[objectId] = (schemaName, tableName, modifyDate);
            }
        }

        Dictionary<int, List<Column>> columnsByObjectId = [];
        await using (var columnsCmd = new SqlCommand(ColumnsQuery, connection))
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
                long? identitySeed = columnsReader.IsDBNull(8) ? null : columnsReader.GetInt64(8);
                long? identityIncrement = columnsReader.IsDBNull(9) ? null : columnsReader.GetInt64(9);
                string? defaultExpr = columnsReader.IsDBNull(10) ? null : columnsReader.GetString(10);
                string? computedExpr = columnsReader.IsDBNull(11) ? null : columnsReader.GetString(11);
                bool isPersistedComputed = !columnsReader.IsDBNull(12) && columnsReader.GetBoolean(12);
                int ordinal = columnsReader.GetInt32(13);

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
                    isIdentity: isIdentity,
                    identitySeed: identitySeed,
                    identityIncrement: identityIncrement,
                    computedExpression: computedExpr,
                    isPersistedComputed: isPersistedComputed));
            }
        }

        var tables = new List<Table>(tableShells.Count);
        foreach (KeyValuePair<int, (string Schema, string Name, DateTime? ModifyDate)> kv in tableShells)
        {
            columnsByObjectId.TryGetValue(kv.Key, out List<Column>? cols);
            tables.Add(new Table(
                Schema: kv.Value.Schema,
                Name: kv.Value.Name,
                Columns: cols ?? [],
                Constraints: [],
                Indexes: [],
                ModifyDate: kv.Value.ModifyDate));
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
