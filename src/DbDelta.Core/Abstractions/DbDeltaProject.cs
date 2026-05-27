using DbDelta.Core.Options;

namespace DbDelta.Core.Abstractions;

/// <summary>
/// Shareable artefact: a comparison project.  Schema v2 adds full endpoint
/// definitions, owner/table mappings, per-object selections and fine-grained
/// options.  The v1 legacy members (<see cref="SourceConnectionId"/> /
/// <see cref="TargetConnectionId"/> / <see cref="Options"/> /
/// <see cref="SelectedObjects"/>) are retained for call-site compatibility and
/// the v1 XML read-path.
/// </summary>
public sealed record DbDeltaProject(
    string Name,
    DateTime CreatedUtc = default,
    DateTime LastModifiedUtc = default,
    ProjectEndpoint? Source = null,
    ProjectEndpoint? Target = null,
    IReadOnlyList<OwnerMappingEntry>? OwnerMappings = null,
    IReadOnlyList<TableMappingEntry>? TableMappings = null,
    ProjectOptions? ProjectOptions = null,
    IReadOnlyDictionary<ObjectSelectionKey, bool>? Selections = null,
    Guid SourceConnectionId = default,
    Guid TargetConnectionId = default,
    ComparisonOptions Options = ComparisonOptions.Default,
    IReadOnlyList<string>? SelectedObjects = null)
{
    /// <summary>Owner-mapping list; never null on a fully-loaded project.</summary>
    public IReadOnlyList<OwnerMappingEntry> OwnerMappings { get; init; } =
        OwnerMappings ?? [];

    /// <summary>Table-mapping list; never null on a fully-loaded project.</summary>
    public IReadOnlyList<TableMappingEntry> TableMappings { get; init; } =
        TableMappings ?? [];

    /// <summary>
    /// Fine-grained project options; falls back to
    /// <see cref="ProjectOptions.Default"/>.
    /// </summary>
    public ProjectOptions ProjectOptions { get; init; } =
        ProjectOptions ?? ProjectOptions.Default;

    /// <summary>Per-object include/exclude selections; never null on a fully-loaded project.</summary>
    public IReadOnlyDictionary<ObjectSelectionKey, bool> Selections { get; init; } =
        Selections ?? new Dictionary<ObjectSelectionKey, bool>();
}
