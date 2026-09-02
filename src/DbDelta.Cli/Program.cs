using System.CommandLine;
using System.Text;
using DbDelta.Cli;
using DbDelta.Cli.Commands;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Dependency;
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
    // The two causes need two remedies, and the CLI must not name an action it
    // does not have: it has no way to exclude one object from a run, so it says
    // what can actually be done on the server instead.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.DataPreservationImpossible,
        ex.Message,
        ex.LoginIsOrphaned
            ? $"The login the user {ex.UserName} was created from no longer exists. Re-create that "
              + $"login, or drop the orphaned user, on the endpoint being read."
            : $"Re-read that endpoint with a login that can see sys.server_principals."));
    return ExitCodes.ScriptGenerationFailure;
}
catch (UnscriptableTableTypeException ex)
{
    // Same refusal, same code, fourth shape: emitting this row would have
    // created a disk-based table type where the source has a memory-optimized
    // one — valid SQL for a different object, which is the one outcome a green
    // banner must never cover.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.DataPreservationImpossible,
        ex.Message,
        $"Deploy the type {ex.Schema}.{ex.Name} by hand — the bucket counts are a sizing "
        + "decision DbDelta cannot make — or leave it out of this run."));
    return ExitCodes.ScriptGenerationFailure;
}
catch (BoundTypeDropException ex)
{
    // Sibling of the one below, same owner decision, same reason it is not an
    // Unscriptable*: the server says Msg 3732 out loud. What it cannot say
    // usefully is WHICH object — for a table type's column it names the internal
    // type-table, a name nobody wrote.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.UnsupportedSchemaChange,
        ex.Message,
        $"Deploy {ex.Binder.SchemaName}.{ex.Binder.ObjectName} first so it stops using "
        + $"{ex.Type.SchemaName}.{ex.Type.ObjectName}, or leave the type out of this run."));
    return ExitCodes.ScriptGenerationFailure;
}
catch (SchemaboundRebuildException ex)
{
    // Not one of the four above, and the file that defines it says why: nothing
    // silent happens here. The server would refuse the DROP TABLE itself with
    // Msg 3729 and roll the whole deploy back. What answering here buys is the
    // NAME of the module doing the blocking, before a line of SQL runs, instead
    // of a server message about an object the operator never chose to touch.
    // Exit 30 all the same: §4.3 already reserves it for a script that could not
    // be produced, and this one could not.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.UnsupportedSchemaChange,
        ex.Message,
        $"Drop the SCHEMABINDING on {ex.Binder.SchemaName}.{ex.Binder.ObjectName}, deploy, and put it "
        + $"back — or leave {ex.Table.SchemaName}.{ex.Table.ObjectName} out of this run."));
    return ExitCodes.ScriptGenerationFailure;
}
catch (DependencyCycleException ex)
{
    // Not a refusal like the four above — those decline to write a statement
    // they cannot write correctly; this one cannot decide an ORDER. Distinct
    // enough that §4.3 gave it a code of its own, 31, which nothing produced
    // until now. The cycle is a legal source schema, not a reader bug: a CHECK
    // calling a function that reads its own table closes the loop as soon as
    // the constraint is written inside CREATE TABLE.
    CliErrorMapper.WriteError(new Error(
        ErrorCode.UnresolvableDependencyCycle,
        ex.Message,
        "Leave one of those objects out of this run, or add the constraint by hand "
        + "with an ALTER TABLE after the deploy — inside CREATE TABLE it has to come "
        + "before the function it calls, and that function needs the table."));
    return ExitCodes.UnresolvableDependencyCycle;
}
catch (Exception ex)
{
    CliErrorMapper.WriteError(new Error(
        ErrorCode.InternalError,
        ex.Message,
        "This is unexpected — re-run with the same arguments and open an issue if it persists."));
    return ExitCodes.InternalError;
}
