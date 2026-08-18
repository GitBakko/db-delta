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

            InstallLastResortErrorHandler(appState);

            var recentProjects =
                Persistence.Json.JsonRecentProjectsStore.CreateDefault();

            // Apply the saved theme BEFORE the window exists, so it is never
            // painted under the wrong variant and corrected a frame later.
            // Blocking is safe here: the store awaits with ConfigureAwait(false),
            // so nothing needs this thread to complete.
            var uiSettings = Persistence.Json.JsonUiSettingsStore.CreateDefault();
            Persistence.Json.AppTheme theme =
                uiSettings.LoadThemeAsync(CancellationToken.None).GetAwaiter().GetResult();
            RequestedThemeVariant = MainWindowViewModel.ToVariant(theme);

            MainWindow mainWindow = new()
            {
                DataContext = new MainWindowViewModel(
                    appState, recentProjects, credentials, uiSettings, theme),
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

                // Seed the "Usati di recente" section in the server picker from
                // the connection store; the dialog's own auto-scan appends a
                // "Risultati scansione" section below it.
                setupVm.SeedRecentServersFrom(connections.Entries);

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
                appState.SourceServerIp = setupVm.Source.ServerIpAddress;
                appState.TargetServerIp = setupVm.Target.ServerIpAddress;

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

    /// <summary>
    /// Last resort for an exception that escapes an <c>async void</c> handler or
    /// an <c>AsyncRelayCommand</c>: report it in the error banner instead of
    /// letting it end the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The app had none, and four write paths rethrow into exactly that gap —
    /// <c>SaveProjectAsync</c>, <c>OnLoadClick</c> and the two save routes of
    /// the setup dialog. Their common failure is ordinary: a locked or
    /// unwritable file under the user's profile. Without this the window
    /// vanishes mid-edit, taking the unsaved project with it.
    /// </para>
    /// <para>
    /// Marking it handled is deliberate. The alternative — terminating — cannot
    /// be better than showing the message, because nothing here has touched a
    /// database: these paths write project files, and a half-written file is
    /// already guarded by the store's own temp-then-move. Deploy failures never
    /// arrive here; they are caught where they happen and shown with their SQL
    /// error.
    /// </para>
    /// <para>
    /// <c>TaskScheduler.UnobservedTaskException</c> is deliberately NOT hooked.
    /// It fires at garbage collection, so the message would reach the banner
    /// minutes after the click that caused it, attached to whatever the user is
    /// doing then. A fire-and-forget call that must not fail silently has to
    /// await, and the ones that matter do.
    /// </para>
    /// </remarks>
    private static void InstallLastResortErrorHandler(AppStateViewModel appState)
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            ReportUnhandled(appState, e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>
    /// What the banner says when an exception reaches the handler. Split out so
    /// it can be exercised without standing up the real application: the
    /// headless tests build their own <c>TestApp</c>, so
    /// <see cref="OnFrameworkInitializationCompleted"/> never runs there and the
    /// subscription itself stays verified by inspection and by the live smoke.
    /// </summary>
    internal static void ReportUnhandled(AppStateViewModel appState, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(exception);
        appState.LastError =
            $"Operazione non riuscita: {exception.Message}. "
            + "Il progetto non è stato salvato; riprova o scegli un altro nome.";
    }
}
