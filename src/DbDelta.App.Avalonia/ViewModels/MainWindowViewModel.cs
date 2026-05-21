using Avalonia.Controls;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
}
