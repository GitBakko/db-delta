using Microsoft.Data.SqlClient;

namespace DbDelta.Providers.LiveDb;

/// <summary>
/// Thin wrapper over <see cref="SqlConnection"/> that opens a connection
/// with cancellation support.
/// </summary>
internal static class ConnectionFactory
{
    /// <summary>
    /// Per-command wait, in seconds, for every catalog read. ADO.NET's default
    /// of 30 s is one slow query away from killing a whole compare, and the
    /// queries here are not small: the column read joins four catalog views for
    /// the entire database, and the index read carries a per-row subquery on
    /// <c>sys.partitions</c>. The deploy path was given 600 s long ago; the
    /// read path, which scans the whole catalog, had been left on the default.
    /// </summary>
    /// <remarks>
    /// Deliberately a bound and not <c>0</c> (unlimited): a read blocked behind
    /// someone else's schema lock has to end by itself, because nothing else
    /// would end it. A read holds no transaction and the app can cancel it, so
    /// the bound can afford to be generous.
    /// </remarks>
    // ponytail: one knob. SqlCommand inherits SqlConnection.CommandTimeout, so
    // this reaches every read command without touching a single call site.
    public const int ReadCommandTimeoutSeconds = 300;

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(WithReadTimeout(connectionString));
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// <paramref name="connectionString"/> with <c>Command Timeout</c> set to
    /// <see cref="ReadCommandTimeoutSeconds"/> — unless the caller already said
    /// otherwise, in which case the string comes back untouched.
    /// </summary>
    /// <remarks>
    /// The keyword is the only lever there is: <c>SqlConnection.CommandTimeout</c>
    /// is get-only, and every command created from the connection inherits it.
    /// <c>ShouldSerialize</c>, not <c>ContainsKey</c>: the latter answers "is
    /// this a known keyword", which is always true. A user who wrote
    /// <c>Command Timeout=0</c> meant unlimited and keeps it.
    /// </remarks>
    internal static string WithReadTimeout(string connectionString)
    {
        SqlConnectionStringBuilder builder = new(connectionString);
        if (builder.ShouldSerialize("Command Timeout")) { return connectionString; }

        builder.CommandTimeout = ReadCommandTimeoutSeconds;
        return builder.ConnectionString;
    }
}
