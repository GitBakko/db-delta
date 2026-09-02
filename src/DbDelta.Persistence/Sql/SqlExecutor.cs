using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Util;

namespace DbDelta.Persistence.Sql;

/// <summary>
/// Result returned by <see cref="SqlExecutor.ExecuteAsync"/>.
/// </summary>
/// <param name="Success">True when every batch executed without error.</param>
/// <param name="ErrorMessage">The failure reason, redacted; null on success.</param>
/// <param name="BatchesExecuted">How many batches ran before the outcome.</param>
/// <param name="TotalDurationMs">Wall-clock duration of the whole run.</param>
/// <param name="RolledBack">
/// True when a rollback was issued and acknowledged, so the target is known to
/// be unchanged. False on success, and false when the failure left the outcome
/// indeterminate from the client's point of view — an operator needs to be able
/// to tell "nothing was applied" from "we do not know", which the previous
/// result shape could not express.
/// </param>
/// <param name="Messages">
/// Everything the server said while the script ran, in the order it said it:
/// every PRINT, and on failure every error the exception carried rather than
/// the one line <c>ex.Message</c> renders. This is the difference between the
/// app showing two lines and SSMS showing a hundred.
/// </param>
public sealed record SqlBatchResult(
    bool Success,
    string? ErrorMessage,
    int BatchesExecuted,
    int TotalDurationMs,
    bool RolledBack = false,
    IReadOnlyList<SqlServerMessage>? Messages = null)
{
    /// <summary>Everything the server said, never null.</summary>
    public IReadOnlyList<SqlServerMessage> Messages { get; init; } = Messages ?? [];

    /// <summary>Just the ones the server raised as errors.</summary>
    public IReadOnlyList<SqlServerMessage> Errors => [.. Messages.Where(m => m.IsError)];
}

/// <summary>
/// Runs a T-SQL script (potentially containing GO batch separators) against a
/// SQL Server target, optionally inside a single owned transaction.
/// </summary>
public static partial class SqlExecutor
{
    /// <summary>Default per-batch command timeout, in seconds.</summary>
    public const int CommandTimeoutSeconds = 60;

    private const int ConnectTimeoutSeconds = 10;

    /// <summary>
    /// Short, fixed timeout for the best-effort rollback. Deliberately not the
    /// caller's timeout: if the batch just timed out, waiting the same amount
    /// again to give up on the rollback would double an already-bad wait.
    /// </summary>
    private const int RollbackTimeoutSeconds = 15;

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
    /// that case no outer transaction is started, so the script's envelope is the
    /// only one. Its FOOTER, though, does not get to act on a failure: this method
    /// stops at the batch that throws, so the script's <c>IF @@ERROR</c> gate and
    /// its closing verdict never run. What keeps the target whole is that the
    /// script's <c>COMMIT</c> is never reached — not the rollback the footer
    /// would have emitted.
    /// </param>
    /// <param name="commandTimeoutSeconds">
    /// Per-batch command timeout. <c>0</c> means unlimited, which is what a DBA
    /// expects of a deployment script: the default 60 s makes a legitimately long
    /// batch — an <c>INSERT … SELECT</c> copying a 30M-row table during an
    /// identity rebuild, a large index build — impossible to deploy at all, since
    /// the timeout aborts it and the transaction rolls the whole thing back.
    /// Callers that cannot cancel a running operation should keep it bounded.
    /// </param>
    public static async Task<SqlBatchResult> ExecuteAsync(
        string connectionString,
        string script,
        CancellationToken ct,
        bool useOwnTransaction = true,
        int commandTimeoutSeconds = CommandTimeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);
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
        // Filled from two places — the InfoMessage callback and the catch
        // blocks — and the callback runs on whichever thread completed the read,
        // so the list is guarded rather than left to chance.
        List<SqlServerMessage> messages = [];
        void Collect(SqlErrorCollection errors)
        {
            lock (messages)
            {
                foreach (SqlError error in errors) { messages.Add(SqlServerMessage.From(error)); }
            }
        }
        IReadOnlyList<SqlServerMessage> Collected()
        {
            lock (messages) { return [.. messages]; }
        }

