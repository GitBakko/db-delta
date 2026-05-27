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
/// Runs a T-SQL script (potentially containing GO batch separators) against a
/// SQL Server target, optionally inside a single owned transaction.
/// </summary>
public static partial class SqlExecutor
{
    private const int CommandTimeoutSeconds = 60;
    private const int ConnectTimeoutSeconds = 10;

    /// <summary>
    /// Splits <paramref name="script"/> on <c>GO</c> statements (case-insensitive,
    /// on its own line, optional trailing whitespace) and executes each non-empty
    /// batch, optionally inside a single transaction owned by this method.
    /// Returns success only when every batch succeeds; on the first failure the
    /// transaction (when owned) is rolled back and the error message is returned.
    /// </summary>
    /// <param name="connectionString">Target connection string.</param>
    /// <param name="script">T-SQL script, optionally containing GO separators.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="useOwnTransaction">
    /// When <see langword="true"/> (the default) this method wraps all batches in a
    /// single transaction it owns and rolls back on failure.  Set to
    /// <see langword="false"/> when the script manages its own transaction (e.g. a
    /// self-contained deploy script with <c>BEGIN TRANSACTION … ROLLBACK</c>); in
    /// that case no outer transaction is started so the script's own
    /// <c>XACT_ABORT</c>/<c>ROLLBACK</c> logic takes full effect.
    /// </param>
    public static async Task<SqlBatchResult> ExecuteAsync(
        string connectionString,
        string script,
        CancellationToken ct,
        bool useOwnTransaction = true)
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

            SqlTransaction? tx = useOwnTransaction
                ? (SqlTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;
            int executed = 0;
            try
            {
                foreach (string batch in batches)
                {
                    await using SqlCommand cmd = tx is null
                        ? new(batch, cn) { CommandTimeout = CommandTimeoutSeconds }
                        : new(batch, cn, tx) { CommandTimeout = CommandTimeoutSeconds };
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    executed++;
                }
                if (tx is not null) { await tx.CommitAsync(ct).ConfigureAwait(false); }
                sw.Stop();
                return new SqlBatchResult(true, null, executed, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                if (tx is not null)
                {
                    try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
                }
                sw.Stop();
                return new SqlBatchResult(false, ConnectionStringRedactor.Redact(ex.Message), executed, (int)sw.ElapsedMilliseconds);
            }
            finally
            {
                if (tx is not null) { await tx.DisposeAsync().ConfigureAwait(false); }
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
