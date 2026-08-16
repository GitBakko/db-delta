using DbDelta.Core.ObjectModel;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.Readers;

/// <summary>
/// Reads PK / UQ / FK / CK / DEFAULT constraints in a small number of batched
/// queries against <c>sys.*</c> catalog views, keyed by parent
/// <c>sys.tables.object_id</c>.
/// </summary>
internal sealed class ConstraintReader
{
    private const string KeysQuery = """
        SELECT
            kc.parent_object_id AS ObjectId,
            kc.name             AS ConstraintName,
            kc.type             AS ConstraintType,
            i.type              AS IndexType,
            ic.key_ordinal      AS KeyOrdinal,
            c.name              AS ColumnName,
            kc.is_system_named  AS IsSystemNamed
        FROM sys.key_constraints AS kc
        INNER JOIN sys.indexes AS i ON i.object_id = kc.parent_object_id
                                    AND i.index_id = kc.unique_index_id
        INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                           AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c ON c.object_id = ic.object_id
                                    AND c.column_id = ic.column_id
        INNER JOIN sys.tables AS t ON t.object_id = kc.parent_object_id
        WHERE t.is_ms_shipped = 0
          AND ic.is_included_column = 0
        ORDER BY kc.parent_object_id, kc.name, ic.key_ordinal;
        """;

    private const string ForeignKeysQuery = """
        SELECT
            fk.parent_object_id    AS ObjectId,
            fk.name                AS ConstraintName,
            cp.name                AS LocalColumn,
            sr.name                AS RefSchema,
            tr.name                AS RefTable,
            cr.name                AS RefColumn,
            fkc.constraint_column_id AS Ordinal,
            fk.delete_referential_action AS OnDelete,
            fk.update_referential_action AS OnUpdate,
            fk.is_disabled         AS IsDisabled,
            fk.is_not_for_replication AS IsNotForReplication
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        INNER JOIN sys.tables AS tp ON tp.object_id = fk.parent_object_id
        INNER JOIN sys.tables AS tr ON tr.object_id = fk.referenced_object_id
        INNER JOIN sys.schemas AS sr ON sr.schema_id = tr.schema_id
        INNER JOIN sys.columns AS cp ON cp.object_id = fkc.parent_object_id
                                     AND cp.column_id = fkc.parent_column_id
        INNER JOIN sys.columns AS cr ON cr.object_id = fkc.referenced_object_id
                                     AND cr.column_id = fkc.referenced_column_id
        WHERE tp.is_ms_shipped = 0
        ORDER BY fk.parent_object_id, fk.name, fkc.constraint_column_id;
        """;

    private const string ChecksQuery = """
        SELECT
            cc.parent_object_id    AS ObjectId,
            cc.name                AS ConstraintName,
            cc.definition          AS Expression,
            cc.is_disabled         AS IsDisabled,
            cc.is_not_for_replication AS IsNotForReplication,
            cc.is_system_named     AS IsSystemNamed
        FROM sys.check_constraints AS cc
        INNER JOIN sys.tables AS t ON t.object_id = cc.parent_object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY cc.parent_object_id, cc.name;
        """;

    private const string DefaultsQuery = """
        SELECT
            dc.parent_object_id    AS ObjectId,
            dc.name                AS ConstraintName,
            c.name                 AS ColumnName,
            dc.definition          AS Expression,
            dc.is_system_named     AS IsSystemNamed
        FROM sys.default_constraints AS dc
        INNER JOIN sys.columns AS c ON c.object_id = dc.parent_object_id
                                    AND c.column_id = dc.parent_column_id
        INNER JOIN sys.tables AS t ON t.object_id = dc.parent_object_id
        WHERE t.is_ms_shipped = 0
        ORDER BY dc.parent_object_id, dc.name;
        """;

    public async Task<IReadOnlyDictionary<int, List<Constraint>>> ReadAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        Dictionary<int, List<Constraint>> byObject = [];

        await ReadKeyConstraintsAsync(connection, byObject, ct).ConfigureAwait(false);
        await ReadForeignKeysAsync(connection, byObject, ct).ConfigureAwait(false);
        await ReadChecksAsync(connection, byObject, ct).ConfigureAwait(false);
        await ReadDefaultsAsync(connection, byObject, ct).ConfigureAwait(false);

