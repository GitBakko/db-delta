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
    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// Cancelled when the dialog that owns this panel closes. Every network and
    /// credential-store call below runs on this token, so "the window is gone"
    /// actually stops the work instead of merely hiding it.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

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

    // ── Database load state ──────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<string> _availableDatabases = [];
    [ObservableProperty] private bool _hasDatabases;
    [ObservableProperty] private bool _isLoadingDatabases;
    [ObservableProperty] private string? _connectionStatusMessage;

    // ── Version info ─────────────────────────────────────────────────────────

    [ObservableProperty] private string? _serverVersion;
    [ObservableProperty] private int? _serverMajorVersion;

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

        // …and abandon a load still in flight, which belongs to the server just
        // left: it would list THAT host's catalogs under this name, and on its
        // success path persist the pair now in the boxes — which survives this
        // setter, see below — under a key it never authenticated against. That
        // entry would then be trusted by the one caller allowed to arm the
        // auto-connect, and the store would launder exactly the disclosure the
        // guard in ScheduleAutoConnect denies. Found by the 2026-09-05 review.
        _loadCts?.Cancel();

        // …and the chosen one with them. A database name is the one field that
        // provably belongs to the server: keeping it would leave OK enabled
        // against a catalog nobody has confirmed exists on the host just named.
        // Measured before writing: all six call sites that assign both fields
        // write DatabaseName AFTER ServerName, so none of them loses a value it
        // was about to set.
        DatabaseName = string.Empty;

        // The credentials the user TYPED, by contrast, stay. Wiping them here
        // was the 2026-08-18 answer to a real problem — a login typed for the
        // previous server being sent 450 ms later to a host that may have
        // arrived from an unauthenticated UDP scan reply — but it solved it by
        // destroying what the user had just typed, per keystroke, with no
        // message. Someone who goes back to append "\SQLSTERI" loses the
        // secret they were halfway through; someone who fills the credentials
        // while the scan is still running and then picks the server from
        // "Risultati scansione" — the ordinary gesture — loses them too. The
        // security goal never needed that: what must not happen is the
        // AUTO-CONNECT firing with a pair that belongs to another server, and
        // that is now denied in ScheduleAutoConnect itself. Pressing «Connetti»
        // with the server name on screen stays the user's own explicit act.
        //
        // A pair the STORE put in is another matter: it was filed under the
        // server just left, nobody typed a character of it, and left in the box
        // it is exactly what «Connetti» then sends to the wrong host — the
        // owner's smoke of 2026-09-17. It follows its server: cleared here, put
        // back by the auto-fill below if the new name is remembered too. An
        // edit to either field makes the pair the user's (the setters clear
        // the vouching), and the rule above takes over.
        if (_vouchedServer is not null) { Password = string.Empty; }

        // Refresh IP from suggestions list (if any).
        ServerIpAddress = ServerSuggestions
            .FirstOrDefault(s => string.Equals(s.Name, value, StringComparison.OrdinalIgnoreCase))?.IpAddress;

        // These two are in this order and it is not cosmetic. This call cancels
        // whatever the previous server had armed, and re-arms only under Windows
        // auth; the auto-fill below then gets the last word, because it is the
        // one caller allowed to arm with a real credential pair. Written the
        // other way round — as it was until 2026-09-03, when clearing the fields
        // made the difference invisible — this call would CANCEL the arm the
        // auto-fill had just made and refuse to replace it, and "pick a
        // remembered server and it connects itself" would quietly stop working.
        _vouchedServer = null;
        ScheduleAutoConnect();
        _ = TryAutoFillCredentialsAsync(value);
    }

    partial void OnAuthModeChanged(AuthenticationMode value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
        // Cancels whatever was armed, and re-arms only if the pair in the boxes
        // is one the store vouched for THIS server (see _vouchedServer). Every
        // bulk assignment — load, swap, clone, copy — sets AuthMode right after
        // ServerName, so without that a panel that was in Windows auth lost the
        // arm the auto-fill had just made and never connected by itself. Found
        // by the 2026-09-05 review.
        ScheduleAutoConnect();
    }

    partial void OnDatabaseNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    // A credential being TYPED is never a signal that it is finished. Both
    // fields commit per keystroke (Avalonia's UpdateSourceTrigger.Default is
    // PropertyChanged), so scheduling the auto-connect from here sent a real
    // login with whatever prefix had been typed as soon as the user paused
    // longer than the 450 ms debounce — reaching for a symbol is enough. The
    // reply is "Login failed for user 'sa'." in the modal, for a connection
    // nobody asked for, and every further pause fires another: that is what an
    // account-lockout policy counts. Reported from the installed v1.1.0 on
    // 2026-09-03, alongside the password field that was not on screen at all.
    //
    // The auto-connect is NOT removed — it is re-armed where the credentials
    // really are complete: TryAutoFillCredentialsAsync, which puts back what
    // DPAPI stored for THIS server. Anything the user types waits for
    // «Connetti», which is always on screen.
    partial void OnUserNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
        // An edited pair is nobody's pair any more: whatever the store vouched
        // for is void the moment either field changes. The auto-fill sets both
        // fields through here too, and vouches only AFTER, so it is exempt.
        _vouchedServer = null;
    }

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(IsValid));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
        _vouchedServer = null;
    }

    partial void OnIsLoadingDatabasesChanged(bool value) =>
        LoadDatabasesCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanLoadDatabases))]
    public async Task LoadDatabasesAsync()
    {
        // The load runs on its own token, linked to the dialog's lifetime, so
        // it dies for two reasons: the window closing, and a different server
        // being named while it is in flight (OnServerNameChanged cancels it).
        // Everything after the await below is keyed on the LIVE ServerName —
        // the list, the credential persist — and must not land under a host
        // this connection never spoke to.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loadCts = cts;
        CancellationToken ct = cts.Token;

        IsLoadingDatabases = true;
        HasDatabases = false;
        AvailableDatabases.Clear();
        ConnectionStatusMessage = null;
        try
        {
            string cs = BuildConnectionString(includeDatabase: false);
            IReadOnlyList<string> dbs =
                await SqlServerDiscovery.ListDatabasesAsync(cs, ct)
                                        .ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
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
                    .GetServerVersionAsync(cs, ct)
                    .ConfigureAwait(true);
                ServerVersion = version;
                if (version is not null)
                {
                    // Parse the major version number from the connection string builder.
                    // We re-query SERVERPROPERTY via the same connection string, but
                    // the version string itself starts with the product year — extract major
                    // from the connection (the method already succeeded so the server is reachable).
                    int? major = await TryGetMajorVersionAsync(cs, ct)
                        .ConfigureAwait(true);
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
            // A load that was cancelled — superseded by a newer server name, or
            // the dialog gone — is not an error anybody asked about, and SqlClient
            // may surface the cancellation as a SqlException rather than an
            // OperationCanceledException, hence the token and not the type.
            if (!ct.IsCancellationRequested)
            {
                ConnectionStatusMessage =
                    $"Errore: {Persistence.Util.ConnectionStringRedactor.Redact(ex.Message)}";
            }
        }
        finally
        {
            IsLoadingDatabases = false;
        }
    }

    private static async Task<int?> TryGetMajorVersionAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            Microsoft.Data.SqlClient.SqlConnectionStringBuilder b = new(connectionString)
            {
                ConnectTimeout = 5,
            };
            await using Microsoft.Data.SqlClient.SqlConnection cn = new(b.ConnectionString);
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using Microsoft.Data.SqlClient.SqlCommand cmd = new(
                "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT);", cn);
            object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
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

    internal string BuildConnectionString(bool includeDatabase = false) =>
        EndpointConnectionString.Build(
            ServerName,
            includeDatabase ? DatabaseName : null,
            AuthMode,
            UserName,
            Password,
            TrustServerCertificate,
            Encrypt);

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

        // A project carries no password, and the one in the box belongs to
        // whatever server was named before «Carica…». Since naming a server no
        // longer wipes the credentials, leaving it here made the loaded panel
        // valid with the previous server's secret behind the dots — one OK away
        // from a login to the project's host with a password entered for
        // another. Cleared BEFORE the server name, so the auto-fill that the
        // setter runs can put back what the store holds for THIS server.
        Password = string.Empty;

        // Runtime state first, not last: the auto-fill below arms the
        // auto-connect only while no list is loaded, and the setter clears
        // this anyway when the name changes.
        AvailableDatabases.Clear();
        HasDatabases = false;

        // The setter does not run for an unchanged name — so a project for the
        // server already on screen cleared the password above and nothing put
        // it back. Found by the owner's smoke of 2026-09-17: pick a remembered
        // server, «Carica…» a project for it, empty box.
        bool sameServer = string.Equals(ServerName, endpoint.Connection.ServerName, StringComparison.Ordinal);
        ServerName = endpoint.Connection.ServerName;
        if (sameServer) { _ = TryAutoFillCredentialsAsync(ServerName); }
        DatabaseName = endpoint.Connection.DatabaseName;
        AuthMode = endpoint.Authentication.Mode;
        UserName = endpoint.Authentication.UserName ?? "";
        RememberCredentials = endpoint.Authentication.RememberCredentials;
        Encrypt = endpoint.Authentication.Encrypt;
        TrustServerCertificate = endpoint.Authentication.TrustServerCertificate;

        // The rest of the runtime state — but NOT ServerSuggestions: those come
        // from the network scan and the connection store, not from the project,
        // and dropping them left the picker empty right after a load.
        ServerVersion = null;
        ServerMajorVersion = null;
        ServerIpAddress = null;
        ScanStatusMessage = null;
        ConnectionStatusMessage = null;
    }
}
