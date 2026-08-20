using System.CommandLine;
using System.Text;
using DbDelta.Cli;
using DbDelta.Cli.Commands;
using DbDelta.Core.Abstractions;
using DbDelta.Core.ScriptGen;

// Every user-facing line this tool prints is Italian, and half of them carry an
// accent or a guillemet. Left to the console's code page they come out as
// whatever that page can represent — and redirected to a file or a pipe, as
// bytes no UTF-8 reader can decode: `dbdelta compare > out.txt` produced a file
// with a replacement character in place of every à. UTF-8 makes the bytes the
// same everywhere, which is the only way an acceptance test can assert on them.
// Guarded because a host with no console at all throws here, and that is not a
// reason to fail the run.
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException)
{
    // No console attached — nothing to configure, nothing to report.
}

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
catch (UnscriptableUserException ex)
{
    // Same refusal, same code, third securable: emitting this row would have
    // created the user WITHOUT LOGIN instead of mapped to the login it has.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.DataPreservationImpossible,
        ex.Message,
        $"Re-read that endpoint with a login that can see sys.server_principals, or exclude "
        + $"the user {ex.UserName} from this run."));
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
