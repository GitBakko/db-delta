using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Providers.LiveDb;
using DbDelta.Shared.Dtos;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Single-source-of-truth state for the comparison flow. Mirrors the old
/// <c>DbDelta.App.State.AppState</c> but uses CommunityToolkit MVVM
/// notifications + a <see cref="RelayCommand"/> for the Compare action.
/// </summary>
public sealed partial class AppStateViewModel : ObservableObject
{
    public AppStateViewModel(ConnectionStoreViewModel? connections = null)
    {
        Connections = connections;
        SourceSlot = new ConnectionPickerSlot(this, isSource: true);
        TargetSlot = new ConnectionPickerSlot(this, isSource: false);
    }

    public ConnectionStoreViewModel? Connections { get; }
    public ConnectionPickerSlot SourceSlot { get; }
    public ConnectionPickerSlot TargetSlot { get; }

    [ObservableProperty]
    private string _sourceConnectionString = "";

    [ObservableProperty]
    private string _targetConnectionString = "";

    /// <summary>
    /// The project the user confirmed in the setup modal — non-null after OK,
    /// carries the server/database names used by the top header strip and the
    /// project header. Independent from <see cref="SourceConnectionString"/>
    /// (which is the runtime-only string with the live password).
    /// </summary>
    [ObservableProperty]
    private DbDeltaProject? _currentProject;

    /// <summary>IPv4 address of the source server (when known — populated from
    /// the modal scan list or DNS fallback). Shown in the project header strip.</summary>
    [ObservableProperty]
    private string? _sourceServerIp;

    /// <summary>IPv4 address of the target server (when known).</summary>
    [ObservableProperty]
    private string? _targetServerIp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwapCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private ComparisonResultDto? _lastComparison;

    /// <summary>
    /// The raw <see cref="ComparisonResult"/> from the most recent comparison run.
    /// Carries the typed <see cref="DifferencePair"/> objects needed by
    /// <c>DeployScriptBuilder</c> and <c>SqlExecutor</c>.
    /// </summary>
    [ObservableProperty]
    private ComparisonResult? _lastComparisonRaw;

    /// <summary>
    /// Source-side dependency edges from the most recent comparison. Required by
    /// the deploy-script generator: with an empty edge list the topological sort
    /// degenerates to kind-then-alphabetical order, which emits a new view
    /// before the new function it selects from (Msg 208).
    /// </summary>
    public IReadOnlyList<DependencyEdge> SourceDependencies { get; private set; } = [];

    /// <summary>
    /// Target-side dependency edges from the most recent comparison. Not used by
    /// the forward script; retained because an inverse (rollback) script has to
    /// be ordered by the dependencies of the side it restores.
    /// </summary>
    public IReadOnlyList<DependencyEdge> TargetDependencies { get; private set; } = [];

    /// <summary>
    /// Active status filter for the result grid. <c>null</c> = no filter.
    /// Bound options match the raw <see cref="DifferenceDto.Status"/> string
    /// values ("Different" / "OnlyInA" / "OnlyInB" / "Identical").
    /// </summary>
    [ObservableProperty]
    private string? _statusFilter;

