using DbDelta.Core.Abstractions;
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using DbDelta.Providers.LiveDb.Readers;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb;

/// <summary>
/// Live SQL Server schema source. Reads via direct sys.* catalog queries.
/// </summary>
public sealed class LiveDbSource
{
    private readonly string _connectionString;

    public LiveDbSource(string connectionString, string? displayName = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        var builder = new SqlConnectionStringBuilder(connectionString);
        DisplayName = displayName ?? $"{builder.DataSource}/{builder.InitialCatalog}";
    }

    public string DisplayName { get; }

    public async Task<Result<Database>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using SqlConnection connection = await ConnectionFactory.OpenAsync(_connectionString, cancellationToken);

            // C3 — refuse to read a catalog we can only see part of. See
            // ReadCatalogAccessAsync for why a partial read is worse than no
            // read at all.
            CatalogAccess access = await ReadCatalogAccessAsync(connection, cancellationToken);
            if (access.MissingGrants() is { Count: > 0 } missing)
            {
                return Result<Database>.Failure(new Error(
                    ErrorCode.InsufficientPermissions,
                    $"The {DisplayName} endpoint is connected as '{access.PrincipalName}', which cannot read the "
                    + $"whole catalog of database '{connection.Database}': {string.Join("; ", missing.Select(m => m.Lack))}. "
                    + "Reading part of it is not safe — SQL Server simply omits what the principal cannot see, so "
                    + "every hidden object would be reported as a difference and scripted as a DROP against the "
                    + "other endpoint.",
                    "Run, in that database: " + string.Join(" ", missing.Select(m => m.Grant))));
            }

            // M13-PARITY.5 #32 — capture DB default collation up-front so the
            // emitter can skip COLLATE clauses on columns that match it.
            string? defaultCollation = await ReadDefaultCollationAsync(connection, cancellationToken);
            IReadOnlyList<Schema> schemas = await new SchemaReader().ReadAsync(connection, cancellationToken);

            // Tables with their columns (M1)
            IReadOnlyList<Table> bareTables = await new TableReader().ReadAsync(connection, cancellationToken);

            // M2: constraints + indexes, keyed by sys.objects.object_id
            IReadOnlyDictionary<int, List<Constraint>> constraintsByObject =
                await new ConstraintReader().ReadAsync(connection, cancellationToken);
            IReadOnlyDictionary<int, List<TableIndex>> indexesByObject =
                await new IndexReader().ReadAsync(connection, cancellationToken);

            // TableReader does not expose the object id; do one extra round-trip to join client-side.
            IReadOnlyDictionary<(string Schema, string Name), int> objectIdByName =
                await ReadTableObjectIdsAsync(connection, cancellationToken);

            var tables = new List<Table>(bareTables.Count);
            foreach (Table t in bareTables)
            {
                int? objectId = objectIdByName.TryGetValue((t.Schema, t.Name), out int id) ? id : null;
                IReadOnlyList<Constraint> cons = objectId is int cid
                    && constraintsByObject.TryGetValue(cid, out List<Constraint>? cl)
                        ? cl
                        : [];
                IReadOnlyList<TableIndex> idx = objectId is int iid
                    && indexesByObject.TryGetValue(iid, out List<TableIndex>? il)
                        ? il
                        : [];
                tables.Add(t with { Constraints = cons, Indexes = idx });
            }

            // M3: views + stored procedures
            ModuleReader moduleReader = new();
            IReadOnlyList<View> views = await moduleReader.ReadViewsAsync(connection, cancellationToken);
            IReadOnlyList<StoredProcedure> procs = await moduleReader.ReadProceduresAsync(connection, cancellationToken);
            IReadOnlyList<Function> functions = await moduleReader.ReadFunctionsAsync(connection, cancellationToken);
            IReadOnlyList<Trigger> triggers = await moduleReader.ReadTriggersAsync(connection, cancellationToken);

            // M5: sequences + synonyms + alias UDTs
            IReadOnlyList<Sequence> sequences = await new SequenceReader().ReadAsync(connection, cancellationToken);
            IReadOnlyList<Synonym> synonyms = await new SynonymReader().ReadAsync(connection, cancellationToken);
            IReadOnlyList<UserDefinedType> udts = await new UserDefinedTypeReader().ReadAsync(connection, cancellationToken);

            // M13-FIX.4: table-type UDTs (13th object kind).
            IReadOnlyList<TableTypeUdt> tableTypes =
                await new TableTypeUdtReader().ReadAsync(connection, cancellationToken);

            // M6: users + roles + permissions
            IReadOnlyList<DatabaseUser> users = await new UserReader().ReadAsync(connection, cancellationToken);
            IReadOnlyList<DatabaseRole> roles = await new RoleReader().ReadAsync(connection, cancellationToken);
            IReadOnlyList<Permission> permissions = await new PermissionReader().ReadAsync(connection, cancellationToken);

            // #24: object-level dependency edges (sys.sql_expression_dependencies).
            IReadOnlyList<DependencyEdge> dependencies =
                await new DependencyReader().ReadAsync(connection, cancellationToken);

            // What the readers above do NOT cover, counted in one round trip, so
            // the verdict can state its own scope. A database holding nothing
            // outside the thirteen kinds yields an empty census and no message.
            UnexaminedCensus unexamined =
                await new UnexaminedReader().ReadAsync(connection, cancellationToken);

