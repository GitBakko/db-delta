using System.Runtime.InteropServices;
using DbDelta.Core.Abstractions;

namespace DbDelta.Persistence.Credentials;

/// <summary>
/// Returns the credential store implementation that matches the current host OS.
/// Throws <see cref="PlatformNotSupportedException"/> on exotic platforms.
/// </summary>
public static class CredentialStoreFactory
{
    public static ICredentialStore Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
#pragma warning disable CA1416
            return new DpapiCredentialStore();
#pragma warning restore CA1416
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
#pragma warning disable CA1416
            return new KeychainCredentialStore();
#pragma warning restore CA1416
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
#pragma warning disable CA1416
            return new SecretServiceCredentialStore();
#pragma warning restore CA1416
        }
        throw new System.PlatformNotSupportedException(
            $"No credential store implementation for {RuntimeInformation.OSDescription}.");
    }
}
