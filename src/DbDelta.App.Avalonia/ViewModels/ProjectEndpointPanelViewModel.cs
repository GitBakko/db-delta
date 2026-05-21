using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.ViewModels;

/// <summary>
/// View-model for one endpoint panel (Source or Target) in the
/// <see cref="ProjectSetupViewModel"/>.  Mirrors the scan/load pattern from
/// <see cref="ConnectionEditViewModel"/> but maps to <see cref="ProjectEndpoint"/>.
/// </summary>
public sealed partial class ProjectEndpointPanelViewModel : ObservableObject
{
    public ProjectEndpointPanelViewModel(string title, bool isTarget)
    {
        (Title, IsTarget) = (title, isTarget);
    }

    // ── Identity ────────────────────────────────────────────────────────────

    public string Title { get; }
    public bool IsTarget { get; }

    // ── Connection fields ────────────────────────────────────────────────────

    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private AuthenticationMode _authMode = AuthenticationMode.SqlServer;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _rememberCredentials;
    [ObservableProperty] private bool _encrypt;
    [ObservableProperty] private bool _trustServerCertificate = true;
    [ObservableProperty] private string _databaseName = "";

    // ── Server scan state ────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<DiscoveredServer> _serverSuggestions = [];
    [ObservableProperty] private bool _hasServerSuggestions;
    [ObservableProperty] private bool _isScanningServers;
    [ObservableProperty] private string? _scanStatusMessage;

    // ── Database load state ──────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<string> _availableDatabases = [];
    [ObservableProperty] private bool _hasDatabases;
    [ObservableProperty] private bool _isLoadingDatabases;
    [ObservableProperty] private string? _connectionStatusMessage;

    // ── Validity ─────────────────────────────────────────────────────────────

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(ServerName)
        && !string.IsNullOrWhiteSpace(DatabaseName)
        && (AuthMode == AuthenticationMode.WindowsIntegrated
            || (!string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Password)));

    // Raise IsValid whenever the relevant backing fields change.
    partial void OnServerNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
    }

    partial void OnDatabaseNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnAuthModeChanged(AuthenticationMode value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
    }

    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
    }

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsScanningServersChanged(bool value) =>
        ScanServersCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingDatabasesChanged(bool value) =>
        LoadDatabasesCommand.NotifyCanExecuteChanged();

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanScanServers))]
    public async Task ScanServersAsync()
    {
        IsScanningServers = true;
        HasServerSuggestions = false;
        ScanStatusMessage = "Scansione in corso…";
        try
        {
            IReadOnlyList<DiscoveredServer> list =
                await SqlServerDiscovery.EnumerateServersAsync(CancellationToken.None)
                                        .ConfigureAwait(true);
            ServerSuggestions.Clear();
            foreach (DiscoveredServer s in list)
            {
                ServerSuggestions.Add(s);
            }
            HasServerSuggestions = list.Count > 0;
            ScanStatusMessage = list.Count == 0
                ? "Nessun server rilevato (SQL Browser potrebbe essere disabilitato)."
                : $"Trovati {list.Count} server.";
        }
        catch (Exception ex)
        {
            ScanStatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsScanningServers = false;
        }
    }

    private bool CanScanServers() => !IsScanningServers;

    [RelayCommand]
    public void PickServer(DiscoveredServer? server)
    {
        if (server is not null && !string.IsNullOrWhiteSpace(server.Name))
        {
            ServerName = server.Name;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadDatabases))]
    public async Task LoadDatabasesAsync()
    {
        IsLoadingDatabases = true;
        HasDatabases = false;
        AvailableDatabases.Clear();
        ConnectionStatusMessage = null;
        try
        {
            string cs = BuildConnectionString();
            IReadOnlyList<string> dbs =
                await SqlServerDiscovery.ListDatabasesAsync(cs, CancellationToken.None)
                                        .ConfigureAwait(true);
            foreach (string db in dbs)
            {
                AvailableDatabases.Add(db);
            }
            HasDatabases = dbs.Count > 0;
            ConnectionStatusMessage = $"Connesso — {dbs.Count} database disponibili";
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage =
                $"Errore: {Persistence.Util.ConnectionStringRedactor.Redact(ex.Message)}";
        }
        finally
        {
            IsLoadingDatabases = false;
        }
    }

    private bool CanLoadDatabases() =>
        !IsLoadingDatabases
        && !string.IsNullOrWhiteSpace(ServerName)
        && (AuthMode == AuthenticationMode.WindowsIntegrated
            || (!string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Password)));

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string BuildConnectionString() =>
        AuthMode == AuthenticationMode.WindowsIntegrated
            ? $"Server={ServerName};Integrated Security=True;"
              + $"Encrypt={Encrypt};TrustServerCertificate={TrustServerCertificate}"
            : $"Server={ServerName};User Id={UserName};Password={Password};"
              + $"Encrypt={Encrypt};TrustServerCertificate={TrustServerCertificate}";

    // ── Materialisation ───────────────────────────────────────────────────────

    /// <summary>
    /// Converts the current panel state into a <see cref="ProjectEndpoint"/>
    /// ready to be embedded in a <see cref="DbDeltaProject"/>.
    /// </summary>
    public ProjectEndpoint ToEndpoint()
    {
        ProjectConnectionRef conn = new(
            Id: Guid.NewGuid(),
            Name: $"{ServerName}.{DatabaseName}",
            ServerName: ServerName,
            DatabaseName: DatabaseName,
            EnvironmentTag: "Dev",
            EnvironmentColorHex: "#0054BD");

        ProjectAuthentication auth = new(
            Mode: AuthMode,
            UserName: AuthMode == AuthenticationMode.SqlServer ? UserName : null,
            RememberCredentials: RememberCredentials,
            Encrypt: Encrypt,
            TrustServerCertificate: TrustServerCertificate);

        return new ProjectEndpoint(conn, auth);
    }

    /// <summary>
    /// Populates a new <see cref="ProjectEndpointPanelViewModel"/> from a
    /// previously-loaded project endpoint.  Returns an empty panel when
    /// <paramref name="endpoint"/> is <see langword="null"/>.
    /// </summary>
    public static ProjectEndpointPanelViewModel FromEndpoint(
        ProjectEndpoint? endpoint,
        string title,
        bool isTarget)
    {
        ProjectEndpointPanelViewModel vm = new(title, isTarget);
        if (endpoint is null)
        {
            return vm;
        }

        vm.ServerName = endpoint.Connection.ServerName;
        vm.DatabaseName = endpoint.Connection.DatabaseName;
        vm.AuthMode = endpoint.Authentication.Mode;
        vm.UserName = endpoint.Authentication.UserName ?? "";
        vm.RememberCredentials = endpoint.Authentication.RememberCredentials;
        vm.Encrypt = endpoint.Authentication.Encrypt;
        vm.TrustServerCertificate = endpoint.Authentication.TrustServerCertificate;
        return vm;
    }
}
