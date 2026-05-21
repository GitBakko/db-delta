namespace DbDelta.Core.Abstractions;

/// <summary>
/// A lightweight, self-contained reference to a connection saved in the
/// project file.  The credential is NOT stored here.
/// </summary>
public sealed record ProjectConnectionRef(
    Guid Id,
    string Name,
    string ServerName,
    string DatabaseName,
    string EnvironmentTag,
    string EnvironmentColorHex);