    /// <summary>
    /// Filter options shown in the picker — value is the raw Status string,
    /// label is the Italian display name.
    /// </summary>
    public IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new(null,         "Tutti"),
        new("Different",  "Modificato"),
        new("OnlyInA",    "Solo in origine"),
        new("OnlyInB",    "Solo in destinazione"),
        new("Identical",  "Identico"),
    ];

    /// <summary>
    /// Differences filtered by the current <see cref="StatusFilter"/>.
    /// Recomputed whenever LastComparison or StatusFilter change.
    /// </summary>
    public IReadOnlyList<DifferenceDto> FilteredDifferences =>
        LastComparison is null
            ? []
            : (StatusFilter is null
                ? LastComparison.Differences
                : [.. LastComparison.Differences.Where(d => string.Equals(d.Status, StatusFilter, StringComparison.Ordinal))]);

    partial void OnLastComparisonChanged(ComparisonResultDto? value) => OnPropertyChanged(nameof(FilteredDifferences));
    partial void OnStatusFilterChanged(string? value) => OnPropertyChanged(nameof(FilteredDifferences));

    /// <summary>
    /// The row currently selected in the results grid. When set, triggers an
    /// async body load in <see cref="DiffViewer"/>.
    /// </summary>
    [ObservableProperty]
    private DifferenceRowViewModel? _selectedRow;

    partial void OnSelectedRowChanged(DifferenceRowViewModel? value)
    {
        OnPropertyChanged(nameof(ShouldShowDiffViewer));
        if (value is null) { return; }
        // Identical rows have nothing to diff — skip the viewer activation so
        // the bottom pane stays collapsed.
        if (value.IsIdentical) { return; }
        _ = DiffViewer.LoadAsync(value, CancellationToken.None);
    }

    /// <summary>True when the diff viewer panel should be visible. Identical
    /// rows are excluded — there is nothing to diff so the pane stays hidden
    /// even though a row is "selected" in the grid.</summary>
    public bool ShouldShowDiffViewer =>
        SelectedRow is not null && !SelectedRow.IsIdentical;

    /// <summary>
    /// Diff viewer state. The resolver is injected by DI in the real app; in
    /// tests or design-time the resolver is null and Load is a no-op.
    /// </summary>
    public DiffViewerViewModel DiffViewer { get; } = new(resolver: null);

    public string StatusText => IsBusy ? "Working…" : "Ready";

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    /// <summary>
    /// Describes a connection-string parse failure without echoing the string.
    /// </summary>
    /// <remarks>
    /// This message is shown in the shell's error banner, so it must never
    /// carry a value. It previously embedded the whole string with passwords
    /// masked by a <c>(password|pwd)\s*=\s*[^;]+</c> regex — which stops at the
    /// first <c>;</c>, so a quoted password containing one (legal, and common in
    /// generated passwords) left its tail in plain sight, and any other
    /// secret-bearing keyword was not covered at all. Key NAMES are safe and
    /// keep the diagnostic value; values are never included.
    /// </remarks>
    private static string DescribeParseFailure(string side, string connectionString, Exception parseEx)
    {
        IEnumerable<string> keys = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.IndexOf('=', StringComparison.Ordinal) is int eq && eq > 0
                ? t[..eq].Trim()
                : "(senza '=')")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        int ctrlIdx = connectionString
            .Select((ch, i) => (ch, i))
            .FirstOrDefault(x => char.IsControl(x.ch)).i;
        bool hasCtrl = connectionString.Any(char.IsControl);

        string ctrl = hasCtrl
            ? $"\nCarattere di controllo alla posizione {ctrlIdx} — probabile newline incollata."
            : string.Empty;

        return $"Stringa di connessione di {side} non valida: {parseEx.Message}"
             + $"\nLunghezza {connectionString.Length}, chiavi lette: {string.Join(", ", keys)}."
             + ctrl;
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    public async Task CompareAsync(CancellationToken ct)
    {
        IsBusy = true;
        LastError = null;
        try
        {
            // Trim defensively — pasted connection strings often carry a trailing
            // newline or stray whitespace from markdown / IDE clipboards that the
            // strict SqlConnectionStringBuilder parser rejects with the unhelpful
            // "Format of the initialization string does not conform to specification" error.
            string srcCs = (SourceConnectionString ?? string.Empty).Trim();
            string tgtCs = (TargetConnectionString ?? string.Empty).Trim();

            // Report enough to diagnose a bad paste (a stray newline, markdown
            // ** that survived the clipboard) WITHOUT echoing the string.
            try
            {
                _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(srcCs);
            }
            catch (Exception parseEx)
            {
                LastError = DescribeParseFailure("provenienza", srcCs, parseEx);
                return;
            }
            try
            {
                _ = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(tgtCs);
            }
            catch (Exception parseEx)
            {
                LastError = DescribeParseFailure("destinazione", tgtCs, parseEx);
                return;
            }

            LiveDbSource src = new(srcCs, "source");
            LiveDbSource tgt = new(tgtCs, "target");

            Result<Database> srcRes = await src.LoadAsync(ct).ConfigureAwait(true);
            if (!srcRes.IsSuccess)
            {
                LastError = srcRes.Error!.Message;
                return;
            }

            Result<Database> tgtRes = await tgt.LoadAsync(ct).ConfigureAwait(true);
            if (!tgtRes.IsSuccess)
            {
                LastError = tgtRes.Error!.Message;
                return;
            }

            ComparisonEngine engine = new();
            ComparisonResult result = engine.Compare(srcRes.Value!, tgtRes.Value!, ComparisonOptions.Default);
            LastComparisonRaw = result;
            LastComparison = Mapper.ToDto(result);

            // Keep the dependency edges: without them the deploy script the app
            // builds has NO topological ordering and degenerates to KindRank, so
            // a new view over a new function emits in the wrong order and fails.
            // Both sides are retained — the target's are what an inverse
            // (rollback) script would need.
            SourceDependencies = srcRes.Value!.Dependencies;
            TargetDependencies = tgtRes.Value!.Dependencies;

            // Wire the diff viewer with a live resolver bound to the same
            // source/target connection strings so the bottom pane can fetch
            // per-object SQL bodies on row selection.
            DiffViewer.SetResolver(new Providers.LiveDb.ObjectBody.LiveDbObjectBodyResolver(srcCs, tgtCs));

            if (Connections is not null)
            {
                await Connections.AutosaveAsync(srcCs, ct).ConfigureAwait(true);
                await Connections.AutosaveAsync(tgtCs, ct).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCompare() => !IsBusy
        && !string.IsNullOrWhiteSpace(SourceConnectionString)
        && !string.IsNullOrWhiteSpace(TargetConnectionString);

    [RelayCommand(CanExecute = nameof(CanSwap))]
    public void Swap() => (SourceConnectionString, TargetConnectionString) = (TargetConnectionString, SourceConnectionString);

    private bool CanSwap() => !IsBusy;

    partial void OnSourceConnectionStringChanged(string value) => CompareCommand.NotifyCanExecuteChanged();
    partial void OnTargetConnectionStringChanged(string value) => CompareCommand.NotifyCanExecuteChanged();
}