            string dbName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            Database db = new(dbName, schemas, tables, views, procs, functions, triggers)
            {
                Sequences = sequences,
                Synonyms = synonyms,
                UserDefinedTypes = udts,
                TableTypeUdts = tableTypes,
                Users = users,
                Roles = roles,
                Permissions = permissions,
                Dependencies = dependencies,
                DefaultCollation = defaultCollation,
                Unexamined = unexamined,
            };
            return Result<Database>.Success(db);
        }
        catch (SqlException ex) when (ex.Number is 4060 or 18456)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.AuthFailed,
                ex.Message,
                "Verify credentials and that the user has CONNECT permission on the database."));
        }
        catch (SqlException ex) when (ex.Number is 53 or -2)
        {
            // -2 is both "could not reach the server" and "the query ran out of
            // time", and the two want opposite remedies. Name them both rather
            // than send someone to the firewall over a slow catalog read.
            return Result<Database>.Failure(new Error(
                ErrorCode.CannotConnect,
                ex.Message,
                "Verify server name, network connectivity, and firewall rules. If the timeout "
                + $"expired during execution, the read already waited {ConnectionFactory.ReadCommandTimeoutSeconds} s — "
                + "raise it with 'Command Timeout=<seconds>' in the connection string."));
        }
        catch (SqlException ex)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.CatalogQueryFailed,
                ex.Message));
        }
    }

    /// <summary>
    /// What this connection is allowed to read of the catalog, and who it is.
    /// </summary>
    private readonly record struct CatalogAccess(
        bool SeesEveryObject,
        bool ReadsDependencies,
        string PrincipalName)
    {
        /// <summary>
        /// The grants this principal is missing, each paired with the statement
        /// that fixes it. Empty ⇒ the read is safe to attempt.
        /// </summary>
        public IReadOnlyList<(string Lack, string Grant)> MissingGrants()
        {
            List<(string Lack, string Grant)> missing = [];
            if (!SeesEveryObject)
            {
                missing.Add((
                    "it lacks VIEW DEFINITION, so sys.tables and its siblings hide every object it holds "
                    + "no permission on",
                    $"GRANT VIEW DEFINITION TO {Core.ScriptGen.Sql.Q(PrincipalName)};"));
            }
            if (!ReadsDependencies)
            {
                missing.Add((
                    "it lacks SELECT on sys.sql_expression_dependencies, which by default is granted to "
                    + "db_owner only, so the dependency edges that order the generated script would be "
                    + "missing",
                    $"GRANT SELECT ON sys.sql_expression_dependencies TO {Core.ScriptGen.Sql.Q(PrincipalName)};"));
            }
            return missing;
        }
    }

    /// <summary>
    /// C3 — asserts that this connection can read the whole catalog, before any
    /// of it is read.
    /// <para>
    /// The sys.* views are filtered by metadata visibility: a principal only
    /// sees rows for objects it holds some permission on. A least-privilege
    /// source login with SELECT on 3 tables of 50 therefore reads a *subset* of
    /// the schema, and the comparison engine classifies the 47 it cannot see as
    /// target-only — i.e. the generated script drops them. Nothing downstream
    /// can tell that apart from a genuine difference, so the read has to fail
    /// here rather than succeed with a truncated catalog.
    /// </para>
    /// <para>
    /// VIEW DEFINITION at database scope is the permission that lifts the
    /// filter; the server-scope disjunct covers a login holding the covering
    /// VIEW ANY DEFINITION instead. Both are evaluated by SQL Server itself, so
    /// role membership and covering permissions are accounted for.
    /// </para>
    /// <para>
    /// sys.sql_expression_dependencies is checked separately because it is not
    /// covered by either: SELECT on it is granted to db_owner alone by default.
    /// Without it <see cref="DependencyReader"/> throws, and were it made to
    /// degrade to an empty edge list instead, the generator would silently fall
    /// back to kind-then-alphabetical ordering — a quieter version of the same
    /// "we only read part of it" failure.
    /// </para>
    /// </summary>
    private static async Task<CatalogAccess> ReadCatalogAccessAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        const string sql = """
            SELECT CASE WHEN HAS_PERMS_BY_NAME(QUOTENAME(DB_NAME()), 'DATABASE', 'VIEW DEFINITION') = 1
                          OR HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DEFINITION') = 1
                        THEN 1 ELSE 0 END AS SeesEveryObject,
                   CASE WHEN HAS_PERMS_BY_NAME('sys.sql_expression_dependencies', 'OBJECT', 'SELECT') = 1
                        THEN 1 ELSE 0 END AS ReadsDependencies,
                   USER_NAME() AS PrincipalName;
            """;
        await using SqlCommand cmd = new(sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false)
            ? new CatalogAccess(
                r.GetInt32(0) == 1,
                r.GetInt32(1) == 1,
                r.IsDBNull(2) ? "unknown" : r.GetString(2))
            : new CatalogAccess(false, false, "unknown");
    }

    /// <summary>
    /// Reads the database default collation via <c>DATABASEPROPERTYEX</c>.
    /// Falls back to null on any failure — the script generator treats a null
    /// default as "no default known" and emits explicit COLLATE on every
    /// string column with a non-null collation, matching Redgate's defensive
    /// shape (M13-PARITY.5 #32).
    /// </summary>
    private static async Task<string?> ReadDefaultCollationAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128));";
        await using SqlCommand cmd = new(sql, connection);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string s && !string.IsNullOrEmpty(s) ? s : null;
    }

    private static async Task<IReadOnlyDictionary<(string Schema, string Name), int>> ReadTableObjectIdsAsync(
        SqlConnection connection,
        CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS SchemaName, t.name AS TableName, t.object_id
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0;
            """;
        Dictionary<(string Schema, string Name), int> map = [];
        await using SqlCommand cmd = new(sql, connection);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            map[(r.GetString(0), r.GetString(1))] = r.GetInt32(2);
        }
        return map;
    }
}
