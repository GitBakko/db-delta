using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DbDelta.Core.Abstractions;
using Meziantou.Framework.Win32;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// Windows DPAPI-backed credential store. Uses
/// <see cref="CredentialManager"/> with LocalMachine persistence — secrets
/// are encrypted with the user's logon key.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiCredentialStore : ICredentialStore
{
    public bool IsAvailable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [SupportedOSPlatform("windows")]
    public Task<string?> GetSecretAsync(string targetKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
#pragma warning disable CA1416
        Credential? cred = CredentialManager.ReadCredential(targetKey);
#pragma warning restore CA1416
        return Task.FromResult(cred?.Password);
    }

    [SupportedOSPlatform("windows")]
    public Task SetSecretAsync(string targetKey, string secret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentNullException.ThrowIfNull(secret);
#pragma warning disable CA1416
        CredentialManager.WriteCredential(
            applicationName: targetKey,
            userName: "dbdelta",
            secret: secret,
            persistence: CredentialPersistence.LocalMachine);
#pragma warning restore CA1416
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    public Task DeleteSecretAsync(string targetKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
#pragma warning disable CA1416
        CredentialManager.DeleteCredential(targetKey);
#pragma warning restore CA1416
        return Task.CompletedTask;
    }
}
