using System.CommandLine;
using System.Text.Json;
using DbDelta.Persistence.Sql;

namespace DbDelta.Cli.Commands;

/// <summary>
/// <c>dbdelta apply</c> — read a pre-generated T-SQL script from disk and
/// execute it against the target server via <see cref="SqlExecutor"/>. With
/// <c>--dry-run</c> the script is parsed (batch count reported) but never
/// executed.
/// </summary>
/// <remarks>
/// Transaction handling is decided per script, because the two modes are
/// mutually exclusive. A DbDelta-generated script is self-contained — it opens
/// its own transaction and gates every step — so wrapping it in a client
/// transaction would give <c>@@TRANCOUNT = 2</c> and turn its <c>COMMIT</c> into
/// a bare decrement. Any other script (hand-written, produced by another tool,
/// or a generated one whose envelope was edited out) gets a client-owned
/// transaction instead: without one a failure at batch 3 of 5 left the database
/// half-migrated, which was the only genuine half-migration hole in the product.
/// This XML doc previously claimed the script always ran in a transaction while
/// the code passed <c>useOwnTransaction: false</c> unconditionally.
/// </remarks>
internal static class ApplyCommand
{
    public static Command Build()
    {
        Option<string> target = new("--target")
        {
            Description = "Target SQL Server connection string",
            Required = true
        };
        Option<string> scriptPath = new("--script")
        {
            Description = "Path to the T-SQL script to apply",
            Required = true
        };
        Option<bool> dryRun = new("--dry-run")
        {
            Description = "Parse + count batches, do not execute."
        };
        Option<int> commandTimeout = new("--command-timeout")
        {
            Description =
                "Per-batch command timeout in seconds. 0 = unlimited, for batches that "
                + "legitimately run long (a table rebuild copying rows, a large index build). "
                + $"Default {SqlExecutor.CommandTimeoutSeconds}.",
            DefaultValueFactory = _ => SqlExecutor.CommandTimeoutSeconds,
        };
        Option<bool> noTransaction = new("--no-transaction")
        {
            Description =
                "Do not wrap the script in a transaction. Needed for a script that cannot "
                + "run inside one (e.g. it contains CREATE DATABASE or a backup), and for a "
                + "script written elsewhere that opens its own transaction without saying so "
                + "in a way we can detect. A DbDelta-generated script declares which of the two it is."
        };

        Command command = new("apply", "Execute a generated T-SQL deployment script against the target")
        {
            target,
            scriptPath,
            dryRun,
            commandTimeout,
            noTransaction
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string tgtConn = parseResult.GetValue(target)!;
            string path = parseResult.GetValue(scriptPath)!;
            bool dry = parseResult.GetValue(dryRun);
            int timeout = parseResult.GetValue(commandTimeout);
            bool noTx = parseResult.GetValue(noTransaction);

            if (!File.Exists(path))
            {
                Console.Error.WriteLine(
                    $"{{\"code\":\"script_not_found\",\"message\":\"Script file not found: {path.Replace("\"", "\\\"")}\",\"remediation\":\"Run `dbdelta script --out <path>` first.\"}}");
                return ExitCodes.ProjectFileError;
            }

            string script = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            string[] batches = SqlExecutor.SplitOnGo(script);

            if (dry)
            {
                JsonElement summary = JsonSerializer.SerializeToElement(new
                {
                    dryRun = true,
                    scriptPath = path,
                    batches = batches.Length,
                });
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
                return ExitCodes.SuccessNoDifferences;
            }

            // A self-contained DbDelta script manages its own transaction and says
            // so with its provenance marker; anything else needs one from us, or a
            // mid-script failure leaves the database half-migrated. A script
            // generated with NoTransactions declares THAT instead, and is taken at
            // its word: it used to need --no-transaction on top to get what it had
            // already asked for, so the two same-named options contradicted.
            // The declaration deliberately wins over the syntactic guess:
            // ScriptManagesItsOwnTransaction also matches a BEGIN TRANSACTION at
            // the start of any line, which a CREATE PROCEDURE body carries
            // perfectly innocently, and without this order such a script would
            // report "transaction": "script" while running inside none.
            bool declaredNoTransaction = SqlExecutor.ScriptDeclaresNoTransaction(script);
            bool selfManaged = !declaredNoTransaction && SqlExecutor.ScriptManagesItsOwnTransaction(script);
            bool useOwnTransaction = !selfManaged && !declaredNoTransaction && !noTx;

            SqlBatchResult result = await SqlExecutor.ExecuteAsync(
                tgtConn, script, ct, useOwnTransaction, timeout).ConfigureAwait(false);

            JsonSerializerOptions opts = new() { WriteIndented = true };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                success = result.Success,
                error = result.ErrorMessage,
                batchesExecuted = result.BatchesExecuted,
                totalDurationMs = result.TotalDurationMs,
                rolledBack = result.RolledBack,
                transaction = selfManaged ? "script" : useOwnTransaction ? "client" : "none",
            }, opts)).ConfigureAwait(false);

            return ct.IsCancellationRequested
                ? ExitCodes.DeploymentCancelled
                : result.Success
                    ? ExitCodes.SuccessNoDifferences
                    : ExitCodes.DeploymentFailure;
        });

        return command;
    }
}
