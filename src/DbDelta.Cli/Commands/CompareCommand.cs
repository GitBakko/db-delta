using System.CommandLine;
using DbDelta.Cli.Output;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Providers.LiveDb;

namespace DbDelta.Cli.Commands;

/// <summary>
/// `dbdelta compare` — load source/target via <see cref="LiveDbSource"/>, run
/// <see cref="ComparisonEngine"/>, emit text or JSON, return spec §4.3 exit code.
/// </summary>
internal static class CompareCommand
{
    public static Command Build()
    {
        Option<string> source = new("--source")
        {
            Description = "Source SQL Server connection string",
            Required = true
        };
        Option<string> target = new("--target")
        {
            Description = "Target SQL Server connection string",
            Required = true
        };
        Option<string> format = new("--format")
        {
            Description = "Output format: text | json",
            DefaultValueFactory = _ => "text"
        };

        Command command = new("compare", "Compare two databases and print the differences")
        {
            source,
            target,
            format
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string srcConn = parseResult.GetValue(source)!;
            string tgtConn = parseResult.GetValue(target)!;
            string fmt = parseResult.GetValue(format) ?? "text";

            LiveDbSource srcSource = new(srcConn, "source");
            LiveDbSource tgtSource = new(tgtConn, "target");

            Result<Database> srcResult = await srcSource.LoadAsync(ct);
            if (!srcResult.IsSuccess)
            {
                WriteError(srcResult.Error!);
                return MapErrorToExitCode(srcResult.Error!);
            }

            Result<Database> tgtResult = await tgtSource.LoadAsync(ct);
            if (!tgtResult.IsSuccess)
            {
                WriteError(tgtResult.Error!);
                return MapErrorToExitCode(tgtResult.Error!);
            }

            ComparisonResult comparison = new ComparisonEngine()
                .Compare(srcResult.Value!, tgtResult.Value!, ComparisonOptions.Default);

            string output = fmt.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? JsonFormatter.Format(comparison)
                : TextFormatter.Format(comparison);

            Console.Out.WriteLine(output);

            bool hasDifferences = comparison.Differences
                .Any(d => d.Status is DifferenceStatus.Different
                                   or DifferenceStatus.OnlyInA
                                   or DifferenceStatus.OnlyInB);
            return hasDifferences
                ? ExitCodes.SuccessDifferencesFound
                : ExitCodes.SuccessNoDifferences;
        });

        return command;
    }

    private static void WriteError(Error error)
    {
        string msg = error.Message.Replace("\"", "\\\"");
        string rem = (error.Remediation ?? string.Empty).Replace("\"", "\\\"");
        Console.Error.WriteLine($"{{\"code\":\"{error.Code}\",\"message\":\"{msg}\",\"remediation\":\"{rem}\"}}");
    }

    private static int MapErrorToExitCode(Error error) => error.Code switch
    {
        ErrorCode.CannotConnect or ErrorCode.AuthFailed or ErrorCode.DbNotFound
            => ExitCodes.ConnectionOrAuthError,
        ErrorCode.InsufficientPermissions
            => ExitCodes.InsufficientPermissions,
        ErrorCode.CatalogQueryFailed
        or ErrorCode.UnsupportedSqlServerVersion
        or ErrorCode.EncryptedObjectUnreadable
        or ErrorCode.NoComparableObjects
            => ExitCodes.SchemaReadFailure,
        ErrorCode.UnresolvableDependencyCycle
            => ExitCodes.UnresolvableDependencyCycle,
        ErrorCode.DataPreservationImpossible
        or ErrorCode.UnsupportedSchemaChange
            => ExitCodes.ScriptGenerationFailure,
        ErrorCode.BatchExecutionFailed
        or ErrorCode.TransactionAborted
            => ExitCodes.DeploymentFailure,
        ErrorCode.CancelledByUser
            => ExitCodes.DeploymentCancelled,
        ErrorCode.ProjectFileCorrupt
        or ErrorCode.ProjectFileVersionUnsupported
            => ExitCodes.ProjectFileError,
        ErrorCode.InternalError
            => ExitCodes.InternalError,
        _ => ExitCodes.InternalError,
    };
}
