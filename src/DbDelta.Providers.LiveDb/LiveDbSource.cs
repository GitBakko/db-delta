using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using DbDelta.Providers.LiveDb.Readers;
using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb;

/// <summary>
/// Live SQL Server <see cref="ISchemaSource"/>. Reads via direct sys.* catalog queries.
/// </summary>
public sealed class LiveDbSource : ISchemaSource
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

            string dbName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            return Result<Database>.Success(new Database(dbName, schemas, tables, views, procs));
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
            return Result<Database>.Failure(new Error(
                ErrorCode.CannotConnect,
                ex.Message,
                "Verify server name, network connectivity, and firewall rules."));
        }
        catch (SqlException ex)
        {
            return Result<Database>.Failure(new Error(
                ErrorCode.CatalogQueryFailed,
                ex.Message));
        }
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
