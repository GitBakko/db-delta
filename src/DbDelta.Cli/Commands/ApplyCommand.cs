using System.CommandLine;
using System.Text.Json;
using DbDelta.Persistence.Sql;

namespace DbDelta.Cli.Commands;

/// <summary>
/// <c>dbdelta apply</c> — read a pre-generated T-SQL script from disk and
/// execute it against the target server inside a single GO-split transaction
/// via <see cref="SqlExecutor"/>. With <c>--dry-run</c> the script is parsed
/// (batch count reported) but never executed.
/// </summary>
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

        Command command = new("apply", "Execute a generated T-SQL deployment script against the target")
        {
            target,
            scriptPath,
            dryRun
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string tgtConn = parseResult.GetValue(target)!;
            string path = parseResult.GetValue(scriptPath)!;
            bool dry = parseResult.GetValue(dryRun);

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

            SqlBatchResult result = await SqlExecutor.ExecuteAsync(tgtConn, script, ct).ConfigureAwait(false);

            JsonSerializerOptions opts = new() { WriteIndented = true };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                success = result.Success,
                error = result.ErrorMessage,
                batchesExecuted = result.BatchesExecuted,
                totalDurationMs = result.TotalDurationMs,
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
