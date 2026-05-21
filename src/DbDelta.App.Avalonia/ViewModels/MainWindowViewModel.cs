using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
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
    private static readonly GridLength _sidebarOpenWidth = new(240);
    private static readonly GridLength _sidebarClosedWidth = new(0);

    public MainWindowViewModel(AppStateViewModel appState)
    {
        AppState = appState;
        AppState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppStateViewModel.LastComparison))
            {
                RebuildRows();
            }
        };
    }

    public AppStateViewModel AppState { get; }

    /// <summary>Build banner shown in the topbar.</summary>
    public string Version => "v0.1 alpha";

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private GridLength _sidebarWidth = _sidebarOpenWidth;

    [ObservableProperty]
    private bool _isSidebarOpen = true;

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
    public void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
        SidebarWidth = IsSidebarOpen ? _sidebarOpenWidth : _sidebarClosedWidth;
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
            Title = "Apri progetto DbDelta",
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
    /// </summary>
    [ObservableProperty]
    private string _groupingMode = "Nessun gruppo";

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
        ApplyGrouping(view);
        return view;
    }

    private void ApplyGrouping(DataGridCollectionView view)
    {
        view.GroupDescriptions.Clear();
        switch (GroupingMode)
        {
            case "Tipo di differenza":
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(DifferenceRowViewModel.Status)));
                break;
            case "Tipo di oggetto":
                view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(DifferenceRowViewModel.Kind)));
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
        if (AppState.LastComparison is not null)
        {
            foreach (DifferenceDto dto in AppState.LastComparison.Differences)
            {
                Rows.Add(new DifferenceRowViewModel(dto, envColor));
            }
        }
        _rowsView?.Refresh();
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
    /// Collects selected rows and writes an alignment SQL summary to a file.
    /// Full DDL generation is available in M9.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeploy))]
    public async Task DeployAsync(Window? window)
    {
        if (window is null)
        {
            return;
        }

        IStorageProvider sp = window.StorageProvider;
        IStorageFile? file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salva script di allineamento",
            DefaultExtension = "sql",
            SuggestedFileName = "allineamento.sql",
        });
        if (file is null)
        {
            return;
        }

        IEnumerable<DifferenceRowViewModel> selected = Rows.Where(r => r.IsSelected);
        string sql = BuildAlignmentSummary([.. selected.Select(r => r.Dto)]);
        await File.WriteAllTextAsync(file.Path.LocalPath, sql).ConfigureAwait(true);
    }

    private bool CanDeploy() => Rows.Any(r => r.IsSelected);

    /// <summary>Execute on target — disabled stub for M9.</summary>
    [RelayCommand(CanExecute = nameof(CanExecuteOnTarget))]
    public void ExecuteOnTarget()
    {
        // M9 stub — not yet implemented.
    }

    private static bool CanExecuteOnTarget() => false;

    private static string BuildAlignmentSummary(IReadOnlyList<DifferenceDto> selection)
    {
        StringBuilder sb = new();
        sb.AppendLine("-- Generated by DbDelta (selected objects)");
        sb.AppendLine("-- Full DDL generation requires live-db context (available in M9).");
        sb.AppendLine("-- Selected differences:");
        foreach (DifferenceDto dto in selection)
        {
            sb.AppendLine($"--   [{dto.Status}] {dto.Kind} {dto.SchemaName}.{dto.ObjectName}");
        }
        return sb.ToString();
    }
}
