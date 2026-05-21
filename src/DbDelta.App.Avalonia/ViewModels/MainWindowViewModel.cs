using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Root view-model for the main window. Holds the single
/// <see cref="AppStateViewModel"/> shared by all child views, plus
/// window-chrome state (sidebar collapsed, theme variant).
/// </summary>
public sealed partial class MainWindowViewModel(AppStateViewModel appState) : ObservableObject
{
    private static readonly GridLength _sidebarOpenWidth = new(240);
    private static readonly GridLength _sidebarClosedWidth = new(0);

    public AppStateViewModel AppState { get; } = appState;

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

        DbDelta.Persistence.Xml.XmlProjectStore store = new();
        await store.SaveAsync(
            file.Path.LocalPath,
            new DbDeltaProject(
                Name: System.IO.Path.GetFileNameWithoutExtension(file.Path.LocalPath),
                SourceConnectionId: src.Id,
                TargetConnectionId: tgt.Id,
                Options: DbDelta.Core.Options.ComparisonOptions.Default),
            System.Threading.CancellationToken.None).ConfigureAwait(true);
        ProjectFilePath = file.Path.LocalPath;
    }

    [RelayCommand]
    public async Task OpenProjectAsync(Window? window)
    {
        if (window is null || AppState.Connections is null)
        {
            return;
        }
        IStorageProvider sp = window.StorageProvider;
        System.Collections.Generic.IReadOnlyList<IStorageFile> picked = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Apri progetto DbDelta",
            AllowMultiple = false,
        });
        if (picked.Count == 0)
        {
            return;
        }
        string path = picked[0].Path.LocalPath;
        DbDelta.Persistence.Xml.XmlProjectStore store = new();
        DbDeltaProject project = await store.LoadAsync(path, System.Threading.CancellationToken.None).ConfigureAwait(true);

        ConnectionEntry? src = AppState.Connections.Entries.FirstOrDefault(e => e.Id == project.SourceConnectionId);
        ConnectionEntry? tgt = AppState.Connections.Entries.FirstOrDefault(e => e.Id == project.TargetConnectionId);
        if (src is null || tgt is null)
        {
            AppState.LastError = "Una o entrambe le connessioni referenziate dal progetto non esistono più. Selezionane di nuove e salva il progetto.";
            return;
        }
        string? srcCs = await AppState.Connections.MaterialiseAsync(src, System.Threading.CancellationToken.None).ConfigureAwait(true);
        string? tgtCs = await AppState.Connections.MaterialiseAsync(tgt, System.Threading.CancellationToken.None).ConfigureAwait(true);
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
}
