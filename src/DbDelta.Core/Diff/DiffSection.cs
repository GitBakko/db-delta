namespace DbDelta.Core.Diff;

/// <summary>
/// A contiguous run of non-Unchanged rows. Used by the minimap to render
/// blue rectangles on the left of each pane.
/// </summary>
public sealed record DiffSection(int StartIndex, int Length)
{
    public int EndIndex => StartIndex + Length - 1;
}
