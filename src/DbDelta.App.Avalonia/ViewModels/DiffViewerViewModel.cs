using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Drives the dual-pane SQL diff viewer. Loads per-object bodies via an
/// <see cref="IObjectBodyResolver"/> and exposes the computed diff rows,
/// diff sections, and section-navigation commands to the view.
/// </summary>
public sealed partial class DiffViewerViewModel(IObjectBodyResolver? resolver = null) : ObservableObject
{
    [ObservableProperty]
    private string? _sourceBody;

    [ObservableProperty]
    private string? _targetBody;

    [ObservableProperty]
    private IReadOnlyList<LineDiff> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<DiffSection> _sections = [];

    [ObservableProperty]
    private int _currentSectionIndex = -1;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _objectQualifiedName;

    /// <summary>True when there is at least one diff row to display.</summary>
    public bool HasContent => Rows.Count > 0;

    partial void OnRowsChanged(IReadOnlyList<LineDiff> value) =>
        OnPropertyChanged(nameof(HasContent));

    /// <summary>
    /// Loads the diff for the given row. Clears previous state, resolves
    /// both SQL bodies, computes diff rows and sections, and sets the initial
    /// section index.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062",
        Justification = "ArgumentNullException.ThrowIfNull is a recognised null guard.")]
    public async Task LoadAsync(DifferenceRowViewModel row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (resolver is null) { return; }

        IsLoading = true;
        try
        {
            ObjectQualifiedName = row.QualifiedName;
            SourceBody = await resolver.ResolveSourceBodyAsync(row.Kind, row.SchemaName, row.ObjectName, ct)
                .ConfigureAwait(false);
            TargetBody = await resolver.ResolveTargetBodyAsync(row.Kind, row.SchemaName, row.ObjectName, ct)
                .ConfigureAwait(false);
            Rows = LineDiffer.Compute(SourceBody, TargetBody);
            Sections = LineDiffer.SectionsFrom(Rows);
            CurrentSectionIndex = Sections.Count > 0 ? 0 : -1;

            if (CurrentSectionIndex >= 0)
            {
                NavigateToRowRequested?.Invoke(this, Sections[0].StartIndex);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Navigate to the next diff section, clamping at the last.</summary>
    [RelayCommand]
    public void NextSection()
    {
        if (Sections.Count == 0) { return; }
        CurrentSectionIndex = Math.Min(CurrentSectionIndex + 1, Sections.Count - 1);
        NavigateToRowRequested?.Invoke(this, Sections[CurrentSectionIndex].StartIndex);
    }

    /// <summary>Navigate to the previous diff section, clamping at zero.</summary>
    [RelayCommand]
    public void PreviousSection()
    {
        if (Sections.Count == 0) { return; }
        CurrentSectionIndex = Math.Max(CurrentSectionIndex - 1, 0);
        NavigateToRowRequested?.Invoke(this, Sections[CurrentSectionIndex].StartIndex);
    }

    /// <summary>
    /// Raised when the view should scroll both panes to the given aligned-row index.
    /// </summary>
    public event EventHandler<int>? NavigateToRowRequested;
}
