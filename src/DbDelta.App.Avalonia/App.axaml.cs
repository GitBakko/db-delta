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
            DbDelta.Core.Abstractions.ICredentialStore credentials = DbDelta.Persistence.Credentials.CredentialStoreFactory.Create();
            DbDelta.Core.Abstractions.IConnectionStore connectionStore = DbDelta.Persistence.Json.JsonConnectionStore.CreateDefault();
            ConnectionStoreViewModel connections = new(connectionStore, credentials);
            AppStateViewModel appState = new(connections);
            _ = LoadAndPrefillAsync(connections, appState);
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

    private static async System.Threading.Tasks.Task LoadAndPrefillAsync(
        ConnectionStoreViewModel cs,
        AppStateViewModel state)
    {
        await cs.LoadAsync(System.Threading.CancellationToken.None).ConfigureAwait(true);
        if (cs.Entries.Count >= 1)
        {
            string? src = await cs.MaterialiseAsync(cs.Entries[0], System.Threading.CancellationToken.None).ConfigureAwait(true);
            if (src is not null)
            {
                state.SourceConnectionString = src;
            }
        }
        if (cs.Entries.Count >= 2)
        {
            string? tgt = await cs.MaterialiseAsync(cs.Entries[1], System.Threading.CancellationToken.None).ConfigureAwait(true);
            if (tgt is not null)
            {
                state.TargetConnectionString = tgt;
            }
        }
    }
}
