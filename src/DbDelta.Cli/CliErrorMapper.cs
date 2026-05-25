using DbDelta.Core.Abstractions;

namespace DbDelta.Cli;

/// <summary>
/// Shared mapping from <see cref="Error"/> to (a) the JSON line written to
/// stderr and (b) the spec §4.3 exit code. Used by every CLI verb so the wire
/// contract for error output stays in one place.
/// </summary>
internal static class CliErrorMapper
{
    public static void WriteError(Error error)
    {
        string msg = error.Message.Replace("\"", "\\\"");
        string rem = (error.Remediation ?? string.Empty).Replace("\"", "\\\"");
        Console.Error.WriteLine($"{{\"code\":\"{error.Code}\",\"message\":\"{msg}\",\"remediation\":\"{rem}\"}}");
    }

    public static int MapErrorToExitCode(Error error) => error.Code switch
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
