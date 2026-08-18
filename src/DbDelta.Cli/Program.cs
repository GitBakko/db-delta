using System.CommandLine;
using DbDelta.Cli;
using DbDelta.Cli.Commands;
using DbDelta.Core.Abstractions;
using DbDelta.Core.ScriptGen;

RootCommand root = new("DbDelta — open-source SQL Server schema compare and deployment tool")
{
    CompareCommand.Build(),
    ReportCommand.Build(),
    ScriptCommand.Build(),
    ApplyCommand.Build()
};

// System.CommandLine's default exception handler catches anything a verb throws
// and returns 1 — which §4.3 defines as "succeeded, differences found". So a
// comparison the engine refuses to represent (two objects colliding under the
// target's collation, ComparisonEngine.MapByIdentity) reached a CI pipeline
// looking exactly like a normal drift report, and the next step ran on a script
// that was never generated. Owning the handler is what makes every unexpected
// failure exit 99 instead, for all four verbs.
CommandLineConfiguration config = new(root) { EnableDefaultExceptionHandler = false };

try
{
    return await root.Parse(args, config).InvokeAsync();
}
catch (OperationCanceledException)
{
    // Ctrl+C / SIGTERM: the run stopped on request, which is not a failure of
    // the tool and has its own code.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.CancelledByUser,
        "Operation cancelled.",
        "Re-run the command to start again."));
    return ExitCodes.DeploymentCancelled;
}
catch (UnscriptableIndexException ex)
{
    // Not unexpected, and not a bug to report: the generator refused on
    // purpose, because the script it was asked for would drop an index it
    // cannot write back. 30, the code §4.3 reserves for a script that could not
    // be produced — never 99 with an invitation to open an issue.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.DataPreservationImpossible,
        ex.Message,
        $"Re-create {ex.IndexName} by hand after the deploy, or leave "
        + $"{ex.Schema}.{ex.Table} out of this run."));
    return ExitCodes.ScriptGenerationFailure;
}
catch (UnscriptablePermissionException ex)
{
    // Same refusal, same code, different securable: emitting this row would
    // have granted over the whole database instead of over one object.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.DataPreservationImpossible,
        ex.Message,
        "Re-run with a login that can see the securable, or drop --include-permissions "
        + "to leave permissions out of this run."));
    return ExitCodes.ScriptGenerationFailure;
}
catch (Exception ex)
{
    CliErrorMapper.WriteError(new Error(
        ErrorCode.InternalError,
        ex.Message,
        "This is unexpected — re-run with the same arguments and open an issue if it persists."));
    return ExitCodes.InternalError;
}
