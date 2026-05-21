using System.Runtime.Versioning;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// Linux libsecret / Secret Service placeholder. Lights up in v2.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class SecretServiceCredentialStore : ICredentialStore
{
    public bool IsAvailable => false;

    public Task<string?> GetSecretAsync(string targetKey, CancellationToken ct) =>
        throw new NotSupportedException("Linux Secret Service credential store ships in v2.");

    public Task SetSecretAsync(string targetKey, string secret, CancellationToken ct) =>
        throw new NotSupportedException("Linux Secret Service credential store ships in v2.");

    public Task DeleteSecretAsync(string targetKey, CancellationToken ct) =>
        throw new NotSupportedException("Linux Secret Service credential store ships in v2.");
}
