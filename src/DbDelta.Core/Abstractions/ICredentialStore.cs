namespace DbDelta.Core.Abstractions;

/// <summary>
/// Per-host secret store. Implementations: Windows DPAPI (ships), macOS
/// Keychain + Linux Secret Service (placeholders until the Avalonia
/// cross-platform distribution is live).
/// </summary>
public interface ICredentialStore
{
    /// <summary>True when this backend can actually persist secrets on the current host.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns the stored secret, or <c>null</c> when not present.</summary>
    Task<string?> GetSecretAsync(string targetKey, CancellationToken ct);

    /// <summary>Creates or overwrites a secret under <paramref name="targetKey"/>.</summary>
    Task SetSecretAsync(string targetKey, string secret, CancellationToken ct);

    /// <summary>Removes a secret. No-op when absent.</summary>
    Task DeleteSecretAsync(string targetKey, CancellationToken ct);
}
