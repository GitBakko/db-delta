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
    private IObjectBodyResolver? _resolver = resolver;

    /// <summary>Replace the body resolver. Called by <see cref="AppStateViewModel"/>
    /// after a successful comparison once the source/target connection strings
    /// are known.</summary>
    public void SetResolver(IObjectBodyResolver? value) => _resolver = value;

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
    /// Loads the diff for the given row: clears previous state, resolves both
    /// SQL bodies, computes diff rows and sections, and sets the initial
    /// section index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Throws whatever the resolver throws. The caller
    /// (<c>AppStateViewModel.LoadDiffAsync</c>) reports it — this used to be a
    /// fire-and-forget call with no catch anywhere, so a resolver failure was an
    /// unobserved exception and nothing on screen changed.
    /// </para>
    /// <para>
    /// Two invariants hold that did not before, and both exist to stop ONE
    /// failure mode: object A's SQL sitting under object B's name. The panes are
    /// cleared BEFORE the first await, not left holding the previous object; and
    /// every pane assignment happens only after BOTH bodies have resolved, so a
    /// failure on the second side cannot leave a half-populated view either.
    /// </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062",
        Justification = "ArgumentNullException.ThrowIfNull is a recognised null guard.")]
    public async Task LoadAsync(DifferenceRowViewModel row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_resolver is null) { return; }

        SourceBody = null;
        TargetBody = null;
        Rows = [];
        Sections = [];
        CurrentSectionIndex = -1;
        ObjectQualifiedName = row.QualifiedName;

        IsLoading = true;
        try
        {
            // No ConfigureAwait(false) here, unlike the rest of the app: every
            // assignment below publishes into ItemsControl bindings, which Avalonia
            // requires on the UI thread. These were the only two view-model awaits
            // in the app that continued on the thread pool.
            string? source = await _resolver.ResolveSourceBodyAsync(row.Kind, row.SchemaName, row.ObjectName, ct);
            string? target = await _resolver.ResolveTargetBodyAsync(row.Kind, row.SchemaName, row.ObjectName, ct);
            ct.ThrowIfCancellationRequested();

            SourceBody = source;
            TargetBody = target;
            Rows = LineDiffer.Compute(source, target);
            Sections = LineDiffer.SectionsFrom(Rows);
            CurrentSectionIndex = Sections.Count > 0 ? 0 : -1;

            if (CurrentSectionIndex >= 0)
            {
                NavigateToRowRequested?.Invoke(this, Sections[0].StartIndex);
            }
        }
        finally
        {
            // A superseded load must not switch the spinner off under the load
            // that replaced it: arrow-keying the grid starts one per row.
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
            }
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
