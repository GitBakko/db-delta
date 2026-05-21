namespace DbDelta.Core.Abstractions;

/// <summary>
/// A user-saved connection. The password is NOT carried here — it lives in
/// the paired <see cref="ICredentialStore"/> under the key
/// <c>"dbdelta:connection:{Id:D}"</c>.
/// </summary>
public sealed record ConnectionEntry(
    Guid Id,
    string Name,
    string ServerName,
    string DatabaseName,
    string ConnectionStringTemplate,
    string EnvironmentTag,
    string EnvironmentColorHex,
    bool IsPinned,
    DateTime CreatedUtc,
    DateTime LastUsedUtc);
