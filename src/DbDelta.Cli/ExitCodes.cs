namespace DbDelta.Cli;

/// <summary>
/// Process exit codes returned by the DbDelta CLI. See spec §4.3.
/// </summary>
internal static class ExitCodes
{
    public const int SuccessNoDifferences = 0;
    public const int SuccessDifferencesFound = 1;
    public const int ConnectionOrAuthError = 10;
    public const int InsufficientPermissions = 11;
    public const int SchemaReadFailure = 20;
    public const int ScriptGenerationFailure = 30;
    public const int UnresolvableDependencyCycle = 31;
    public const int DeploymentFailure = 40;
    public const int DeploymentCancelled = 41;
    public const int ProjectFileError = 60;
    public const int InternalError = 99;
}
