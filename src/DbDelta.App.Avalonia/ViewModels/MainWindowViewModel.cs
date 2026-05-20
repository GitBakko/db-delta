using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Root view-model for the main window. Holds the single
/// <see cref="AppStateViewModel"/> shared by all child views.
/// </summary>
public sealed partial class MainWindowViewModel(AppStateViewModel appState) : ObservableObject
{
    public AppStateViewModel AppState { get; } = appState;

    /// <summary>Build banner shown in the topbar.</summary>
    public string Version => "v0.1 alpha";

    [ObservableProperty]
    private bool _isDarkTheme;

    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = IsDarkTheme
                ? Avalonia.Styling.ThemeVariant.Dark
                : Avalonia.Styling.ThemeVariant.Light;
        }
    }
}
