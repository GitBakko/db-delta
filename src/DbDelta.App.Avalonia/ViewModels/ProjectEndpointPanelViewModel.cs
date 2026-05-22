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
        _serverSuggestions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ServerCountText));
            OnPropertyChanged(nameof(HasServerSuggestions));
        };
        _availableDatabases.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DatabaseCountText));
            OnPropertyChanged(nameof(HasDatabases));
        };
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

    // ── Version info ─────────────────────────────────────────────────────────

    [ObservableProperty] private string? _serverVersion;
    [ObservableProperty] private int? _serverMajorVersion;

    // ── Derived display properties ────────────────────────────────────────────

    /// <summary>
    /// Friendly display name shown in the Source/Target band header.
    /// Falls back to a placeholder when ServerName is empty.
    /// </summary>
    public string DisplayBandName => string.IsNullOrWhiteSpace(ServerName)
        ? (IsTarget ? "Seleziona una destinazione…" : "Seleziona una provenienza…")
        : ServerName;

    /// <summary>Inline counter text shown next to the "Server" section label.</summary>
    public string ServerCountText => $"({ServerSuggestions.Count} trovati)";

    /// <summary>Inline counter text shown next to the "Database" section label.</summary>
    public string DatabaseCountText => $"({AvailableDatabases.Count} trovati)";

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
        OnPropertyChanged(nameof(DisplayBandName));
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
                : null;
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
            string cs = BuildConnectionString(includeDatabase: false);
            IReadOnlyList<string> dbs =
                await SqlServerDiscovery.ListDatabasesAsync(cs, CancellationToken.None)
                                        .ConfigureAwait(true);
            foreach (string db in dbs)
            {
                AvailableDatabases.Add(db);
            }
            HasDatabases = dbs.Count > 0;
            ConnectionStatusMessage = $"Connesso — {dbs.Count} database disponibili";

            // Best-effort version detection — failure is silently suppressed.
            try
            {
                string? version = await SqlServerDiscovery
                    .GetServerVersionAsync(cs, CancellationToken.None)
                    .ConfigureAwait(true);
                ServerVersion = version;
                if (version is not null)
                {
                    // Parse the major version number from the connection string builder.
                    // We re-query SERVERPROPERTY via the same connection string, but
                    // the version string itself starts with the product year — extract major
                    // from the connection (the method already succeeded so the server is reachable).
                    int? major = await TryGetMajorVersionAsync(cs).ConfigureAwait(true);
                    ServerMajorVersion = major;
                }
            }
            catch
            {
                // Version info is a nice-to-have — keep databases loaded.
            }
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

    private static async Task<int?> TryGetMajorVersionAsync(string connectionString)
    {
        try
        {
            Microsoft.Data.SqlClient.SqlConnectionStringBuilder b = new(connectionString)
            {
                ConnectTimeout = 5,
            };
            await using Microsoft.Data.SqlClient.SqlConnection cn = new(b.ConnectionString);
            await cn.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using Microsoft.Data.SqlClient.SqlCommand cmd = new(
                "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT);", cn);
            object? result = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            return result is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    private bool CanLoadDatabases() =>
        !IsLoadingDatabases
        && !string.IsNullOrWhiteSpace(ServerName)
        && (AuthMode == AuthenticationMode.WindowsIntegrated
            || (!string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Password)));

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string BuildConnectionString(bool includeDatabase = false)
    {
        string db = includeDatabase && !string.IsNullOrWhiteSpace(DatabaseName)
            ? $"Database={DatabaseName};"
            : "";
        return AuthMode switch
        {
            AuthenticationMode.WindowsIntegrated =>
                $"Server={ServerName};{db}Integrated Security=True;"
                + $"Encrypt={Encrypt};TrustServerCertificate={TrustServerCertificate}",
            AuthenticationMode.SqlServer or _ =>
                $"Server={ServerName};{db}User Id={UserName};Password={Password};"
                + $"Encrypt={Encrypt};TrustServerCertificate={TrustServerCertificate}",
        };
    }

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

    /// <summary>
    /// Copies all connection fields from the given endpoint into this instance.
    /// Clears runtime scan/load state (suggestions, databases, version).
    /// </summary>
    public void LoadFromEndpoint(ProjectEndpoint? endpoint)
    {
        if (endpoint is null) { return; }

        ServerName = endpoint.Connection.ServerName;
        DatabaseName = endpoint.Connection.DatabaseName;
        AuthMode = endpoint.Authentication.Mode;
        UserName = endpoint.Authentication.UserName ?? "";
        RememberCredentials = endpoint.Authentication.RememberCredentials;
        Encrypt = endpoint.Authentication.Encrypt;
        TrustServerCertificate = endpoint.Authentication.TrustServerCertificate;

        // Clear runtime state.
        ServerSuggestions.Clear();
        HasServerSuggestions = false;
        AvailableDatabases.Clear();
        HasDatabases = false;
        ServerVersion = null;
        ServerMajorVersion = null;
        ScanStatusMessage = null;
        ConnectionStatusMessage = null;
    }
}
