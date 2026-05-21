using System.Runtime.Versioning;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// macOS Keychain placeholder. Lights up in v2 when the Avalonia
/// cross-platform distribution activates.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class KeychainCredentialStore : ICredentialStore
{
    public bool IsAvailable => false;

    public Task<string?> GetSecretAsync(string targetKey, CancellationToken ct) =>
        throw new System.NotSupportedException("macOS Keychain credential store ships in v2.");

    public Task SetSecretAsync(string targetKey, string secret, CancellationToken ct) =>
        throw new System.NotSupportedException("macOS Keychain credential store ships in v2.");

    public Task DeleteSecretAsync(string targetKey, CancellationToken ct) =>
        throw new System.NotSupportedException("macOS Keychain credential store ships in v2.");
}
