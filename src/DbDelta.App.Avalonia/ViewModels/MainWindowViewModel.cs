using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.Reports;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Json;
using DbDelta.Persistence.Sql;
using DbDelta.Persistence.Util;
using DbDelta.Shared.Dtos;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Root view-model for the main window. Holds the single
/// <see cref="AppStateViewModel"/> shared by all child views, plus
/// window-chrome state (sidebar collapsed, theme variant) and the
/// Red-Gate-style results grid state (search, grouping, deploy).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly JsonRecentProjectsStore _recentProjects;
    private readonly ICredentialStore? _credentials;
    private readonly JsonUiSettingsStore? _uiSettings;

    public MainWindowViewModel(AppStateViewModel appState)
        : this(appState, JsonRecentProjectsStore.CreateDefault(), credentials: null)
    {
    }

    /// <param name="appState">Shared application state.</param>
    /// <param name="recentProjects">MRU store behind the topbar project combo.</param>
    /// <param name="credentials">Credential store, or null when unavailable.</param>
    /// <param name="uiSettings">
    /// Backing store for the theme preference. Null keeps the choice
    /// session-only, which is what the short overload above wants.
    /// </param>
    /// <param name="initialTheme">
    /// The theme already applied to the application by startup. Passed in
    /// rather than loaded here so the window is never built under the wrong
    /// variant and repainted a frame later.
    /// </param>
    public MainWindowViewModel(
        AppStateViewModel appState,
        JsonRecentProjectsStore recentProjects,
        ICredentialStore? credentials,
        JsonUiSettingsStore? uiSettings = null,
        AppTheme initialTheme = AppTheme.System)
    {
        AppState = appState;
        _recentProjects = recentProjects;
        _credentials = credentials;
        _uiSettings = uiSettings;
        _theme = initialTheme;
        AppState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppStateViewModel.LastComparison))
            {
                RebuildRows();
                // The report needs a comparison and nothing else, so it turns on
                // here rather than with the selection the deploy buttons watch.
                SaveReportCommand.NotifyCanExecuteChanged();
            }
            else if (e.PropertyName is nameof(AppStateViewModel.TargetConnectionString)
                                    or nameof(AppStateViewModel.ResultsAreStale))
            {
                // Both commands, not just this one: DeployAsync writes a script
                // built from the same stale pairs, and a script saved against
                // the wrong server is executed by hand later with nobody left to
                // notice.
                DeployCommand.NotifyCanExecuteChanged();
                ExecuteOnTargetCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ResultsAreStale));
            }
        };
        Rows.CollectionChanged += (_, _) =>
        {
            DeployCommand.NotifyCanExecuteChanged();
            ExecuteOnTargetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionSummary));
        };

        // Fire-and-forget initial MRU load; failures are silent (empty combo).
        _ = RefreshProjectMruAsync();

        // Live refresh: any AddOrTouchAsync (modal save, EditProject, ...)
        // fires this static event so the topbar combo updates without
        // requiring an app restart.
        JsonRecentProjectsStore.RecentProjectsChanged += OnRecentProjectsChanged;
    }

    private async void OnRecentProjectsChanged(object? sender, EventArgs e) => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshProjectMruAsync).ConfigureAwait(true);

    /// <summary>
    /// Reloads <see cref="ProjectMru"/> from the JSON store. Called on startup
    /// and after every save / open so the topbar combo reflects the latest
    /// recent-files list.
    /// </summary>
    private async Task RefreshProjectMruAsync()
    {
        try
        {
            IReadOnlyList<RecentProject> list =
                await _recentProjects.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            ProjectMru.Clear();
            foreach (RecentProject e in list)
            {
                ProjectMru.Add(e);
            }
        }
        catch
        {
            // Best-effort — leaving the combo empty is acceptable.
        }
    }

    /// <summary>Count of rows with <c>Different</c> status (modified on both sides).</summary>
    public int DiffsModifiedCount => Rows.Count(r => r.Status == "Different");

    /// <summary>Count of rows present only on the target side.</summary>
    public int DiffsOnlyDestCount => Rows.Count(r => r.IsTargetOnly);

    /// <summary>Count of rows present only on the source side.</summary>
    public int DiffsOnlyProvCount => Rows.Count(r => r.IsSourceOnly);

    /// <summary>Count of rows that match on both sides (no diff to deploy).</summary>
    public int IdenticalsCount => Rows.Count(r => r.IsIdentical);

    /// <summary>Total differences excluding identical rows.</summary>
    public int TotalDiffsCount => Rows.Count(r => !r.IsIdentical);

    /// <summary>Count of currently selected rows in the grid.</summary>
    public int SelectedCount => Rows.Count(r => r.IsSelected);

    /// <summary>
    /// Header text shown to the left of the per-type badges in the results
    /// action bar. Examples: "Nessuna differenza." /
    /// "12 differenze rilevate" / "3 di 12 differenze selezionate".
    /// </summary>
    public string SelectionSummary
    {
        get
        {
            int total = TotalDiffsCount;
            int selected = SelectedCount;
            return total == 0 && IdenticalsCount == 0
                ? "Nessuna differenza."
                : selected == 0
                    ? $"{total} differenze rilevate"
                    : $"{selected} di {total} differenze selezionate";
        }
    }

    /// <summary>
    /// Called by <see cref="DifferenceRowViewModel"/> whenever its
    /// <c>IsSelected</c> flips, so the action bar counter and the deploy /
    /// execute can-execute states refresh.
    /// </summary>
    internal void OnRowSelectionChanged()
    {
        // A bulk toggle flips hundreds of rows in a loop; letting each one
        // re-run the counters and both can-execute probes is quadratic and
        // visibly janky on a real comparison. The bulk commands raise this
        // once at the end instead.
        if (_bulkUpdating) { return; }
        DeployCommand.NotifyCanExecuteChanged();
        ExecuteOnTargetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(AllVisibleSelected));
    }

    private bool _bulkUpdating;

    /// <summary>
    /// The rows a bulk selection command acts on: everything the grid is
    /// currently showing, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately filtered by <see cref="SearchPredicate"/> — the same
    /// predicate the view uses — so "seleziona tutto" can never tick a row the
    /// user cannot see. A hidden row silently added to the selection becomes a
    /// statement in the deploy script that nobody reviewed. Identical rows are
    /// excluded because they are not selectable at all.
    /// </remarks>
    private IEnumerable<DifferenceRowViewModel> VisibleSelectableRows() =>
        Rows.Where(r => r.IsSelectable && SearchPredicate(r));

    /// <summary>
    /// Tri-state for the "select all" box: <see langword="true"/> when every
    /// visible row is ticked, <see langword="false"/> when none is, and
    /// <see langword="null"/> when only some are.
    /// </summary>
    public bool? AllVisibleSelected
    {
        get
        {
            int total = 0;
            int selected = 0;
            foreach (DifferenceRowViewModel row in VisibleSelectableRows())
            {
                total++;
                if (row.IsSelected) { selected++; }
            }
            return total == 0 || selected == 0
                ? false
                : selected == total ? true : null;
        }
    }

    /// <summary>
    /// Ticks every visible row, or clears them when they are already all ticked.
    /// </summary>
    [RelayCommand]
    private void ToggleAllVisible() =>
        // Partially selected counts as "not all", so the first click completes
        // the selection rather than throwing away what the user already ticked.
        SetSelection(VisibleSelectableRows(), AllVisibleSelected != true);

    /// <summary>
    /// Ticks every row of one grid group, or clears them when they are already
    /// all ticked. Bound from the group header, whose data context is the
    /// group itself.
    /// </summary>
    [RelayCommand]
    private void ToggleGroup(object? group)
    {
        if (group is not DataGridCollectionViewGroup g) { return; }
        List<DifferenceRowViewModel> rows =
            [.. g.Items.OfType<DifferenceRowViewModel>().Where(r => r.IsSelectable)];
        if (rows.Count == 0) { return; }
        SetSelection(rows, !rows.TrueForAll(r => r.IsSelected));
    }

    private void SetSelection(IEnumerable<DifferenceRowViewModel> rows, bool selected)
    {
        _bulkUpdating = true;
        try
        {
            foreach (DifferenceRowViewModel row in rows) { row.IsSelected = selected; }
        }
        finally
        {
            _bulkUpdating = false;
        }
        OnRowSelectionChanged();
    }

    public AppStateViewModel AppState { get; }

    /// <summary>
    /// Display version, bound by both the topbar banner and the status-bar
    /// pill (e.g. "v1.0.0-rc1"); resolved from the publish-stamped assembly
    /// version. Replaced the hardcoded "v0.1 alpha" banner string.
    /// </summary>
    public string AppVersion => AppVersionInfo.Display;

    [RelayCommand]
    private void OpenVersionHistory()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppVersionInfo.HistoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Impossibile aprire la version history: {ex.Message}";
            Debug.WriteLine($"Failed to open version history: {ex.Message}");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeLight))]
    [NotifyPropertyChangedFor(nameof(IsThemeDark))]
    [NotifyPropertyChangedFor(nameof(IsThemeSystem))]
    [NotifyPropertyChangedFor(nameof(ThemeTooltip))]
    private AppTheme _theme;

    /// <summary>True when the shell is pinned to the light variant.</summary>
    public bool IsThemeLight => Theme == AppTheme.Light;

    /// <summary>True when the shell is pinned to the dark variant.</summary>
    public bool IsThemeDark => Theme == AppTheme.Dark;

    /// <summary>True when the shell follows the operating system.</summary>
    public bool IsThemeSystem => Theme == AppTheme.System;

    /// <summary>
    /// Tooltip for the topbar theme button. One button carries three states, so
    /// the tooltip is what names the current one.
    /// </summary>
    public string ThemeTooltip => Theme switch
    {
        AppTheme.Light => "Tema: chiaro",
        AppTheme.Dark => "Tema: scuro",
        AppTheme.System => "Tema: sistema",
        _ => "Tema: sistema",
    };

    /// <summary>
    /// Maps a stored preference onto the Avalonia variant. Shared with
    /// <c>App.OnFrameworkInitializationCompleted</c>, which applies the
    /// persisted theme before the main window is built.
    /// </summary>
    /// <remarks>
    /// <see cref="AppTheme.System"/> maps to <see cref="ThemeVariant.Default"/>
    /// and NOT to Light: Default is the only value that lets Avalonia resolve
    /// the variant from the OS setting.
    /// </remarks>
    public static ThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        AppTheme.System => ThemeVariant.Default,
        _ => ThemeVariant.Default,
    };

    [ObservableProperty]
    private string? _projectFilePath;

    /// <summary>
    /// Advances the theme one step: Chiaro → Scuro → Sistema → Chiaro.
    /// </summary>
    [RelayCommand]
    public async Task CycleThemeAsync()
    {
        Theme = Theme switch
        {
            AppTheme.Light => AppTheme.Dark,
            AppTheme.Dark => AppTheme.System,
            AppTheme.System => AppTheme.Light,
            _ => AppTheme.Light,
        };

        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = ToVariant(Theme);
        }

        if (_uiSettings is null) { return; }
        try
        {
            await _uiSettings.SaveThemeAsync(Theme, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference we could not write down is not worth interrupting the
            // user over — the theme they clicked is already applied on screen.
            Debug.WriteLine($"Failed to persist theme: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task SaveProjectAsync(Window? window)
    {
        if (window is null) { return; }
        if (AppState.CurrentProject is null)
        {
            AppState.LastError = "Nessun progetto attivo da salvare. Carica o crea un progetto prima.";
            return;
        }

        // Name-only dialog — path is computed under %LOCALAPPDATA%\DbDelta\Projects
        // (user feedback round 7: don't pop the OS save-file picker, just ask
        // for the project name).
        Views.SaveProjectDialog dialog = new();
        dialog.SetInitialName(AppState.CurrentProject.Name ?? "Nuovo progetto");
        dialog.SetHint($"Verrà salvato in: {ProjectsFolder.GetOrCreate()}");
        string? name = await dialog.ShowDialog<string?>(window).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) { return; }

        string path = ProjectsFolder.ResolvePath(name);

        // Snapshot current grid selection into the project's Selections map so
        // it round-trips on reload (matches schema v2 contract).
        Dictionary<ObjectSelectionKey, bool> selections = [];
        foreach (DifferenceRowViewModel row in Rows.Where(r => r.IsSelectable))
        {
            selections[new ObjectSelectionKey(row.Kind, row.SchemaName, row.ObjectName)] = row.IsSelected;
        }

        DbDeltaProject p = AppState.CurrentProject;
        DbDeltaProject toSave = p with
        {
            Name = name,
            LastModifiedUtc = DateTime.UtcNow,
            Selections = selections.ToFrozenDictionary(),
        };

        Persistence.Xml.XmlProjectStore store = new();
        await store.SaveAsync(path, toSave, CancellationToken.None).ConfigureAwait(true);
        AppState.CurrentProject = toSave;
        ProjectFilePath = path;

        await _recentProjects.AddOrTouchAsync(path, CancellationToken.None).ConfigureAwait(true);
        await RefreshProjectMruAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task OpenConnectionManagerAsync(Window? owner)
    {
        if (owner is null || AppState.Connections is null)
        {
            return;
        }
        Views.ConnectionManagerDialog dialog = new() { DataContext = AppState.Connections };
        await dialog.ShowDialog(owner);
    }

    [RelayCommand]
    public async Task OpenProjectAsync(Window? window)
    {
        if (window is null) { return; }

        // Round-7 UX: show MRU list + browse-from-disk option, instead of
        // jumping straight into the OS file picker.
        IReadOnlyList<RecentProject> recents =
            await _recentProjects.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        Views.LoadProjectDialog dialog = new();
        dialog.SetRecentProjects(recents);

        string? path = await dialog.ShowDialog<string?>(window).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) { return; }

        await LoadProjectFromPathAsync(window, path).ConfigureAwait(true);
    }

    /// <summary>
    /// Shared open-from-disk flow used by both <see cref="OpenProjectAsync"/>
    /// and the MRU dropdown. Loads the .dbd, opens the setup dialog
    /// pre-populated with the project endpoints (so DPAPI auto-fill can
    /// recover saved passwords), and on OK swaps the new project into
    /// AppState + restores the grid selection state.
    /// </summary>
    private async Task LoadProjectFromPathAsync(Window window, string path)
    {
        Persistence.Xml.XmlProjectStore store = new();
        DbDeltaProject project;
        try
        {
            project = await store.LoadAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppState.LastError = $"Impossibile caricare il progetto: {ex.Message}";
            return;
        }

        // Reuse the setup dialog so the user can confirm credentials (DPAPI
        // auto-fill restores passwords for RememberCredentials endpoints).
        var vm = ProjectSetupViewModel.FromProject(project, _credentials);
        vm.SeedRecentServersFrom(AppState.Connections?.Entries);
        Views.ProjectSetupDialog dialog = new() { DataContext = vm };

        DbDeltaProject? result =
            await dialog.ShowDialog<DbDeltaProject?>(window).ConfigureAwait(true);
        if (result is null) { return; }

        // Carry over the pre-existing Selections so user-marked rows survive
        // a reload (the rebuilt grid will re-apply them on the next compare).
        DbDeltaProject merged = result with { Selections = project.Selections };

        AppState.SourceConnectionString = dialog.LastSourceConnectionString ?? string.Empty;
        AppState.TargetConnectionString = dialog.LastTargetConnectionString ?? string.Empty;
        AppState.CurrentProject = merged;
        ProjectFilePath = path;

        if (!string.IsNullOrWhiteSpace(AppState.SourceConnectionString)
            && !string.IsNullOrWhiteSpace(AppState.TargetConnectionString))
        {
            await AppState.CompareCommand.ExecuteAsync(CancellationToken.None).ConfigureAwait(true);
            ReapplySavedSelections(project.Selections);
        }

        await _recentProjects.AddOrTouchAsync(path, CancellationToken.None).ConfigureAwait(true);
        await RefreshProjectMruAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Restores saved per-row IsSelected flags after a comparison rebuild.
    /// Keys match by (Kind, Schema, Name); rows missing from the saved map
    /// stay at their current state.
    /// </summary>
    private void ReapplySavedSelections(IReadOnlyDictionary<ObjectSelectionKey, bool> saved)
    {
        if (saved.Count == 0) { return; }
        foreach (DifferenceRowViewModel row in Rows)
        {
            ObjectSelectionKey key = new(row.Kind, row.SchemaName, row.ObjectName);
            if (saved.TryGetValue(key, out bool sel))
            {
                row.IsSelected = sel;
            }
        }
    }

    /// <summary>Opens the project at the given path. Invoked when the user
    /// picks an entry from the topbar MRU combo. Accepts a tuple so XAML
    /// (which cannot easily compose multi-arg CommandParameters) can still
    /// pass both the owning Window and the chosen file path.</summary>
    [RelayCommand]
    public async Task OpenRecentProjectAsync((Window? Window, string? Path) args)
    {
        if (args.Window is null || string.IsNullOrWhiteSpace(args.Path)) { return; }
        if (!File.Exists(args.Path))
        {
            AppState.LastError = $"Il file '{args.Path}' non esiste più. Verrà rimosso dalla lista.";
            await RefreshProjectMruAsync().ConfigureAwait(true);
            return;
        }
        await LoadProjectFromPathAsync(args.Window, args.Path).ConfigureAwait(true);
    }

    // ── Results grid state ───────────────────────────────────────────────────

    /// <summary>
    /// Recent project entries shown in the "I miei progetti" topbar combo.
    /// Each entry carries the full path + save-date so the dropdown can show
    /// both filename and "salvato il …" timestamp.
    /// </summary>
    public ObservableCollection<RecentProject> ProjectMru { get; } = [];

    /// <summary>Grouping options shown in the topbar ComboBox.</summary>
    public IReadOnlyList<string> GroupingOptions { get; } =
    [
        "Tipo di differenza",
        "Tipo di oggetto",
        "Nessun gruppo",
    ];

    /// <summary>Text entered in the topbar search box.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// Selected grouping mode. One of:
    /// "Tipo di differenza" / "Tipo di oggetto" / "Nessun gruppo".
    /// Default groups by difference type so the user sees buckets immediately.
    /// </summary>
    [ObservableProperty]
    private string _groupingMode = "Tipo di differenza";

    /// <summary>Flat observable source that backs <see cref="RowsView"/>.</summary>
    public ObservableCollection<DifferenceRowViewModel> Rows { get; } = [];

    private DataGridCollectionView? _rowsView;

    /// <summary>
    /// Grouped + filtered view of <see cref="Rows"/> consumed by the DataGrid.
    /// Re-built whenever <see cref="GroupingMode"/> changes.
    /// </summary>
    public DataGridCollectionView RowsView => _rowsView ??= BuildRowsView();

    private DataGridCollectionView BuildRowsView()
    {
        DataGridCollectionView view = new(Rows)
        {
            Filter = SearchPredicate
        };
        // Stable status order — Diversi → Solo destinazione → Solo provenienza
        // → Identici — drives the order groups appear in when grouping is on,
        // and the row order when no grouping is selected.
        view.SortDescriptions.Add(DataGridSortDescription
            .FromPath(nameof(DifferenceRowViewModel.StatusOrder)));
        // KindOrder (Tabelle → Viste → Procedure → Funzioni → Trigger → rest)
        // — KindDisplayName alphabetical was producing the wrong order
        // (Funzioni / Procedure / Tabelle / Trigger / Viste).
        view.SortDescriptions.Add(DataGridSortDescription
            .FromPath(nameof(DifferenceRowViewModel.KindOrder)));
        view.SortDescriptions.Add(DataGridSortDescription
            .FromPath(nameof(DifferenceRowViewModel.QualifiedName)));
        ApplyGrouping(view);
        return view;
    }

    private void ApplyGrouping(DataGridCollectionView view)
    {
        view.GroupDescriptions.Clear();
        switch (GroupingMode)
        {
            case "Tipo di differenza":
                view.GroupDescriptions.Add(
                    new DataGridPathGroupDescription(nameof(DifferenceRowViewModel.StatusDisplayItalian)));
                break;
            case "Tipo di oggetto":
                view.GroupDescriptions.Add(
                    new DataGridPathGroupDescription(nameof(DifferenceRowViewModel.KindDisplayName)));
                break;
            case "Nessun gruppo":
            default:
                // No grouping applied
                break;
        }
    }

    private bool SearchPredicate(object item)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }
        if (item is not DifferenceRowViewModel row)
        {
            return true;
        }
        string q = SearchText.Trim();
        return Contains(row.Kind, q)
            || Contains(row.SchemaName, q)
            || Contains(row.ObjectName, q)
            || Contains(row.Status, q)
            // The Italian label, not just the raw enum. The grid shows
            // "Diverso" and groups by it, while the search compared only
            // "Different" — so typing what was on screen found nothing.
            || Contains(row.StatusDisplayItalian, q)
            || Contains(row.KindDisplayName, q);
    }

    private static bool Contains(string? source, string query) =>
        source?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    partial void OnGroupingModeChanged(string value)
    {
        if (_rowsView is not null)
        {
            ApplyGrouping(_rowsView);
            _rowsView.Refresh();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _rowsView?.Refresh();
        // The visible set just changed, so "all selected" may have flipped
        // without a single row's IsSelected moving.
        OnPropertyChanged(nameof(AllVisibleSelected));
    }

    /// <summary>
    /// Rebuilds <see cref="Rows"/> from the latest comparison result.
    /// Called by <see cref="RefreshCommand"/> and when a comparison completes.
    /// </summary>
    private void RebuildRows()
    {
        Rows.Clear();
        string envColor = "#0054BD"; // default cobalt — env-aware colour comes in Wave 2C
        if (AppState.LastComparison is not null && AppState.LastComparisonRaw is not null)
        {
            // The DTO list is a positional projection of the raw list — Mapper.ToDto
            // is a single Select over Differences — so the pair for row i IS raw[i].
            // This used to re-join the two by (Kind, Schema, Name) through a
            // ToDictionary, which was both unnecessary and fatal: that triple is
            // NOT unique. Permission.Identity leaves ClassDesc out while its
            // DiffKey keeps it, so two permission rows can produce the same key,
            // and the ArgumentException came out of a PropertyChanged handler
            // with no try around it — the app died mid-comparison. Pairing by
            // index cannot throw and cannot silently drop a row either.
            IReadOnlyList<DifferencePair> pairs = AppState.LastComparisonRaw.Differences;
            IReadOnlyList<DifferenceDto> dtos = AppState.LastComparison.Differences;
            if (pairs.Count != dtos.Count)
            {
                // Only reachable if someone publishes the two halves separately;
                // PublishComparison is the one place that sets both, and its
                // remarks already flag that the generated setters stay public.
                AppState.LastError =
                    "Risultati incoerenti: il confronto grezzo e la sua proiezione hanno "
                    + $"{pairs.Count} e {dtos.Count} differenze. Rilancia il confronto.";
            }
            else
            {
                for (int i = 0; i < dtos.Count; i++)
                {
                    DifferenceRowViewModel row = new(pairs[i], dtos[i], envColor);
                    row.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(DifferenceRowViewModel.IsSelected))
                        {
                            OnRowSelectionChanged();
                        }
                    };
                    Rows.Add(row);
                }
            }
        }
        _rowsView?.Refresh();
        DeployCommand.NotifyCanExecuteChanged();
        ExecuteOnTargetCommand.NotifyCanExecuteChanged();
        // Surface the new bucket counts to the badges in the results action bar.
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(DiffsModifiedCount));
        OnPropertyChanged(nameof(DiffsOnlyDestCount));
        OnPropertyChanged(nameof(DiffsOnlyProvCount));
        OnPropertyChanged(nameof(IdenticalsCount));
        OnPropertyChanged(nameof(TotalDiffsCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(AllVisibleSelected));
    }

    // ── New topbar commands ──────────────────────────────────────────────────

    /// <summary>
    /// Opens the setup dialog on a blank project. On OK the result replaces
    /// <c>AppState.CurrentProject</c> and a comparison is fired, exactly as the
    /// startup dialog does.
    /// </summary>
    [RelayCommand]
    public async Task NewProjectAsync(Window? owner)
    {
        if (owner is null) { return; }

        // A project is open: starting a new one drops the current comparison,
        // the rows the user has ticked, and anything not yet saved or executed.
        if (AppState.CurrentProject is not null)
        {
            bool go = await Views.ConfirmDialog.AskAsync(
                owner,
                title: "Nuovo progetto",
                headline: "Vuoi abbandonare il progetto corrente?",
                body: "Le modifiche non salvate e le operazioni non ancora eseguite "
                    + "andranno perse. Salva prima il progetto se vuoi conservarle.",
                confirmLabel: "Abbandona e crea").ConfigureAwait(true);
            if (!go) { return; }
        }

        await RunSetupDialogAsync(owner, project: null).ConfigureAwait(true);
    }

    /// <summary>
    /// Shared setup-dialog round trip: build the view-model, seed the server
    /// picker from the connection store, show, and adopt the result.
    /// </summary>
    /// <remarks>
    /// The seeding is the point. "Nuovo" and "Modifica" each used to new up the
    /// dialog themselves and neither passed the connection store, so both
    /// opened with an empty server drop-down.
    /// </remarks>
    private async Task<DbDeltaProject?> RunSetupDialogAsync(Window owner, DbDeltaProject? project)
    {
        var vm = ProjectSetupViewModel.FromProject(project, _credentials);
        vm.SeedRecentServersFrom(AppState.Connections?.Entries);
        Views.ProjectSetupDialog dialog = new() { DataContext = vm };

        DbDeltaProject? result =
            await dialog.ShowDialog<DbDeltaProject?>(owner).ConfigureAwait(true);
        if (result is null) { return null; }

        AppState.SourceConnectionString = dialog.LastSourceConnectionString ?? string.Empty;
        AppState.TargetConnectionString = dialog.LastTargetConnectionString ?? string.Empty;
        AppState.CurrentProject = result;

        if (!string.IsNullOrWhiteSpace(AppState.SourceConnectionString)
            && !string.IsNullOrWhiteSpace(AppState.TargetConnectionString))
        {
            await AppState.CompareCommand.ExecuteAsync(CancellationToken.None).ConfigureAwait(true);
        }

        return result;
    }

    /// <summary>
    /// Re-opens the project-setup dialog pre-populated with the currently
    /// active project, so the user can tweak endpoint, credentials, or
    /// options. On OK the new project replaces <c>AppState.CurrentProject</c>
    /// and a fresh comparison is fired.
    /// </summary>
    [RelayCommand]
    public async Task EditProjectAsync(Window? owner)
    {
        if (owner is null || AppState.CurrentProject is null)
        {
            return;
        }

        await RunSetupDialogAsync(owner, AppState.CurrentProject).ConfigureAwait(true);
    }

    /// <summary>Re-runs the comparison and rebuilds the grid.</summary>
    /// <remarks>
    /// Through <c>ExecuteAsync</c>, which runs the command without consulting
    /// its CanExecute gate — that gate can transiently return false during
    /// state transitions, and this path has always meant to bypass it. Calling
    /// <c>CompareAsync</c> directly would bypass the command's token too, and
    /// the overlay's Annulla button would be dead for exactly this button.
    /// The IsBusy guard stays: <c>ExecuteAsync</c> cancels the run in flight
    /// before starting a new one, so without it a second Aggiorna would abort
    /// the first instead of being ignored.
    /// </remarks>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(AppState.SourceConnectionString)
            || string.IsNullOrWhiteSpace(AppState.TargetConnectionString))
        {
            AppState.LastError = "Carica o crea un progetto prima di poter aggiornare il confronto.";
            return;
        }
        if (AppState.IsBusy) { return; }
        await AppState.CompareCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    /// <summary>
    /// Status text shown in the bottom bar of the main window.
    /// Overrides the simple "Ready / Working…" text from AppStateViewModel
    /// with deploy-action feedback.
    /// </summary>
    [ObservableProperty]
    private string _statusText = "Ready";

    /// <summary>
    /// Clears <see cref="AppStateViewModel.LastError"/>, collapsing the error
    /// banner in the shell.
    /// </summary>
    [RelayCommand]
    private void DismissError() => AppState.LastError = null;

    /// <summary>
    /// Produces an alignment SQL script for the selected rows and lets the
    /// user save it to a file via the system Save dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeploy))]
    public async Task DeployAsync(Window? owner)
    {
        if (owner is null) { return; }
        if (AppState.LastComparisonRaw is null) { return; }

        IReadOnlyList<DifferencePair> selected = SelectedPairs();
        if (selected.Count == 0) { StatusText = "Nessuna differenza selezionata."; return; }

        // Asked BEFORE the save dialog: a cancel here means no script at all, so
        // there is no point making the user choose a file first.
        IReadOnlyDictionary<(string Schema, string Table, string Column), string>? backfill =
            await AskForBackfillAsync(owner, selected).ConfigureAwait(true);
        if (backfill is null) { StatusText = BackfillCancelledMessage; return; }

        // Built before the save dialog for the same reason the backfill question
        // is asked before it: a refusal means there is no script, so there is no
        // point making the user name a file for it first.
        if (TryBuildDeployScript(selected, backfill) is not string script) { return; }

        FilePickerSaveOptions opts = new()
        {
            Title = "Salva script di allineamento",
            SuggestedFileName = $"DbDelta-{DateTime.Now:yyyyMMdd-HHmm}.sql",
            FileTypeChoices = [new("Script SQL") { Patterns = ["*.sql"] }],
        };
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(opts).ConfigureAwait(true);
        if (file is null) { return; }

        await using Stream s = await file.OpenWriteAsync().ConfigureAwait(true);
        await using StreamWriter w = new(s, Encoding.UTF8);
        await w.WriteAsync(script).ConfigureAwait(true);

        StatusText = $"Script salvato in {file.Path.LocalPath} — {selected.Count} oggetti.";
    }

    private bool CanDeploy() => Rows.Any(r => r.IsSelected) && !AppState.ResultsAreStale;

    /// <summary>
    /// Writes the HTML comparison report the CLI has always been able to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generator was finished and tested and had exactly one production
    /// caller — <c>dbdelta report</c> — so the only way to get the report from
    /// the desktop app was to install the CLI and re-run the comparison against
    /// both servers a second time. The input it needs is
    /// <see cref="AppStateViewModel.LastComparisonRaw"/>, which the app is
    /// already holding.
    /// </para>
    /// <para>
    /// It covers the WHOLE comparison, not the ticked rows: the report is the
    /// record of what the two databases looked like, while the selection is
    /// what the user intends to deploy. That is also why it does not require a
    /// selection, and why the tooltip says so.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanSaveReport))]
    public async Task SaveReportAsync(Window? owner)
    {
        if (owner is null) { return; }
        if (AppState.LastComparisonRaw is null) { return; }

        FilePickerSaveOptions opts = new()
        {
            Title = "Salva report di confronto",
            SuggestedFileName = $"DbDelta-report-{DateTime.Now:yyyyMMdd-HHmm}.html",
            FileTypeChoices = [new("Report HTML") { Patterns = ["*.html"] }],
        };
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(opts).ConfigureAwait(true);
        if (file is null) { return; }

        string html = new HtmlReportGenerator().Generate(AppState.LastComparisonRaw);
        await using Stream s = await file.OpenWriteAsync().ConfigureAwait(true);
        await using StreamWriter w = new(s, Encoding.UTF8);
        await w.WriteAsync(html).ConfigureAwait(true);

        StatusText = $"Report salvato in {file.Path.LocalPath}.";
    }

    /// <summary>
    /// A comparison is enough — unlike the deploy buttons this needs no
    /// selection, and a stale result is still a truthful record of the run that
    /// produced it.
    /// </summary>
    private bool CanSaveReport() => AppState.LastComparisonRaw is not null;

    /// <summary>
    /// The deploy script, or <c>null</c> after putting the generator's refusal
    /// in the error banner. Shared by <see cref="DeployAsync"/> and
    /// <see cref="ExecuteOnTargetAsync"/> — both build the same script and both
    /// must decline the same way.
    /// </summary>
    /// <remarks>
    /// <see cref="UnscriptableIndexException"/> is a deliberate refusal, not a
    /// fault: the script would drop a columnstore / XML / spatial / hash index
    /// and hold no statement to re-create it. Left to escape, a
    /// <c>[RelayCommand]</c> rethrows it on the UI thread and the window dies —
    /// the one outcome worse than not deploying.
    /// </remarks>
    internal string? TryBuildDeployScript(
        IReadOnlyList<DifferencePair> selected,
        IReadOnlyDictionary<(string Schema, string Table, string Column), string> backfill)
    {
        try
        {
            return DeployScriptBuilder.Build(
                AppState.LastComparisonRaw!,
                selected,
                AppState.SourceConnectionString ?? string.Empty,
                AppState.TargetConnectionString ?? string.Empty,
                DateTime.UtcNow,
                AppState.SourceDependencies,
                AppState.TargetDependencies,
                backfill);
        }
        catch (BoundTypeDropException ex)
        {
            AppState.LastError =
                $"Script non generato: il tipo {ex.Type.SchemaName}.{ex.Type.ObjectName} va ricostruito, ma "
                + $"{ex.Binder.SchemaName}.{ex.Binder.ObjectName} lo usa ancora. SQL Server rifiuta la DROP TYPE "
                + "(Msg 3732), e nessun ordine può salvarlo: la DROP e la CREATE del tipo sono un corpo solo, "
                + "allo slot del tipo, che viene prima di tutto ciò che può legarlo. Allinea prima chi lo usa, "
                + "oppure togli il tipo dalla selezione.";
            StatusText = "Nessuno script generato: vedi il messaggio in alto.";
            return null;
        }
        catch (SchemaboundRebuildException ex)
        {
            AppState.LastError =
                $"Script non generato: {ex.Table.SchemaName}.{ex.Table.ObjectName} va ricostruita, ma "
                + $"{ex.Binder.SchemaName}.{ex.Binder.ObjectName} la lega con SCHEMABINDING. SQL Server rifiuta "
                + "sia la DROP TABLE (Msg 3729) sia la sp_rename (Msg 15336), quindi il rilascio si fermerebbe "
                + "a metà e tornerebbe indietro. Togli il SCHEMABINDING, rilascia e rimettilo, oppure togli "
                + "la tabella dalla selezione.";
            StatusText = "Nessuno script generato: vedi il messaggio in alto.";
            return null;
        }
        catch (UnscriptableIndexException ex)
        {
            AppState.LastError =
                $"Script non generato: l'indice {ex.IndexName} su {ex.Schema}.{ex.Table} è di tipo "
                + $"{ex.TypeDesc ?? "sconosciuto"} e DbDelta non sa ricrearlo. Allineare questa tabella "
                + "lo eliminerebbe senza rimetterlo. Togli la tabella dalla selezione, oppure ricrea "
                + "l'indice a mano dopo il rilascio.";
            StatusText = "Nessuno script generato: vedi il messaggio in alto.";
            return null;
        }
        catch (UnscriptablePermissionException ex)
        {
            AppState.LastError =
                $"Script non generato: il permesso {ex.Action} per {ex.GranteeName} è di classe "
                + $"{ex.ClassDesc} ma l'oggetto a cui si riferisce non ha un nome leggibile. "
                + "Scriverlo lo concederebbe sull'intero database invece che su quell'oggetto. "
                + "Rileggi con un login che veda l'oggetto, oppure escludi i permessi dal confronto.";
            StatusText = "Nessuno script generato: vedi il messaggio in alto.";
            return null;
        }
        catch (UnscriptableUserException ex)
        {
            AppState.LastError =
                $"Script non generato: l'utente {ex.UserName} è associato a un login "
                + (ex.LoginIsOrphaned
                    ? "che su questo server non esiste più"
                    : "di cui questa connessione non può leggere il nome")
                + ". Scriverlo lo creerebbe WITHOUT LOGIN, cioè "
                + "senza nessuno che possa autenticarsi. "
                + (ex.LoginIsOrphaned
                    ? "Ricrea quel login, oppure elimina l'utente orfano, sull'estremità che stai leggendo, "
                    : "Rileggi quell'estremità con un login che veda sys.server_principals, ")
                + "oppure togli l'utente dalla selezione.";
            StatusText = "Nessuno script generato: vedi il messaggio in alto.";
            return null;
        }
        catch (DependencyCycleException ex)
        {
            // Senza questo ramo l'eccezione sfugge al gestore di ultima istanza
            // e la finestra mostra il banner sbagliato: qui non c'è alcun catch
            // generico, di proposito.
            AppState.LastError =
                "Script non generato: le dipendenze fra questi oggetti formano un ciclo, "
                + $"quindi non esiste un ordine in cui scriverli — {ex.Message}. Succede quando un "
                + "CHECK chiama una funzione che legge la stessa tabella: DbDelta scrive il vincolo "
                + "dentro CREATE TABLE, e allora la tabella dovrebbe venire prima della funzione che "
                + "però ha bisogno della tabella. Togli uno dei due dalla selezione, oppure aggiungi "
                + "il vincolo a mano con un ALTER TABLE dopo il rilascio.";
            StatusText = "Nessuno script generato: vedi il messaggio in alto.";
            return null;
        }
        catch (UnscriptableTableTypeException ex)
        {
            AppState.LastError =
                $"Script non generato: il tipo tabella {ex.Schema}.{ex.Name} è memory-optimized. "
                + "DbDelta non scrive né la clausola MEMORY_OPTIMIZED né gli indici HASH con il loro "
                + "BUCKET_COUNT, quindi lo script creerebbe un tipo su disco con lo stesso nome. "
                + "Distribuiscilo a mano, oppure toglilo dalla selezione.";
            StatusText = "Nessuno script generato: vedi il messaggio in alto.";
            return null;
        }
    }

    /// <summary>
    /// Surfaced to the view so the grid can say the rows no longer describe the
    /// configured endpoints. Mirrors <see cref="AppStateViewModel.ResultsAreStale"/>.
    /// </summary>
    public bool ResultsAreStale => AppState.ResultsAreStale;

    /// <summary>
    /// Shows a confirmation dialog then executes the alignment script for the
    /// selected rows in a transaction against the target connection.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteOnTarget))]
    public async Task ExecuteOnTargetAsync(Window? owner)
    {
        if (owner is null) { return; }
        if (AppState.LastComparisonRaw is null) { return; }
        if (string.IsNullOrWhiteSpace(AppState.TargetConnectionString)) { return; }

        IReadOnlyList<DifferencePair> selected = SelectedPairs();
        if (selected.Count == 0) { StatusText = "Nessuna differenza selezionata."; return; }

        IReadOnlyDictionary<(string Schema, string Table, string Column), string>? backfill =
            await AskForBackfillAsync(owner, selected).ConfigureAwait(true);
        if (backfill is null) { StatusText = BackfillCancelledMessage; return; }

        // Script built up-front; the dialog owns the whole confirm → execute →
        // outcome flow and runs this delegate in-dialog (busy state + result
        // panel). Result stays null when the user cancels before executing.
        if (TryBuildDeployScript(selected, backfill) is not string script) { return; }

        // The DROPs by name, not just how many. The dialog is the last gate
        // before an irreversible change and the pairs are already in hand.
        IReadOnlyList<string> dropped =
        [
            .. selected
                .Where(p => p.Status == DifferenceStatus.OnlyInB)
                .Select(p => $"{p.Identity.SchemaName}.{p.Identity.ObjectName}  ({p.Identity.Kind})")
                .Order(StringComparer.OrdinalIgnoreCase)
        ];

        ConfirmExecuteViewModel vm = new(
            objectCount: selected.Count,
            differentCount: selected.Count(p => p.Status == DifferenceStatus.Different),
            droppedObjects: dropped,
            onlyInSourceCount: selected.Count(p => p.Status == DifferenceStatus.OnlyInA),
            sourceSummary: ConnectionStringRedactor.Redact(AppState.SourceConnectionString ?? string.Empty),
            targetSummary: ConnectionStringRedactor.Redact(AppState.TargetConnectionString),
            script: script,
            executeAsync: () => SqlExecutor.ExecuteAsync(
                AppState.TargetConnectionString,
                script,
                CancellationToken.None,
                useOwnTransaction: false,
                commandTimeoutSeconds: DeployCommandTimeoutSeconds));

        Views.ConfirmExecuteDialog dlg = new() { DataContext = vm };
        await dlg.ShowDialog(owner).ConfigureAwait(true);

        await AfterExecuteAsync(vm.Result, vm.ResultMessage).ConfigureAwait(true);
    }

    /// <summary>
    /// Settles the app after a direct execution: mirrors the outcome in the
    /// status bar and, on success only, re-runs the comparison.
    /// </summary>
    /// <remarks>
    /// A successful run has just made every row on screen a lie — the objects it
    /// aligned still show as different, and the ticks are still there to be
    /// deployed a second time. Refreshing by hand worked, but nobody should have
    /// to know that. On FAILURE nothing is touched — not because the script rolls
    /// itself back, which it never gets to do, but because its <c>COMMIT</c> is
    /// never reached — so the rows still describe the target exactly, and throwing
    /// them away would cost the operator the selection they are about to retry.
    /// </remarks>
    /// <param name="result">The outcome, or null when the user never executed.</param>
    /// <param name="resultMessage">The dialog's rendering of that outcome.</param>
    public async Task AfterExecuteAsync(SqlBatchResult? result, string resultMessage)
    {
        if (result is null) { return; } // cancelled before executing — nothing happened
        StatusText = resultMessage;
        LastRun = new LastRunViewModel(result, DateTime.Now);
        if (!result.Success) { return; }

        // Through the command, like every other call site: a refresh nobody can
        // stop is the one that runs right after a deploy, on a catalog that has
        // just changed under it.
        await AppState.CompareCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (AppState.LastError is null)
        {
            StatusText = $"{resultMessage} Confronto aggiornato.";
        }
    }

    /// <summary>
    /// Per-batch command timeout for a deploy run from the app, in seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executor's own default is 60 s, which is a fine default for the
    /// ad-hoc statement it was written for and far too short for a deploy: an
    /// index rebuild, a table rebuild carrying its rows, a CREATE INDEX over a
    /// few million rows all pass a minute without anything being wrong. The
    /// timeout then aborts the batch and XACT_ABORT rolls back everything that
    /// came before it — minutes of correct work discarded, for a batch that was
    /// merely long.
    /// </para>
    /// <para>
    /// Deliberately NOT unlimited, which the CLI does offer as
    /// <c>--command-timeout 0</c>. That is safe there because a console has
    /// Ctrl-C; here the call passes <see cref="CancellationToken.None"/> and the
    /// dialog has no cancel button — the window refuses to close while a run is
    /// in flight, precisely so a transaction is never abandoned. An unlimited
    /// timeout would make a batch blocked on someone else's lock unkillable
    /// short of killing the app, which abandons the transaction it was trying to
    /// protect. Ten minutes is long enough that no honest batch hits it, and
    /// short enough to be a way out. Unlimited becomes available here when the
    /// dialog can genuinely cancel (W3-5), not before.
    /// </para>
    /// </remarks>
    private const int DeployCommandTimeoutSeconds = 600;

    private bool CanExecuteOnTarget() =>
        Rows.Any(r => r.IsSelected)
        && !string.IsNullOrWhiteSpace(AppState.TargetConnectionString)
        && !AppState.ResultsAreStale;

    /// <summary>
    /// Returns the <see cref="DifferencePair"/> for every currently selected row.
    /// </summary>
    private IReadOnlyList<DifferencePair> SelectedPairs() =>
        [.. Rows.Where(r => r.IsSelected).Select(r => r.Pair)];

    /// <summary>
    /// The most recent direct execution, or null until one has run. Survives its
    /// dialog: the outcome panel is gone the moment the operator closes it, and
    /// with it went every error the server had raised.
    /// </summary>
    [ObservableProperty]
    private LastRunViewModel? _lastRun;

    /// <summary>
    /// Opens the transcript of that run — every error, and every PRINT that says
    /// which object was being worked on when one arrived.
    /// </summary>
    [RelayCommand]
    public async Task ShowLastRunAsync(Window? owner)
    {
        if (owner is null || LastRun is null) { return; }
        Views.LastRunDialog dialog = new() { DataContext = LastRun };
        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }

    private const string BackfillCancelledMessage =
        "Operazione annullata: mancano i valori per le colonne NOT NULL da aggiungere.";

    /// <summary>
    /// Runs the Msg 4901 preflight over the selection and, when it finds
    /// anything, asks the operator for a value per column.
    /// </summary>
    /// <returns>
    /// The map to hand the generator — empty when there was nothing to ask —
    /// or <c>null</c> when the user cancelled, which aborts the whole action:
    /// generating or executing anyway produces a script that dies on the first
    /// populated table, halfway through.
    /// </returns>
    /// <remarks>
    /// Shared by «Genera script» and «Allinea destinazione» deliberately. The
    /// two paths build the same script from the same selection; a question asked
    /// on only one of them is a deploy that fails from the button that was not
    /// wired.
    /// </remarks>
    private async Task<IReadOnlyDictionary<(string Schema, string Table, string Column), string>?>
        AskForBackfillAsync(Window owner, IReadOnlyList<DifferencePair> selected)
    {
        IReadOnlyList<BackfillRequirement> required =
            BackfillPreflight.Scan(AppState.LastComparisonRaw!, selected);
        if (required.Count == 0)
        {
            return FrozenDictionary<(string Schema, string Table, string Column), string>.Empty;
        }

        Views.BackfillDialog dialog = new() { DataContext = new BackfillViewModel(required) };
        return await dialog
            .ShowDialog<IReadOnlyDictionary<(string Schema, string Table, string Column), string>?>(owner)
            .ConfigureAwait(true);
    }
}
