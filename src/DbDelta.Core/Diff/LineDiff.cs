namespace DbDelta.Core.Diff;

/// <summary>
/// A single aligned row in the dual-pane diff. <see cref="SourceText"/> is null
/// for pure inserts; <see cref="TargetText"/> is null for pure deletes. For
/// <see cref="LineStatus.Modified"/> rows both texts are non-null and differ.
/// <see cref="SourceLineNumber"/> / <see cref="TargetLineNumber"/> are 1-based
/// or null if that side has no line for this row.
/// </summary>
public sealed record LineDiff(
    int Index,
    int? SourceLineNumber,
    int? TargetLineNumber,
    string? SourceText,
    string? TargetText,
    LineStatus Status);
