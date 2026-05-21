using System.Diagnostics;
using Microsoft.Data.SqlClient;
using DbDelta.Persistence.Util;

namespace DbDelta.Persistence.Sql;

public static class ConnectionTester
{
    public sealed record TestResult(bool Success, string Message, TimeSpan Latency, string? ServerVersion);

    public static async Task<TestResult> TestAsync(string connectionString, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 10 };
        }
        catch (Exception ex)
        {
            return new TestResult(false, ex.Message, TimeSpan.Zero, null);
        }
        var sw = Stopwatch.StartNew();
        try
        {
            await using SqlConnection cn = new(builder.ConnectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using SqlCommand cmd = new("SELECT @@VERSION", cn);
            object? versionObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();
            string version = versionObj?.ToString()?.Split('\n')[0].Trim() ?? "(sconosciuta)";
            return new TestResult(true, $"Connesso ({sw.Elapsed.TotalMilliseconds:F0} ms)", sw.Elapsed, version);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(false, ConnectionStringRedactor.Redact(ex.Message), sw.Elapsed, null);
        }
    }
}
