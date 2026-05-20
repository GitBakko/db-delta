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
            IReadOnlyList<Table> tables = await new TableReader().ReadAsync(connection, cancellationToken);
            string dbName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            return Result<Database>.Success(new Database(dbName, schemas, tables));
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
}
