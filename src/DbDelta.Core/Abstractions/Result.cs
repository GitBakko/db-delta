namespace DbDelta.Core.Abstractions;

/// <summary>
/// Strongly-typed domain errors used at port boundaries (see spec §4).
/// </summary>
public enum ErrorCode
{
    CannotConnect,
    AuthFailed,
    DbNotFound,
    InsufficientPermissions,
    UnsupportedSqlServerVersion,
    EncryptedObjectUnreadable,
    CatalogQueryFailed,
    NoComparableObjects,
    UnresolvableDependencyCycle,
    DataPreservationImpossible,
    UnsupportedSchemaChange,
    BatchExecutionFailed,
    TransactionAborted,
    CancelledByUser,
    ProjectFileCorrupt,
    ProjectFileVersionUnsupported,
    InternalError,
}

public sealed record Error(ErrorCode Code, string Message, string? Remediation = null);

public sealed record Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(Value!) : onFailure(Error!);
    }
}