        try
        {
            await using SqlConnection cn = new(builder.ConnectionString);
            // Severity 10 and below never throws, so without this every PRINT in
            // the script — the running commentary of which object was being
            // created — is discarded. Deliberately WITHOUT
            // FireInfoMessageEventOnUserErrors: that flag reroutes errors up to
            // severity 16 through this same callback instead of throwing, which
            // would turn a failed deploy into a silent success.
            cn.InfoMessage += (_, e) => Collect(e.Errors);
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
                        ? new(batch, cn) { CommandTimeout = commandTimeoutSeconds }
                        : new(batch, cn, tx) { CommandTimeout = commandTimeoutSeconds };
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    executed++;
                }
                if (tx is not null) { await tx.CommitAsync(ct).ConfigureAwait(false); }
                sw.Stop();
                return new SqlBatchResult(
                    true, null, executed, (int)sw.ElapsedMilliseconds, Messages: Collected());
            }
            catch (Exception ex)
            {
                if (ex is SqlException sqlEx) { Collect(sqlEx.Errors); }
                // A rollback must never be cancellable — passing `ct` here meant
                // that when cancellation WAS the failure the rollback threw
                // immediately and we fell through to connection dispose, which
                // does roll back but silently, leaving the caller unable to say
                // whether the target had been touched.
                bool rolledBack = await TryRollbackAsync(cn, tx).ConfigureAwait(false);
                sw.Stop();
                return new SqlBatchResult(
                    false,
                    ConnectionStringRedactor.Redact(ex.Message),
                    executed,
                    (int)sw.ElapsedMilliseconds,
                    rolledBack,
                    Collected());
            }
            finally
            {
                if (tx is not null) { await tx.DisposeAsync().ConfigureAwait(false); }
            }
        }
        catch (Exception ex)
        {
            if (ex is SqlException sqlEx) { Collect(sqlEx.Errors); }
            sw.Stop();
            return new SqlBatchResult(
                false,
                ConnectionStringRedactor.Redact(ex.Message),
                0,
                (int)sw.ElapsedMilliseconds,
                Messages: Collected());
        }
    }

    /// <summary>
    /// Rolls back whatever is still open, without letting cancellation stop it.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the rollback was issued and acknowledged, so
    /// the target is known to be unchanged.
    /// </returns>
    /// <remarks>
    /// Two cases. With a client-owned transaction we roll that back. Without one
    /// — the self-contained deploy script manages its own — this method reports
    /// exactly one thing: whether the transaction was STILL OPEN when we asked.
    /// Measured rather than assumed, on <c>mssql/server:2022-latest</c>
    /// 16.0.4265.3, reading <c>@@TRANCOUNT</c> and <c>XACT_STATE()</c> on the
    /// failing connection itself.
    /// <list type="bullet">
    /// <item>Still open, so the ROLLBACK below really is issued and we report
    /// true: Msg 3701 where it means "the object is not there" (severity 11),
    /// Msg 15225 from <c>sp_rename</c>, and Msg 4145, a compile error whose batch
    /// never ran at all.</item>
    /// <item>Already gone, so this is a no-op and we report false: Msg 3701 where
    /// it means "you do not have permission" (severity 14), Msg 2714, Msg 1767,
    /// Msg 8134.</item>
    /// </list>
    /// Two things the discriminant is NOT. It is not the message number: Msg 3701
    /// sits on both lists, at severity 11 and at severity 14. And it is not
    /// <c>XACT_ABORT</c> — every case above was run with the flag ON and again
    /// with it OFF and came out identical, which is what the rest of this repo
    /// already says (see the <c>&lt;remarks&gt;</c> on
    /// <c>DeploymentScriptWriter</c>: "the reason is severity rather than
    /// XACT_ABORT"). Severity tracks the split for the run-time errors; the
    /// compile error at severity 15 is the case that shows severity is a good
    /// predictor and not the mechanism.
    /// <para>
    /// What true buys is CONFIRMATION, not preservation. In the still-open case
    /// disposing the connection returns it to the pool and rolls the transaction
    /// back anyway, so the target survives either way — issuing the ROLLBACK
    /// ourselves is how we get to SAY it survived. And that case is the likeliest
    /// way a real deploy fails: a bare DROP of an object that is no longer there,
    /// i.e. the second run that bare DROPs already declare unsupported. The
    /// script's own footer helps in neither case, because this executor stops at
    /// the batch that throws — the <c>IF @@ERROR</c> gate and the final verdict
    /// never run. It earns its keep on TIMEOUT and CANCELLATION too, where the
    /// client gave up mid-batch. Purely best-effort: after a timeout the
    /// connection may be unusable, and in that case dispose (which returns it to
    /// the pool and triggers <c>sp_reset_connection</c>) is what rolls it back —
    /// we just cannot confirm it, so we report false rather than claim it.
    /// </para>
    /// <para>
    /// In that second case the answer comes from <c>@@TRANCOUNT</c>, not from
    /// the command succeeding. One branch used to serve two opposite meanings
    /// with no discriminant: with a script envelope <c>@@TRANCOUNT == 0</c> means
    /// XACT_ABORT already rolled everything back, but under
    /// <c>--no-transaction</c> it means there was never a transaction and every
    /// batch before the failure is permanently committed. Returning true for
    /// both printed <c>"rolledBack": true</c> over a half-migrated target. We now
    /// report true only for a rollback this method actually issued; the
    /// already-rolled-back envelope case under-claims, which is the safe
    /// direction for a flag documented as "known to be unchanged".
    /// </para>
    /// </remarks>
    private static async Task<bool> TryRollbackAsync(SqlConnection cn, SqlTransaction? tx)
    {
        if (tx is not null)
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            if (cn.State != System.Data.ConnectionState.Open) { return false; }
            await using SqlCommand rollback = new(
                "IF @@TRANCOUNT > 0 BEGIN ROLLBACK TRANSACTION; SELECT 1 END ELSE SELECT 0;", cn)
            { CommandTimeout = RollbackTimeoutSeconds };
            object? rolledBack =
                await rollback.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            return rolledBack is int flag && flag == 1;
        }
        catch
        {
            return false;
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

    /// <summary>
    /// True when <paramref name="script"/> opens its own transaction, i.e. it is
    /// self-contained and must NOT be wrapped in a client-owned one.
    /// </summary>
    /// <remarks>
    /// The two modes are mutually exclusive: a client transaction plus the
    /// script's own <c>BEGIN TRANSACTION</c> gives <c>@@TRANCOUNT = 2</c>, so the
    /// script's <c>COMMIT</c> becomes a bare decrement and the client's commit
    /// then commits work the script believed its failure gate had blocked.
    /// <para>
    /// Answered by PROVENANCE first: a script DbDelta generated says so with
    /// <c>-- dbdelta:transaction=script</c> (<c>DeploymentScriptWriter</c>
    /// writes it next to the <c>BEGIN TRANSACTION</c> it emits), and that is not
    /// a guess. The syntactic fallback stays for scripts written elsewhere and
    /// for scripts generated before the marker existed — it is a heuristic and
    /// it over-detects: the pattern is line-anchored, so a
    /// <c>BEGIN TRANSACTION</c> at the start of a line inside a block comment or
    /// a procedure body reads as self-contained, and that script then runs with
    /// no transaction at all. Ruling that out needs a parser rather than a
    /// regex; <c>--no-transaction</c> is the documented escape hatch meanwhile.
    /// </para>
    /// </remarks>
    public static bool ScriptManagesItsOwnTransaction(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return script.Contains(DeploymentScriptWriter.SelfManagedTransactionMarker, StringComparison.Ordinal)
            || BeginTransactionPattern().IsMatch(script);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLinePattern();

    [GeneratedRegex(
        @"^\s*BEGIN\s+TRAN(SACTION)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex BeginTransactionPattern();
}
