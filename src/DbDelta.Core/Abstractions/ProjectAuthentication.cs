namespace DbDelta.Core.Abstractions;

/// <summary>
/// How the application authenticates when connecting to a SQL Server instance
/// referenced by a saved project.
/// </summary>
public enum AuthenticationMode
{
    SqlServer,
    WindowsIntegrated,
}

/// <summary>
/// Authentication settings embedded in a <see cref="ProjectEndpoint"/>.
/// </summary>
public sealed record ProjectAuthentication(
    AuthenticationMode Mode,
    string? UserName,
    bool RememberCredentials,
    bool Encrypt,
    bool TrustServerCertificate);
