namespace DbDelta.Shared.Dtos;

/// <summary>
/// Wire form of <see cref="Core.Diff.ComparisonResult"/>.
/// </summary>
public sealed record ComparisonResultDto(IReadOnlyList<DifferenceDto> Differences)
{
    /// <summary>
    /// Families of objects the comparison did not examine. Empty ⇒ the verdict
    /// covers everything both endpoints hold.
    /// </summary>
    /// <remarks>
    /// Structured, so a pipeline can gate on it, and paired with
    /// <see cref="UnexaminedSummary"/> for the human reading the same file.
    /// Init-only rather than positional: a report without the field is still a
    /// valid document, and every hand-built DTO in the tests keeps compiling.
    /// </remarks>
    public IReadOnlyList<UnexaminedGroupDto> Unexamined { get; init; } = [];

    /// <summary>
    /// The same caveat as one sentence, or empty when nothing was skipped.
    /// </summary>
    public string UnexaminedSummary { get; init; } = string.Empty;
}

/// <summary>
/// One family of objects left unexamined, with the label the app and the CLI
/// show, so a consumer of the JSON does not need our lookup table.
/// </summary>
public sealed record UnexaminedGroupDto(string Key, string Label, int Count);
