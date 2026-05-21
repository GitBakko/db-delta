using DbDelta.Core.Options;

namespace DbDelta.Core.Abstractions;

/// <summary>
/// Shareable artefact: a comparison project. References two
/// <see cref="ConnectionEntry"/> ids (kept stable across rename).
/// </summary>
public sealed record DbDeltaProject(
    string Name,
    Guid SourceConnectionId,
    Guid TargetConnectionId,
    ComparisonOptions Options,
    IReadOnlyList<string>? SelectedObjects = null);
