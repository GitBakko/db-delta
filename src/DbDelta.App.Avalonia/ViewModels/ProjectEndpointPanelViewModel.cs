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
    private readonly ICredentialStore? _credentialStore;
    private bool _autoFillFromCredentialsInFlight;
    private CancellationTokenSource? _autoConnectCts;
    private const int AutoConnectDebounceMs = 450;

    public ProjectEndpointPanelViewModel(string title, bool isTarget, ICredentialStore? credentialStore = null)
    {
        (Title, IsTarget) = (title, isTarget);
        _credentialStore = credentialStore;
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

    /// <summary>
    /// IPv4 address of the currently-selected server, when it matches a row
    /// in <see cref="ServerSuggestions"/>. Used to show the IP next to the
    /// server name in the band header.
    /// </summary>
    [ObservableProperty] private string? _serverIpAddress;

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
    /// Falls back to a placeholder when ServerName is empty. Appends the IP
    /// in parentheses when known (resolved from <see cref="ServerSuggestions"/>).
    /// </summary>
    public string DisplayBandName => string.IsNullOrWhiteSpace(ServerName)
        ? (IsTarget ? "Seleziona una destinazione…" : "Seleziona una provenienza…")
        : (string.IsNullOrWhiteSpace(ServerIpAddress)
            ? ServerName
            : $"{ServerName}  ({ServerIpAddress})");

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

        // Different server → forget the previously-loaded databases so the
        // auto-connect heuristic doesn't skip the new connection.
        HasDatabases = false;
        AvailableDatabases.Clear();

        // Refresh IP from suggestions list (if any) and try DPAPI auto-fill.
        ServerIpAddress = ServerSuggestions
            .FirstOrDefault(s => string.Equals(s.Name, value, StringComparison.OrdinalIgnoreCase))?.IpAddress;
        _ = TryAutoFillCredentialsAsync(value);
        ScheduleAutoConnect();
    }

    partial void OnAuthModeChanged(AuthenticationMode value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
        ScheduleAutoConnect();
    }

    partial void OnServerIpAddressChanged(string? value)
        => OnPropertyChanged(nameof(DisplayBandName));

    partial void OnDatabaseNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
        ScheduleAutoConnect();
    }

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
        ScheduleAutoConnect();
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
            ApplyScanResults(list);
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

    /// <summary>
    /// Replaces <see cref="ServerSuggestions"/> with the given list and refreshes
    /// <see cref="ServerIpAddress"/> for the currently-typed <see cref="ServerName"/>
    /// (if any). Public so the parent VM can push a single shared scan into
    /// both source and target panels at once.
    /// </summary>
    public void ApplyScanResults(IReadOnlyList<DiscoveredServer> list)
    {
        ArgumentNullException.ThrowIfNull(list);

        // Preserve any "Usati di recente" prefix at the top of the list when
        // refreshing — only replace the "Risultati scansione" tail.
        List<DiscoveredServer> recents = [..
            ServerSuggestions.Where(s => string.Equals(s.Section, RecentSection, StringComparison.Ordinal))];

        ServerSuggestions.Clear();
        foreach (DiscoveredServer r in recents)
        {
            ServerSuggestions.Add(r);
        }

        bool first = true;
        foreach (DiscoveredServer s in list)
        {
            ServerSuggestions.Add(s with { Section = ScanSection, IsSectionFirst = first });
            first = false;
        }
        HasServerSuggestions = ServerSuggestions.Count > 0;
        ScanStatusMessage = list.Count == 0
            ? "Nessun server rilevato (SQL Browser potrebbe essere disabilitato)."
            : null;

        // Refresh IP for the currently-typed server name, if it matches a row.
        if (!string.IsNullOrWhiteSpace(ServerName))
        {
            ServerIpAddress = list
                .FirstOrDefault(s => string.Equals(s.Name, ServerName, StringComparison.OrdinalIgnoreCase))?
                .IpAddress;
        }
    }

    /// <summary>Section label for previously-used server entries.</summary>
    public const string RecentSection = "Usati di recente";

    /// <summary>Section label for network scan results.</summary>
    public const string ScanSection = "Risultati scansione";

    /// <summary>
    /// Prepends a set of "recently used" servers to <see cref="ServerSuggestions"/>.
    /// Called once from <c>App</c> right after the connection-store load so the
    /// modal opens with the user's known servers already visible — even before
    /// a network scan completes.
    /// </summary>
    public void SeedRecentServers(IReadOnlyList<(string Name, string? IpAddress)> recents)
    {
        if (recents is null || recents.Count == 0) { return; }

        // Strip any previous "recent" entries first so we don't duplicate them
        // across multiple seedings.
        for (int i = ServerSuggestions.Count - 1; i >= 0; i--)
        {
            if (string.Equals(ServerSuggestions[i].Section, RecentSection, StringComparison.Ordinal))
            {
                ServerSuggestions.RemoveAt(i);
            }
        }

        // Insert recents at the top, marking the first as section-first.
        for (int i = 0; i < recents.Count; i++)
        {
            (string name, string? ip) = recents[i];
            ServerSuggestions.Insert(i, new DiscoveredServer(
                Name: name,
                IpAddress: ip,
                Section: RecentSection,
                IsSectionFirst: i == 0));
        }

        // First scan-section item may need its IsSectionFirst recomputed:
        // it's the first row whose Section equals ScanSection.
        bool firstScanSeen = false;
        for (int i = 0; i < ServerSuggestions.Count; i++)
        {
            DiscoveredServer s = ServerSuggestions[i];
            if (!string.Equals(s.Section, ScanSection, StringComparison.Ordinal)) { continue; }
            bool shouldBeFirst = !firstScanSeen;
            if (s.IsSectionFirst != shouldBeFirst)
            {
                ServerSuggestions[i] = s with { IsSectionFirst = shouldBeFirst };
            }
            firstScanSeen = true;
        }

        HasServerSuggestions = ServerSuggestions.Count > 0;
    }

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
            // Success → no inline status message; the populated database combo
            // is itself the visible confirmation. Errors still surface here.
            ConnectionStatusMessage = null;

            // Persist credentials if user opted in.
            await TryPersistCredentialsAsync().ConfigureAwait(true);

            // Fallback: if we still don't have an IP (user typed server name
            // without running the network scan), resolve it via DNS so the band
            // header can always show "Server (192.168.x.x)".
            if (string.IsNullOrWhiteSpace(ServerIpAddress))
            {
                ServerIpAddress = await TryResolveIpAsync(ServerName).ConfigureAwait(true);
            }

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

    // ── Auto-connect debounce ────────────────────────────────────────────────

    /// <summary>
    /// Debounced auto-connect. When all required connection fields are filled
    /// (server + auth credentials) AND no databases have been loaded yet, fire
    /// <see cref="LoadDatabasesAsync"/> after a short quiet period so the user
    /// doesn't have to click "Connetti" manually. Each change cancels the
    /// previous pending attempt — a single connection is fired per quiescent
    /// burst of edits.
    /// </summary>
    private void ScheduleAutoConnect()
    {
        _autoConnectCts?.Cancel();
        if (!IsAutoConnectEligible()) { return; }

        CancellationTokenSource cts = new();
        _autoConnectCts = cts;
        _ = AutoConnectAfterDelayAsync(cts.Token);
    }

    private async Task AutoConnectAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoConnectDebounceMs, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested || !IsAutoConnectEligible()) { return; }

        // LoadDatabasesAsync already handles the IsLoadingDatabases flag and
        // surfaces errors via ConnectionStatusMessage, so we can fire-and-forget.
        try
        {
            await LoadDatabasesAsync().ConfigureAwait(true);
        }
        catch
        {
            // Swallow — manual Connetti remains available if auto-connect fails.
        }
    }

    private bool IsAutoConnectEligible() =>
        !IsLoadingDatabases
        && !HasDatabases
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
        bool isTarget,
        ICredentialStore? credentialStore = null)
    {
        ProjectEndpointPanelViewModel vm = new(title, isTarget, credentialStore);
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
        ServerIpAddress = null;
        ScanStatusMessage = null;
        ConnectionStatusMessage = null;
    }

    // ── DNS resolution (IP fallback) ─────────────────────────────────────────

    /// <summary>
    /// Best-effort DNS lookup to surface the IPv4 of a server typed manually
    /// (i.e. not picked from the scan list). Strips any "\instance" suffix
    /// and any "tcp:" prefix. Returns null on lookup failure.
    /// </summary>
    private static async Task<string?> TryResolveIpAsync(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) { return null; }
        string host = serverName.Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) { host = host[4..]; }
        int comma = host.IndexOf(',');           // strip ",port"
        if (comma > 0) { host = host[..comma]; }
        int slash = host.IndexOf('\\');          // strip "\instance"
        if (slash > 0) { host = host[..slash]; }
        if (string.IsNullOrWhiteSpace(host)) { return null; }

        // If the user already typed an IP literal, just keep it.
        if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? literal))
        {
            return literal.ToString();
        }

        try
        {
            System.Net.IPHostEntry entry =
                await System.Net.Dns.GetHostEntryAsync(host).ConfigureAwait(true);
            System.Net.IPAddress? v4 = entry.AddressList.FirstOrDefault(
                a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return v4?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // ── DPAPI credential persistence ─────────────────────────────────────────

    /// <summary>
    /// Builds the per-server credential key used by <see cref="ICredentialStore"/>.
    /// Server name is normalised to lower-case so different casings collapse to
    /// the same entry.
    /// </summary>
    private static string CredentialKey(string serverName)
        => $"dbdelta:server:{serverName.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Attempts to read previously-saved credentials for the given server and
    /// populate <see cref="UserName"/> + <see cref="Password"/>. Triggered on
    /// every <see cref="ServerName"/> change. Idempotent and silent on failure.
    /// </summary>
    private async Task TryAutoFillCredentialsAsync(string serverName)
    {
        if (_credentialStore is null
            || !_credentialStore.IsAvailable
            || string.IsNullOrWhiteSpace(serverName)
            || _autoFillFromCredentialsInFlight)
        {
            return;
        }

        _autoFillFromCredentialsInFlight = true;
        try
        {
            string? blob = await _credentialStore
                .GetSecretAsync(CredentialKey(serverName), CancellationToken.None)
                .ConfigureAwait(true);
            if (string.IsNullOrEmpty(blob)) { return; }

            // Format: "user|password" — '|' is forbidden in SQL Server logins so
            // it's a safe separator. Older single-field secrets land in Password.
            int sep = blob.IndexOf('|');
            if (sep < 0)
            {
                Password = blob;
                return;
            }

            string user = blob[..sep];
            string pwd = blob[(sep + 1)..];
            if (!string.IsNullOrWhiteSpace(user)) { UserName = user; }
            Password = pwd;
            RememberCredentials = true;
        }
        catch
        {
            // Credential store failures are non-fatal — fall back to manual entry.
        }
        finally
        {
            _autoFillFromCredentialsInFlight = false;
        }
    }

    /// <summary>
    /// Persists the current <see cref="UserName"/>/<see cref="Password"/> to
    /// <see cref="ICredentialStore"/> under the server-specific key when
    /// <see cref="RememberCredentials"/> is enabled; otherwise removes any
    /// existing entry so unchecking the flag actively forgets the secret.
    /// </summary>
    private async Task TryPersistCredentialsAsync()
    {
        if (_credentialStore is null
            || !_credentialStore.IsAvailable
            || string.IsNullOrWhiteSpace(ServerName)
            || AuthMode != AuthenticationMode.SqlServer)
        {
            return;
        }

        string key = CredentialKey(ServerName);
        try
        {
            if (RememberCredentials && !string.IsNullOrEmpty(Password))
            {
                await _credentialStore
                    .SetSecretAsync(key, $"{UserName}|{Password}", CancellationToken.None)
                    .ConfigureAwait(true);
            }
            else
            {
                await _credentialStore
                    .DeleteSecretAsync(key, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
        catch
        {
            // Non-fatal — secret persistence is best-effort.
        }
    }
}
