using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb;

/// <summary>
/// Thin wrapper over <see cref="SqlConnection"/> that opens a connection
/// with cancellation support.
/// </summary>
internal static class ConnectionFactory
{
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }
}
