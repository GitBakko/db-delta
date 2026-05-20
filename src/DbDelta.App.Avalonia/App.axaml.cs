using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;

namespace DbDelta.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Singleton AppState — the view model layer is intentionally light;
            // the heavy lifting lives in DbDelta.Core / Providers.
            AppStateViewModel appState = new();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(appState),
            };
        }

#if DEBUG
        // F12 inspects the live visual tree (AvaloniaUI.DiagnosticsSupport).
        this.AttachDeveloperTools();
#endif

        base.OnFrameworkInitializationCompleted();
    }
}