        return byObject;
    }

    private static async Task ReadKeyConstraintsAsync(
        SqlConnection connection,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        int? currentObjectId = null;
        string? currentName = null;
        string? currentType = null;
        bool isClustered = false;
        bool isSystemNamed = false;
        List<string> currentCols = [];

        await using SqlCommand cmd = new(KeysQuery, connection);
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = reader.GetInt32(0);
            string name = reader.GetString(1);
            string type = reader.GetString(2).Trim();
            byte indexType = reader.GetByte(3);
            string column = reader.GetString(5);
            bool systemNamed = reader.GetBoolean(6);

            if (currentName is not null && (currentName != name || currentObjectId != objectId))
            {
                FlushKey(byObject, currentObjectId!.Value, currentName, currentType!, isClustered, isSystemNamed, currentCols);
                currentCols = [];
            }

            currentObjectId = objectId;
            currentName = name;
            currentType = type;
            isClustered = indexType == 1;
            isSystemNamed = systemNamed;
            currentCols.Add(column);
        }

        if (currentName is not null)
        {
            FlushKey(byObject, currentObjectId!.Value, currentName, currentType!, isClustered, isSystemNamed, currentCols);
        }
    }

    private static void FlushKey(
        Dictionary<int, List<Constraint>> byObject,
        int objectId,
        string name,
        string type,
        bool isClustered,
        bool isSystemNamed,
        List<string> columns)
    {
        Constraint c = type switch
        {
            "PK" => new PrimaryKey(name, [.. columns], isClustered) { IsSystemNamed = isSystemNamed },
            "UQ" => new UniqueConstraint(name, [.. columns], isClustered) { IsSystemNamed = isSystemNamed },
            _ => throw new InvalidOperationException($"Unexpected key constraint type '{type}'."),
        };
        Append(byObject, objectId, c);
    }

    private static async Task ReadForeignKeysAsync(
        SqlConnection connection,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        int? currentObjectId = null;
        string? currentName = null;
        string? refSchema = null;
        string? refTable = null;
        ReferentialAction onDelete = ReferentialAction.NoAction;
        ReferentialAction onUpdate = ReferentialAction.NoAction;
        bool isDisabled = false;
        bool isNfr = false;
        List<string> localCols = [];
        List<string> refCols = [];

        await using SqlCommand cmd = new(ForeignKeysQuery, connection);
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = reader.GetInt32(0);
            string name = reader.GetString(1);
            string localCol = reader.GetString(2);
            string rs = reader.GetString(3);
            string rt = reader.GetString(4);
            string rc = reader.GetString(5);
            byte onDel = reader.GetByte(7);
            byte onUpd = reader.GetByte(8);
            bool disabled = reader.GetBoolean(9);
            bool nfr = reader.GetBoolean(10);

            if (currentName is not null && (currentName != name || currentObjectId != objectId))
            {
                FlushForeignKey(byObject, currentObjectId!.Value, currentName,
                    localCols, refSchema!, refTable!, refCols,
                    onDelete, onUpdate, isDisabled, isNfr);
                localCols = [];
                refCols = [];
            }

            currentObjectId = objectId;
            currentName = name;
            refSchema = rs;
            refTable = rt;
            onDelete = MapAction(onDel);
            onUpdate = MapAction(onUpd);
            isDisabled = disabled;
            isNfr = nfr;
            localCols.Add(localCol);
            refCols.Add(rc);
        }

        if (currentName is not null)
        {
            FlushForeignKey(byObject, currentObjectId!.Value, currentName,
                localCols, refSchema!, refTable!, refCols,
                onDelete, onUpdate, isDisabled, isNfr);
        }
    }

    private static void FlushForeignKey(
        Dictionary<int, List<Constraint>> byObject,
        int objectId,
        string name,
        List<string> localCols,
        string refSchema,
        string refTable,
        List<string> refCols,
        ReferentialAction onDelete,
        ReferentialAction onUpdate,
        bool isDisabled,
        bool isNotForReplication)
    {
        ForeignKey fk = new(
            Name: name,
            Columns: [.. localCols],
            ReferencedSchema: refSchema,
            ReferencedTable: refTable,
            ReferencedColumns: [.. refCols],
            OnDelete: onDelete,
            OnUpdate: onUpdate,
            IsDisabled: isDisabled,
            IsNotForReplication: isNotForReplication);
        Append(byObject, objectId, fk);
    }

    private static async Task ReadChecksAsync(
        SqlConnection connection,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        await using SqlCommand cmd = new(ChecksQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            CheckConstraint ck = new(
                Name: r.GetString(1),
                Expression: r.GetString(2),
                IsDisabled: r.GetBoolean(3),
                IsNotForReplication: r.GetBoolean(4))
            { IsSystemNamed = r.GetBoolean(5) };
            Append(byObject, objectId, ck);
        }
    }

    private static async Task ReadDefaultsAsync(
        SqlConnection connection,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        await using SqlCommand cmd = new(DefaultsQuery, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            int objectId = r.GetInt32(0);
            DefaultConstraint df = new(
                Name: r.GetString(1),
                ColumnName: r.GetString(2),
                Expression: r.GetString(3))
            { IsSystemNamed = r.GetBoolean(4) };
            Append(byObject, objectId, df);
        }
    }

    private static void Append(Dictionary<int, List<Constraint>> byObject, int objectId, Constraint c)
    {
        if (!byObject.TryGetValue(objectId, out List<Constraint>? list))
        {
            list = [];
            byObject[objectId] = list;
        }
        list.Add(c);
    }

    private static ReferentialAction MapAction(byte b) => b switch
    {
        0 => ReferentialAction.NoAction,
        1 => ReferentialAction.Cascade,
        2 => ReferentialAction.SetNull,
        3 => ReferentialAction.SetDefault,
        _ => ReferentialAction.NoAction,
    };
}
