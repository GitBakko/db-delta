using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb.ObjectBody;

/// <summary>
/// Resolves the SQL body for a single object from two live SQL Server databases.
/// Modules (View/Procedure/Function/Trigger) return the raw <c>sys.sql_modules.definition</c>.
/// Tables return a synthesised CREATE TABLE script via <see cref="TableScriptEmitter.GenerateCreateTable(Table)"/>.
/// </summary>
public sealed class LiveDbObjectBodyResolver(string sourceConnectionString, string targetConnectionString) : IObjectBodyResolver
{
    private readonly string _sourceConnectionString = sourceConnectionString ?? throw new ArgumentNullException(nameof(sourceConnectionString));
    private readonly string _targetConnectionString = targetConnectionString ?? throw new ArgumentNullException(nameof(targetConnectionString));

    /// <inheritdoc/>
    public Task<string?> ResolveSourceBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct) =>
        ResolveBodyAsync(_sourceConnectionString, kind, schemaName, objectName, ct);

    /// <inheritdoc/>
    public Task<string?> ResolveTargetBodyAsync(string kind, string schemaName, string objectName, CancellationToken ct) =>
        ResolveBodyAsync(_targetConnectionString, kind, schemaName, objectName, ct);

    private static async Task<string?> ResolveBodyAsync(
        string connectionString,
        string kind,
        string schemaName,
        string objectName,
        CancellationToken ct)
    {
        await using SqlConnection connection = await ConnectionFactory.OpenAsync(connectionString, ct).ConfigureAwait(false);

        return kind switch
        {
            "Table" => await ResolveTableBodyAsync(connection, schemaName, objectName, ct).ConfigureAwait(false),
            "View" or "Procedure" or "Function" or "Trigger" =>
                await ResolveModuleBodyAsync(connection, schemaName, objectName, ct).ConfigureAwait(false),
            "Sequence" => await ResolveSequenceBodyAsync(connection, schemaName, objectName, ct).ConfigureAwait(false),
            "Synonym" => await ResolveSynonymBodyAsync(connection, schemaName, objectName, ct).ConfigureAwait(false),
            "UserDefinedType" => await ResolveUserDefinedTypeBodyAsync(connection, schemaName, objectName, ct).ConfigureAwait(false),
            "User" => await ResolveUserBodyAsync(connection, objectName, ct).ConfigureAwait(false),
            "Role" => await ResolveRoleBodyAsync(connection, objectName, ct).ConfigureAwait(false),
            "Permission" => Task.FromResult<string?>(null).Result, // permissions render as identity-only rows
            _ => await ResolveModuleBodyAsync(connection, schemaName, objectName, ct).ConfigureAwait(false),
        };
    }

    // ── M5 body resolvers ──────────────────────────────────────────────────

    private static async Task<string?> ResolveSequenceBodyAsync(
        SqlConnection connection, string schema, string name, CancellationToken ct)
    {
        const string sql = """
            SELECT seq.name, TYPE_NAME(seq.user_type_id),
                   CAST(seq.start_value AS bigint), CAST(seq.increment AS bigint),
                   CAST(seq.minimum_value AS bigint), CAST(seq.maximum_value AS bigint),
                   seq.is_cycling, seq.is_cached, seq.cache_size
            FROM sys.sequences AS seq
            INNER JOIN sys.schemas AS s ON s.schema_id = seq.schema_id
            WHERE s.name = @schema AND seq.name = @name;
            """;

        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", name);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) { return null; }

        Sequence seq = new(
            Schema: schema,
            Name: r.GetString(0),
            DataType: r.GetString(1),
            StartValue: r.GetInt64(2),
            Increment: r.GetInt64(3),
            MinValue: r.IsDBNull(4) ? null : r.GetInt64(4),
            MaxValue: r.IsDBNull(5) ? null : r.GetInt64(5),
            IsCycling: r.GetBoolean(6),
            IsCached: r.GetBoolean(7),
            CacheSize: r.IsDBNull(8) ? null : r.GetInt32(8));
        return new SequenceScriptEmitter().EmitCreate(seq);
    }

    private static async Task<string?> ResolveSynonymBodyAsync(
        SqlConnection connection, string schema, string name, CancellationToken ct)
    {
        const string sql = """
            SELECT syn.base_object_name
            FROM sys.synonyms AS syn
            INNER JOIN sys.schemas AS s ON s.schema_id = syn.schema_id
            WHERE s.name = @schema AND syn.name = @name;
            """;
        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", name);
        object? raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null or DBNull) { return null; }

        Synonym syn = new(
            Schema: schema, Name: name, BaseObjectName: (string)raw,
            TargetServer: null, TargetDatabase: null, TargetSchema: null, TargetObject: null);
        return new SynonymScriptEmitter().EmitCreate(syn);
    }

    private static async Task<string?> ResolveUserBodyAsync(
        SqlConnection connection, string name, CancellationToken ct)
    {
        const string sql = """
            SELECT p.type, sp.name AS LoginName, p.default_schema_name,
                   CAST(CASE WHEN sp.name IS NULL AND p.authentication_type IN (1, 3)
                             THEN 1 ELSE 0 END AS bit) AS LoginNameIsHidden
            FROM sys.database_principals AS p
            LEFT JOIN sys.server_principals AS sp ON sp.sid = p.sid
            WHERE p.name = @name;
            """;
        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@name", name);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) { return null; }
        DatabaseUser user = new(
            Name: name,
            TypeCode: r.GetString(0).Trim(),
            LoginName: r.IsDBNull(1) ? null : r.GetString(1),
            DefaultSchema: r.IsDBNull(2) ? "dbo" : r.GetString(2))
        {
            LoginNameIsHidden = r.GetBoolean(3),
        };

        // This body is read, not run: the diff viewer renders it. EmitCreate
        // refuses a hidden login name rather than writing WITHOUT LOGIN, and a
        // throw here would empty the pane for a row the grid can perfectly well
        // show — the same failure as an unscriptable index, one kind over.
        return user.LoginNameIsHidden
            ? $"-- USER {Sql.Q(user.Name)} is mapped to a login this connection cannot name "
              + $"(DEFAULT_SCHEMA = {Sql.Q(user.DefaultSchema)}) — read, not scriptable by DbDelta"
            : new UserScriptEmitter().EmitCreate(user);
    }

    private static async Task<string?> ResolveRoleBodyAsync(
        SqlConnection connection, string name, CancellationToken ct)
    {
        const string ownerSql = """
            SELECT o.name
            FROM sys.database_principals AS r
            LEFT JOIN sys.database_principals AS o ON o.principal_id = r.owning_principal_id
            WHERE r.name = @name AND r.type = 'R';
            """;
        string? owner;
        await using (SqlCommand cmd = new(ownerSql, connection))
        {
            cmd.Parameters.AddWithValue("@name", name);
            object? scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is null) { return null; }
            owner = scalar is DBNull ? "dbo" : (string)scalar;
        }

        const string membersSql = """
            SELECT m.name
            FROM sys.database_role_members AS rm
            INNER JOIN sys.database_principals AS r ON r.principal_id = rm.role_principal_id
            INNER JOIN sys.database_principals AS m ON m.principal_id = rm.member_principal_id
            WHERE r.name = @name
            ORDER BY m.name;
            """;
        List<string> members = [];
        await using (SqlCommand cmd = new(membersSql, connection))
        {
            cmd.Parameters.AddWithValue("@name", name);
            await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                members.Add(r.GetString(0));
            }
        }

        DatabaseRole role = new(name, owner ?? "dbo", members);
        return new RoleScriptEmitter().EmitCreate(role);
    }

    private static async Task<string?> ResolveUserDefinedTypeBodyAsync(
        SqlConnection connection, string schema, string name, CancellationToken ct)
    {
        const string sql = """
            SELECT TYPE_NAME(t.system_type_id), t.max_length, t.precision, t.scale, t.is_nullable
            FROM sys.types AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @name AND t.is_user_defined = 1 AND t.is_assembly_type = 0;
            """;
        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@name", name);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false)) { return null; }

        UserDefinedType udt = new(
            Schema: schema, Name: name,
            BaseTypeName: r.GetString(0),
            MaxLength: r.GetInt16(1),
            Precision: r.GetByte(2),
            Scale: r.GetByte(3),
            IsNullable: r.GetBoolean(4));
        return new UserDefinedTypeScriptEmitter().EmitCreate(udt);
    }

    private static async Task<string?> ResolveModuleBodyAsync(
        SqlConnection connection,
        string schemaName,
        string objectName,
        CancellationToken ct)
    {
        const string sql = """
            SELECT sm.definition
            FROM sys.sql_modules AS sm
            INNER JOIN sys.objects AS o ON o.object_id = sm.object_id
            INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE s.name = @schema AND o.name = @name;
            """;

        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@name", objectName);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is DBNull or null ? null : (string)result;
    }

    private static async Task<string?> ResolveTableBodyAsync(
        SqlConnection connection,
        string schemaName,
        string objectName,
        CancellationToken ct)
    {
        // Check the table exists first by fetching its object_id.
        const string objectIdSql = """
            SELECT t.object_id
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @name;
            """;

        int objectId;
        await using (SqlCommand idCmd = new(objectIdSql, connection))
        {
            idCmd.Parameters.AddWithValue("@schema", schemaName);
            idCmd.Parameters.AddWithValue("@name", objectName);
            object? scalar = await idCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is null or DBNull) { return null; }
            objectId = (int)scalar;
        }

        // Read the single table's columns, constraints, and indexes.
        Table bareTables = await ReadSingleTableAsync(connection, objectId, schemaName, objectName, ct).ConfigureAwait(false);
        IReadOnlyDictionary<int, List<Constraint>> constraints =
            await ReadConstraintsForObjectAsync(connection, objectId, ct).ConfigureAwait(false);
        IReadOnlyDictionary<int, List<TableIndex>> indexes =
            await ReadIndexesForObjectAsync(connection, objectId, ct).ConfigureAwait(false);

        constraints.TryGetValue(objectId, out List<Constraint>? cons);
        indexes.TryGetValue(objectId, out List<TableIndex>? idx);

        Table fullTable = bareTables with
        {
            Constraints = cons ?? [],
            Indexes = idx ?? [],
        };

        // GenerateFullTableBody includes FK + index DDL so the diff viewer
        // surfaces every kind of difference the ComparisonEngine flags
        // (columns + constraints + indexes). Round-16 bugfix.
        return TableScriptEmitter.GenerateFullTableBody(fullTable);
    }

    private static async Task<Table> ReadSingleTableAsync(
        SqlConnection connection,
        int objectId,
        string schemaName,
        string objectName,
        CancellationToken ct)
    {
        const string columnsSql = """
            SELECT
                c.name                                     AS ColumnName,
                TYPE_NAME(c.user_type_id)                  AS TypeName,
                c.max_length                               AS MaxLength,
                c.precision                                AS [Precision],
                c.scale                                    AS Scale,
                c.is_nullable                              AS IsNullable,
                c.is_identity                              AS IsIdentity,
                CAST(ic.seed_value AS bigint)              AS IdentitySeed,
                CAST(ic.increment_value AS bigint)         AS IdentityIncrement,
                dc.definition                              AS DefaultExpression,
                cc.definition                              AS ComputedExpression,
                ISNULL(cc.is_persisted, 0)                 AS IsPersistedComputed,
                c.column_id                                AS Ordinal
            FROM sys.columns AS c
            LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id
                                                 AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id
                                                    AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id
                                                  AND cc.column_id = c.column_id
            WHERE c.object_id = @objectId
            ORDER BY c.column_id;
            """;

        List<Column> columns = [];
        await using SqlCommand cmd = new(columnsSql, connection);
        cmd.Parameters.AddWithValue("@objectId", objectId);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string columnName = r.GetString(0);
            string typeName = r.GetString(1);
            short maxLength = r.GetInt16(2);
            byte precision = r.GetByte(3);
            byte scale = r.GetByte(4);
            bool isNullable = r.GetBoolean(5);
            bool isIdentity = r.GetBoolean(6);
            long? identitySeed = r.IsDBNull(7) ? null : r.GetInt64(7);
            long? identityIncrement = r.IsDBNull(8) ? null : r.GetInt64(8);
            string? defaultExpr = r.IsDBNull(9) ? null : r.GetString(9);
            string? computedExpr = r.IsDBNull(10) ? null : r.GetString(10);
            bool isPersistedComputed = !r.IsDBNull(11) && r.GetBoolean(11);
            int ordinal = r.GetInt32(12);
            columns.Add(new Column(
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

        return new Table(schemaName, objectName, columns);
    }

    private static async Task<IReadOnlyDictionary<int, List<Constraint>>> ReadConstraintsForObjectAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken ct)
    {
        // Reuse the full reader but filter client-side — the reader returns all tables
        // already, so for a single-object resolver we pass a filtered connection.
        // Since ConstraintReader is internal, we replicate a targeted query here.
        Dictionary<int, List<Constraint>> byObject = [];

        await ReadKeyConstraintsForObjectAsync(connection, objectId, byObject, ct).ConfigureAwait(false);
        await ReadForeignKeysForObjectAsync(connection, objectId, byObject, ct).ConfigureAwait(false);
        await ReadChecksForObjectAsync(connection, objectId, byObject, ct).ConfigureAwait(false);
        await ReadDefaultsForObjectAsync(connection, objectId, byObject, ct).ConfigureAwait(false);

        return byObject;
    }

    private static async Task ReadKeyConstraintsForObjectAsync(
        SqlConnection connection,
        int objectId,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        const string sql = """
            SELECT kc.parent_object_id, kc.name, kc.type, i.type AS IndexType,
                   ic.key_ordinal, c.name AS ColumnName, kc.is_system_named
            FROM sys.key_constraints AS kc
            INNER JOIN sys.indexes AS i ON i.object_id = kc.parent_object_id
                                        AND i.index_id = kc.unique_index_id
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id
                                               AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id
                                        AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = @objectId AND ic.is_included_column = 0
            ORDER BY kc.name, ic.key_ordinal;
            """;

        string? currentName = null;
        string? currentType = null;
        bool isClustered = false;
        bool isSystemNamed = false;
        List<string> currentCols = [];

        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@objectId", objectId);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = r.GetString(1);
            string type = r.GetString(2).Trim();
            byte indexType = r.GetByte(3);
            string column = r.GetString(5);
            bool systemNamed = r.GetBoolean(6);

            if (currentName is not null && currentName != name)
            {
                AppendKeyConstraint(byObject, objectId, currentName, currentType!, isClustered, isSystemNamed, currentCols);
                currentCols = [];
            }

            currentName = name;
            currentType = type;
            isClustered = indexType == 1;
            isSystemNamed = systemNamed;
            currentCols.Add(column);
        }

        if (currentName is not null)
        {
            AppendKeyConstraint(byObject, objectId, currentName, currentType!, isClustered, isSystemNamed, currentCols);
        }
    }

    private static void AppendKeyConstraint(
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

    private static async Task ReadForeignKeysForObjectAsync(
        SqlConnection connection,
        int objectId,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        const string sql = """
            SELECT fk.name, cp.name AS LocalCol, sr.name AS RefSchema, tr.name AS RefTable,
                   cr.name AS RefCol, fkc.constraint_column_id,
                   fk.delete_referential_action, fk.update_referential_action,
                   fk.is_disabled, fk.is_not_for_replication
            FROM sys.foreign_keys AS fk
            INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.tables AS tr ON tr.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas AS sr ON sr.schema_id = tr.schema_id
            INNER JOIN sys.columns AS cp ON cp.object_id = fkc.parent_object_id
                                         AND cp.column_id = fkc.parent_column_id
            INNER JOIN sys.columns AS cr ON cr.object_id = fkc.referenced_object_id
                                         AND cr.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = @objectId
            ORDER BY fk.name, fkc.constraint_column_id;
            """;

        string? currentName = null;
        string? refSchema = null, refTable = null;
        ReferentialAction onDelete = ReferentialAction.NoAction;
        ReferentialAction onUpdate = ReferentialAction.NoAction;
        bool isDisabled = false, isNfr = false;
        List<string> localCols = [], refCols = [];

        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@objectId", objectId);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = r.GetString(0);
            string localCol = r.GetString(1);
            string rs = r.GetString(2);
            string rt = r.GetString(3);
            string rc = r.GetString(4);
            byte onDel = r.GetByte(6);
            byte onUpd = r.GetByte(7);
            bool disabled = r.GetBoolean(8);
            bool nfr = r.GetBoolean(9);

            if (currentName is not null && currentName != name)
            {
                AppendForeignKey(byObject, objectId, currentName, localCols, refSchema!, refTable!, refCols, onDelete, onUpdate, isDisabled, isNfr);
                localCols = [];
                refCols = [];
            }

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
            AppendForeignKey(byObject, objectId, currentName, localCols, refSchema!, refTable!, refCols, onDelete, onUpdate, isDisabled, isNfr);
        }
    }

    private static void AppendForeignKey(
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
        bool isNfr)
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
            IsNotForReplication: isNfr);
        Append(byObject, objectId, fk);
    }

    private static async Task ReadChecksForObjectAsync(
        SqlConnection connection,
        int objectId,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        const string sql = """
            SELECT cc.name, cc.definition, cc.is_disabled, cc.is_not_for_replication,
                   cc.is_system_named
            FROM sys.check_constraints AS cc
            WHERE cc.parent_object_id = @objectId
            ORDER BY cc.name;
            """;

        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@objectId", objectId);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            CheckConstraint ck = new(
                Name: r.GetString(0),
                Expression: r.GetString(1),
                IsDisabled: r.GetBoolean(2),
                IsNotForReplication: r.GetBoolean(3))
            { IsSystemNamed = r.GetBoolean(4) };
            Append(byObject, objectId, ck);
        }
    }

    private static async Task ReadDefaultsForObjectAsync(
        SqlConnection connection,
        int objectId,
        Dictionary<int, List<Constraint>> byObject,
        CancellationToken ct)
    {
        const string sql = """
            SELECT dc.name, c.name AS ColumnName, dc.definition, dc.is_system_named
            FROM sys.default_constraints AS dc
            INNER JOIN sys.columns AS c ON c.object_id = dc.parent_object_id
                                        AND c.column_id = dc.parent_column_id
            WHERE dc.parent_object_id = @objectId
            ORDER BY dc.name;
            """;

        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@objectId", objectId);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            DefaultConstraint df = new(
                Name: r.GetString(0),
                ColumnName: r.GetString(1),
                Expression: r.GetString(2))
            { IsSystemNamed = r.GetBoolean(3) };
            Append(byObject, objectId, df);
        }
    }

    private static async Task<IReadOnlyDictionary<int, List<TableIndex>>> ReadIndexesForObjectAsync(
        SqlConnection connection,
        int objectId,
        CancellationToken ct)
    {
        const string sql = """
            SELECT i.name, i.is_unique, i.type, i.has_filter, i.filter_definition,
                   i.index_id, ic.key_ordinal, ic.is_descending_key, ic.is_included_column, c.name AS ColumnName
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = @objectId
              AND i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.type IN (1, 2)
              AND i.name IS NOT NULL
            ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal;
            """;

        Dictionary<int, List<TableIndex>> byObject = [];
        int? currentIndexId = null;
        string? currentName = null;
        bool isUnique = false, isClustered = false;
        string? filter = null;
        List<IndexColumn> keys = [];
        List<string> included = [];

        await using SqlCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@objectId", objectId);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = r.GetString(0);
            bool isUq = r.GetBoolean(1);
            byte indexType = r.GetByte(2);
            bool hasFilter = r.GetBoolean(3);
            string? filterDef = r.IsDBNull(4) ? null : r.GetString(4);
            int indexId = r.GetInt32(5);
            bool isDesc = r.GetBoolean(7);
            bool isIncl = r.GetBoolean(8);
            string column = r.GetString(9);

            if (currentIndexId is not null && currentIndexId != indexId)
            {
                FlushIndex(byObject, objectId, currentName!, isUnique, isClustered, filter, keys, included);
                keys = [];
                included = [];
            }

            currentIndexId = indexId;
            currentName = name;
            isUnique = isUq;
            isClustered = indexType == 1;
            filter = hasFilter ? filterDef : null;

            if (isIncl) { included.Add(column); }
            else { keys.Add(new IndexColumn(column, isDesc)); }
        }

        if (currentIndexId is not null)
        {
            FlushIndex(byObject, objectId, currentName!, isUnique, isClustered, filter, keys, included);
        }

        return byObject;
    }

    private static void FlushIndex(
        Dictionary<int, List<TableIndex>> byObject,
        int objectId,
        string name,
        bool isUnique,
        bool isClustered,
        string? filter,
        List<IndexColumn> keys,
        List<string> included)
    {
        TableIndex ix = new(
            Name: name,
            IsUnique: isUnique,
            IsClustered: isClustered,
            FilterExpression: filter,
            KeyColumns: [.. keys],
            IncludedColumns: [.. included]);

        if (!byObject.TryGetValue(objectId, out List<TableIndex>? list))
        {
            list = [];
            byObject[objectId] = list;
        }
        list.Add(ix);
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

    private static string FormatDataType(string typeName, short maxLength, byte precision, byte scale) =>
        typeName switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{typeName}(max)" : $"{typeName}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{typeName}(max)" : $"{typeName}({maxLength})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
            _ => typeName,
        };
}
