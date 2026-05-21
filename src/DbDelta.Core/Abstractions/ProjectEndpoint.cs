namespace DbDelta.Core.Abstractions;

/// <summary>
/// One side (source or target) of a saved comparison project.
/// Pairs a <see cref="ProjectConnectionRef"/> with its
/// <see cref="ProjectAuthentication"/> settings.
/// </summary>
public sealed record ProjectEndpoint(
    ProjectConnectionRef Connection,
    ProjectAuthentication Authentication);
