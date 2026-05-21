using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.Abstractions;

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
            ICredentialStore credentials = Persistence.Credentials.CredentialStoreFactory.Create();
            IConnectionStore connectionStore = Persistence.Json.JsonConnectionStore.CreateDefault();
            ConnectionStoreViewModel connections = new(connectionStore, credentials);
            AppStateViewModel appState = new(connections);

            MainWindow mainWindow = new()
            {
                DataContext = new MainWindowViewModel(appState),
            };
            desktop.MainWindow = mainWindow;

            // Show the project-setup dialog before the main window is visible.
            // We schedule it so the Avalonia dispatcher loop is running when we
            // call ShowDialog.
            desktop.MainWindow.Opened += async (_, _) =>
            {
                await LoadAndPrefillAsync(connections, appState).ConfigureAwait(true);

                // Skip the dialog when autosave already restored both endpoints.
                bool alreadyLoaded =
                    !string.IsNullOrWhiteSpace(appState.SourceConnectionString)
                    && !string.IsNullOrWhiteSpace(appState.TargetConnectionString);

                if (!alreadyLoaded)
                {
                    ProjectSetupViewModel setupVm = new();
                    ProjectSetupDialog dialog = new() { DataContext = setupVm };
                    DbDeltaProject? result =
                        await dialog.ShowDialog<DbDeltaProject?>(mainWindow)
                                    .ConfigureAwait(true);

                    if (result is null)
                    {
                        // User cancelled — close the app gracefully.
                        desktop.Shutdown();
                        return;
                    }

                    // Feed the project's connection strings into AppState so the
                    // comparison flow can run immediately.
                    if (result.Source is not null)
                    {
                        appState.SourceConnectionString =
                            BuildConnectionString(result.Source);
                    }
                    if (result.Target is not null)
                    {
                        appState.TargetConnectionString =
                            BuildConnectionString(result.Target);
                    }
                }
            };
        }

#if DEBUG
        // F12 inspects the live visual tree (AvaloniaUI.DiagnosticsSupport).
        this.AttachDeveloperTools();
#endif

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task LoadAndPrefillAsync(
        ConnectionStoreViewModel cs,
        AppStateViewModel state)
    {
        await cs.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        if (cs.Entries.Count >= 1)
        {
            string? src = await cs.MaterialiseAsync(cs.Entries[0], CancellationToken.None).ConfigureAwait(true);
            if (src is not null)
            {
                state.SourceConnectionString = src;
            }
        }
        if (cs.Entries.Count >= 2)
        {
            string? tgt = await cs.MaterialiseAsync(cs.Entries[1], CancellationToken.None).ConfigureAwait(true);
            if (tgt is not null)
            {
                state.TargetConnectionString = tgt;
            }
        }
    }

    private static string BuildConnectionString(ProjectEndpoint endpoint)
    {
        string server = endpoint.Connection.ServerName;
        string db = endpoint.Connection.DatabaseName;
        bool trust = endpoint.Authentication.TrustServerCertificate;
        bool encrypt = endpoint.Authentication.Encrypt;

        if (endpoint.Authentication.Mode == AuthenticationMode.WindowsIntegrated)
        {
            return $"Server={server};Database={db};Integrated Security=True;"
                   + $"Encrypt={encrypt};TrustServerCertificate={trust}";
        }

        string user = endpoint.Authentication.UserName ?? "";
        // Password is not stored in ProjectEndpoint — user will need to enter it.
        // We emit an empty-password string; the comparison will fail with a clear
        // SQL auth error, which is the correct UX path to the credential-edit flow.
        return $"Server={server};Database={db};User Id={user};Password=;"
               + $"Encrypt={encrypt};TrustServerCertificate={trust}";
    }
}
