using Microsoft.Data.SqlClient;

namespace DbDelta.Persistence.Sql;

/// <summary>
/// Best-effort SQL Server discovery utilities.
///
/// <see cref="EnumerateServersAsync"/> returns a set of well-known local
/// server aliases. <c>SqlDataSourceEnumerator</c> (the UDP 1434 broadcast
/// to the SQL Browser service) was removed from <c>Microsoft.Data.SqlClient</c>
/// on .NET 6+; callers that need live network scanning should add the
/// <c>System.Data.SqlClient</c> compatibility shim or implement a custom
/// SSRP broadcast. The picker UI must always allow free-text entry.
///
/// <see cref="ListDatabasesAsync"/> runs <c>SELECT name FROM sys.databases</c>
/// against a connected server, filtering out system DBs and offline databases.
/// </summary>
public static class SqlServerDiscovery
{
    /// <summary>
    /// Returns a seed list of well-known local server aliases.
    /// The <paramref name="ct"/> parameter is reserved for a future
    /// network-scan implementation.
    /// </summary>
    public static Task<IReadOnlyList<string>> EnumerateServersAsync(
#pragma warning disable IDE0060
        CancellationToken ct
#pragma warning restore IDE0060
    ) =>
        Task.FromResult<IReadOnlyList<string>>(
        [
            "(local)",
            "localhost",
            "127.0.0.1",
            @".\SQLEXPRESS",
            @"(local)\SQLEXPRESS",
        ]);

    /// <summary>
    /// Connects to <paramref name="connectionString"/> (overriding
    /// <c>InitialCatalog</c> to <c>master</c>) and queries
    /// <c>sys.databases</c> for user databases that are online.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(
        string connectionString,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Strip InitialCatalog so we connect to master regardless of what the user typed.
        SqlConnectionStringBuilder builder = new(connectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 10,
        };

        List<string> result = [];

        await using SqlConnection cn = new(builder.ConnectionString);
        await cn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state_desc = 'ONLINE'
            ORDER BY name;
            """;

        await using SqlCommand cmd = new(sql, cn);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(r.GetString(0));
        }

        return result;
    }
}
