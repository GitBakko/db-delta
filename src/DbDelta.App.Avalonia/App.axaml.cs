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
                WindowState = Avalonia.Controls.WindowState.Maximized,
            };
            desktop.MainWindow = mainWindow;

            // Show the project-setup dialog before the main window is visible.
            // We schedule it so the Avalonia dispatcher loop is running when we
            // call ShowDialog.
            desktop.MainWindow.Opened += async (_, _) =>
            {
                // Preload the MRU connection list so the dialog's pickers are
                // populated; do NOT auto-prefill AppState — the main shell must
                // start empty until the user confirms a project.
                await connections.LoadAsync(CancellationToken.None).ConfigureAwait(true);

                // Pass the credential store so the dialog can auto-fill saved
                // user/password pairs on server selection (DPAPI on Windows).
                ProjectSetupViewModel setupVm = new(credentials);
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

                // Prefer the live connection strings captured at OK time — they
                // include the password the user just typed, which is NOT carried
                // by ProjectEndpoint (intentionally). Fall back to the endpoint
                // builder for the (rare) case where the dialog was bypassed.
                appState.SourceConnectionString =
                    dialog.LastSourceConnectionString
                    ?? (result.Source is not null ? BuildConnectionString(result.Source) : string.Empty);
                appState.TargetConnectionString =
                    dialog.LastTargetConnectionString
                    ?? (result.Target is not null ? BuildConnectionString(result.Target) : string.Empty);

                // Publish the chosen project so the main shell's header strip
                // can show the source/target server + database names.
                appState.CurrentProject = result;

                // Auto-run the comparison so the main view is no longer empty.
                if (!string.IsNullOrWhiteSpace(appState.SourceConnectionString)
                    && !string.IsNullOrWhiteSpace(appState.TargetConnectionString))
                {
                    await appState.CompareCommand
                                  .ExecuteAsync(CancellationToken.None)
                                  .ConfigureAwait(true);
                }
            };
        }

#if DEBUG
        // F12 inspects the live visual tree (AvaloniaUI.DiagnosticsSupport).
        this.AttachDeveloperTools();
#endif

        base.OnFrameworkInitializationCompleted();
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
