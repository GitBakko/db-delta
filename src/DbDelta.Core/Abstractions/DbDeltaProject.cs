using DbDelta.Core.Options;

namespace DbDelta.Core.Abstractions;

/// <summary>
/// Shareable artefact: a comparison project. Schema v2 adds full endpoint
/// definitions and per-object selections. The v1 legacy members
/// (<see cref="SourceConnectionId"/> / <see cref="TargetConnectionId"/> /
/// <see cref="Options"/> / <see cref="SelectedObjects"/>) are retained for
/// call-site compatibility and the v1 XML read-path.
/// </summary>
/// <remarks>
/// It also carried owner/table mappings and a ProjectOptions record, saved to
/// and read from the <c>.dbd</c> and consulted by nothing: no engine, no
/// generator, not even the project dialog, which never showed them. Deleted on
/// 2026-08-20 by the owner's call. A <c>.dbd</c> written before that still
/// loads — the reader ignores elements it does not know — and loses those
/// elements the next time it is saved.
/// </remarks>
public sealed record DbDeltaProject(
    string Name,
    DateTime CreatedUtc = default,
    DateTime LastModifiedUtc = default,
    ProjectEndpoint? Source = null,
    ProjectEndpoint? Target = null,
    IReadOnlyDictionary<ObjectSelectionKey, bool>? Selections = null,
    Guid SourceConnectionId = default,
    Guid TargetConnectionId = default,
    ComparisonOptions Options = ComparisonOptions.Default,
    IReadOnlyList<string>? SelectedObjects = null)
{
    /// <summary>Per-object include/exclude selections; never null on a fully-loaded project.</summary>
    public IReadOnlyDictionary<ObjectSelectionKey, bool> Selections { get; init; } =
        Selections ?? new Dictionary<ObjectSelectionKey, bool>();
}
