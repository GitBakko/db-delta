using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ScriptGen;
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
    public MainWindowViewModel(AppStateViewModel appState)
    {
        AppState = appState;
        AppState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppStateViewModel.LastComparison))
            {
                RebuildRows();
            }
            else if (e.PropertyName == nameof(AppStateViewModel.TargetConnectionString))
            {
                ExecuteOnTargetCommand.NotifyCanExecuteChanged();
            }
        };
        Rows.CollectionChanged += (_, _) =>
        {
            DeployCommand.NotifyCanExecuteChanged();
            ExecuteOnTargetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionSummary));
        };
    }

    /// <summary>
    /// Human-readable counter shown in the results action bar. Excludes
    /// identical rows from the differences total, surfaces per-type partials
    /// (modificate / solo destinazione / solo provenienza) and ends with the
    /// identical count. Example:
    /// "3 di 12 differenze selezionate · 5 modificate, 4 solo destinazione,
    ///  3 solo provenienza · 27 identiche".
    /// </summary>
    public string SelectionSummary
    {
        get
        {
            int totalDiffs = Rows.Count(r => !r.IsIdentical);
            int identicals = Rows.Count(r => r.IsIdentical);
            int selected = Rows.Count(r => r.IsSelected);
            int diffModified = Rows.Count(r => r.Status == "Different");
            int diffOnlyDest = Rows.Count(r => r.IsTargetOnly);
            int diffOnlyProv = Rows.Count(r => r.IsSourceOnly);

            if (totalDiffs == 0 && identicals == 0) { return "Nessuna differenza."; }

            string header = selected == 0
                ? $"{totalDiffs} differenze rilevate"
                : $"{selected} di {totalDiffs} differenze selezionate";

            List<string> partials = [];
            if (diffModified > 0) { partials.Add($"{diffModified} modificate"); }
            if (diffOnlyDest > 0) { partials.Add($"{diffOnlyDest} solo destinazione"); }
            if (diffOnlyProv > 0) { partials.Add($"{diffOnlyProv} solo provenienza"); }
            string detail = partials.Count > 0 ? $" · {string.Join(", ", partials)}" : string.Empty;

            string tail = identicals > 0 ? $" · {identicals} identiche" : string.Empty;
            return $"{header}{detail}{tail}.";
        }
    }

    /// <summary>
    /// Called by <see cref="DifferenceRowViewModel"/> whenever its
    /// <c>IsSelected</c> flips, so the action bar counter and the deploy /
    /// execute can-execute states refresh.
    /// </summary>
    internal void OnRowSelectionChanged()
    {
        DeployCommand.NotifyCanExecuteChanged();
        ExecuteOnTargetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public AppStateViewModel AppState { get; }

    /// <summary>Build banner shown in the topbar.</summary>
    public string Version => "v0.1 alpha";

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private string? _projectFilePath;

    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    [RelayCommand]
    public async Task SaveProjectAsync(Window? window)
    {
        if (window is null || AppState.Connections is null)
        {
            return;
        }
        IStorageProvider sp = window.StorageProvider;
        IStorageFile? file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva progetto DbDelta",
            DefaultExtension = "dbd",
            SuggestedFileName = "progetto.dbd",
        });
        if (file is null)
        {
            return;
        }

        ConnectionEntry? src = AppState.Connections.Entries.FirstOrDefault();
        ConnectionEntry? tgt = AppState.Connections.Entries.Skip(1).FirstOrDefault();
        if (src is null || tgt is null)
        {
            AppState.LastError = "Servono almeno due connessioni salvate prima di poter salvare un progetto.";
            return;
        }

        Persistence.Xml.XmlProjectStore store = new();
        DateTime now = DateTime.UtcNow;
        await store.SaveAsync(
            file.Path.LocalPath,
            new DbDeltaProject(
                Name: Path.GetFileNameWithoutExtension(file.Path.LocalPath),
                CreatedUtc: now,
                LastModifiedUtc: now,
                SourceConnectionId: src.Id,
                TargetConnectionId: tgt.Id,
                Options: Core.Options.ComparisonOptions.Default),
            CancellationToken.None).ConfigureAwait(true);
        ProjectFilePath = file.Path.LocalPath;
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
        if (window is null || AppState.Connections is null)
        {
            return;
        }
        IStorageProvider sp = window.StorageProvider;
        IReadOnlyList<IStorageFile> picked = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Carica progetto DbDelta",
            AllowMultiple = false,
        });
        if (picked.Count == 0)
        {
            return;
        }
        string path = picked[0].Path.LocalPath;
        Persistence.Xml.XmlProjectStore store = new();
        DbDeltaProject project = await store.LoadAsync(path, CancellationToken.None).ConfigureAwait(true);

        ConnectionEntry? src = AppState.Connections.Entries.FirstOrDefault(e => e.Id == project.SourceConnectionId);
        ConnectionEntry? tgt = AppState.Connections.Entries.FirstOrDefault(e => e.Id == project.TargetConnectionId);
        if (src is null || tgt is null)
        {
            AppState.LastError = "Una o entrambe le connessioni referenziate dal progetto non esistono più. Selezionane di nuove e salva il progetto.";
            return;
        }
        string? srcCs = await AppState.Connections.MaterialiseAsync(src, CancellationToken.None).ConfigureAwait(true);
        string? tgtCs = await AppState.Connections.MaterialiseAsync(tgt, CancellationToken.None).ConfigureAwait(true);
        if (srcCs is not null)
        {
            AppState.SourceConnectionString = srcCs;
        }
        if (tgtCs is not null)
        {
            AppState.TargetConnectionString = tgtCs;
        }
        ProjectFilePath = path;
    }

    // ── Results grid state ───────────────────────────────────────────────────

    /// <summary>
    /// Recent project paths shown in the "I miei progetti" drop-down.
    /// Populated lazily by Wave 2B; empty for now.
    /// </summary>
    public ObservableCollection<string> ProjectMru { get; } = [];

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
        view.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription
            .FromPath(nameof(DifferenceRowViewModel.StatusOrder)));
        view.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription
            .FromPath(nameof(DifferenceRowViewModel.KindDisplayName)));
        view.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription
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

    partial void OnSearchTextChanged(string value) => _rowsView?.Refresh();

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
            // Build a lookup from (Kind, Schema, Name) → DifferencePair so each
            // row can carry its typed pair for the deploy pipeline.
            // Key = (Kind, Schema, Name) — all strings come from the same comparison engine
            // so ordinal equality is sufficient.
            Dictionary<(string Kind, string Schema, string Name), DifferencePair> pairMap =
                AppState.LastComparisonRaw.Differences.ToDictionary(
                    p => (p.Identity.Kind, p.Identity.SchemaName, p.Identity.ObjectName));

            foreach (DifferenceDto dto in AppState.LastComparison.Differences)
            {
                if (pairMap.TryGetValue((dto.Kind, dto.SchemaName, dto.ObjectName), out DifferencePair? pair)
                    && pair is not null)
                {
                    DifferenceRowViewModel row = new(pair, dto, envColor);
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
    }

    // ── New topbar commands ──────────────────────────────────────────────────

    /// <summary>Stub — opens the Wave 2C new-project dialog when ready.</summary>
    [RelayCommand]
    public void NewProject()
    {
        // Wave 2C stub — no-op until ProjectSetupDialog routing is wired.
    }

    /// <summary>
    /// Edit the current project (opens Wave 2C dialog — no-op stub until ready).
    /// </summary>
    [RelayCommand]
    public void EditProject()
    {
        // Wave 2C stub.
    }

    /// <summary>Re-runs the comparison and rebuilds the grid.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        await AppState.CompareCommand.ExecuteAsync(null).ConfigureAwait(true);
        RebuildRows();
    }

    /// <summary>
    /// Status text shown in the bottom bar of the main window.
    /// Overrides the simple "Ready / Working…" text from AppStateViewModel
    /// with deploy-action feedback.
    /// </summary>
    [ObservableProperty]
    private string _statusText = "Ready";

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

        FilePickerSaveOptions opts = new()
        {
            Title = "Salva script di allineamento",
            SuggestedFileName = $"DbDelta-{DateTime.Now:yyyyMMdd-HHmm}.sql",
            FileTypeChoices = [new("Script SQL") { Patterns = ["*.sql"] }],
        };
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(opts).ConfigureAwait(true);
        if (file is null) { return; }

        string script = DeployScriptBuilder.Build(
            selected,
            AppState.SourceConnectionString ?? string.Empty,
            AppState.TargetConnectionString ?? string.Empty,
            DateTime.UtcNow);

        await using Stream s = await file.OpenWriteAsync().ConfigureAwait(true);
        await using StreamWriter w = new(s, Encoding.UTF8);
        await w.WriteAsync(script).ConfigureAwait(true);

        StatusText = $"Script salvato in {file.Path.LocalPath} — {selected.Count} oggetti.";
    }

    private bool CanDeploy() => Rows.Any(r => r.IsSelected);

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

        Views.ConfirmExecuteDialog dlg = new()
        {
            DataContext = new ConfirmExecuteViewModel(
                selected.Count,
                ConnectionStringRedactor.Redact(AppState.TargetConnectionString)),
        };
        bool? ok = await dlg.ShowDialog<bool?>(owner).ConfigureAwait(true);
        if (ok != true) { return; }

        string script = DeployScriptBuilder.Build(
            selected,
            AppState.SourceConnectionString ?? string.Empty,
            AppState.TargetConnectionString ?? string.Empty,
            DateTime.UtcNow);

        StatusText = "Esecuzione in corso…";
        SqlBatchResult res = await SqlExecutor.ExecuteAsync(
            AppState.TargetConnectionString!,
            script,
            CancellationToken.None).ConfigureAwait(true);

        StatusText = res.Success
            ? $"Esecuzione completata — {res.BatchesExecuted} batch in {res.TotalDurationMs} ms."
            : $"Esecuzione fallita: {res.ErrorMessage}";
    }

    private bool CanExecuteOnTarget() =>
        Rows.Any(r => r.IsSelected)
        && !string.IsNullOrWhiteSpace(AppState.TargetConnectionString);

    /// <summary>
    /// Returns the <see cref="DifferencePair"/> for every currently selected row.
    /// </summary>
    private IReadOnlyList<DifferencePair> SelectedPairs() =>
        [.. Rows.Where(r => r.IsSelected).Select(r => r.Pair)];
}
