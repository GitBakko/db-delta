using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using DbDelta.Persistence.Util;

namespace DbDelta.Persistence.Sql;

/// <summary>
/// Result returned by <see cref="SqlExecutor.ExecuteAsync"/>.
/// </summary>
public sealed record SqlBatchResult(
    bool Success,
    string? ErrorMessage,
    int BatchesExecuted,
    int TotalDurationMs);

/// <summary>
/// Runs a T-SQL script (potentially containing GO batch separators) inside a
/// single transaction against a SQL Server target.
/// </summary>
public static partial class SqlExecutor
{
    private const int CommandTimeoutSeconds = 60;
    private const int ConnectTimeoutSeconds = 10;

    /// <summary>
    /// Splits <paramref name="script"/> on <c>GO</c> statements (case-insensitive,
    /// on its own line, optional trailing whitespace) and executes each non-empty
    /// batch inside a single transaction.
    /// Returns success only when every batch succeeds; on the first failure the
    /// transaction is rolled back and the error message is returned.
    /// </summary>
    public static async Task<SqlBatchResult> ExecuteAsync(
        string connectionString,
        string script,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(script);

        string[] batches = SplitOnGo(script);
        if (batches.Length == 0)
        {
            return new SqlBatchResult(true, null, 0, 0);
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = ConnectTimeoutSeconds,
            };
        }
        catch (Exception ex)
        {
            return new SqlBatchResult(false, ConnectionStringRedactor.Redact(ex.Message), 0, 0);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await using SqlConnection cn = new(builder.ConnectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
            int executed = 0;
            try
            {
                foreach (string batch in batches)
                {
                    await using SqlCommand cmd = new(batch, cn, tx)
                    {
                        CommandTimeout = CommandTimeoutSeconds,
                    };
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    executed++;
                }
                await tx.CommitAsync(ct).ConfigureAwait(false);
                sw.Stop();
                return new SqlBatchResult(true, null, executed, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { /* best-effort rollback */ }
                sw.Stop();
                return new SqlBatchResult(false, ConnectionStringRedactor.Redact(ex.Message), executed, (int)sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new SqlBatchResult(false, ConnectionStringRedactor.Redact(ex.Message), 0, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Splits a T-SQL script into individual GO-delimited batches.
    /// Lines consisting solely of the keyword <c>GO</c> (case-insensitive,
    /// optional surrounding whitespace) act as separators; they are not
    /// included in the output batches.
    /// Empty batches (whitespace-only) are excluded from the result.
    /// </summary>
    public static string[] SplitOnGo(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        string[] lines = script.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        List<string> batches = [];
        List<string> current = [];

        foreach (string line in lines)
        {
            if (GoLinePattern().IsMatch(line))
            {
                string batch = string.Join(Environment.NewLine, current).Trim();
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    batches.Add(batch);
                }
                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }

        // Flush remaining lines after the last GO (or when there is no GO at all).
        string tail = string.Join(Environment.NewLine, current).Trim();
        if (!string.IsNullOrWhiteSpace(tail))
        {
            batches.Add(tail);
        }

        return [.. batches];
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLinePattern();
}
