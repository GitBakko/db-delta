using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Sql;
using Microsoft.Data.SqlClient;

namespace DbDelta.App.ViewModels;

public sealed partial class ConnectionEditViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTime _createdUtc;

    public ConnectionEditViewModel(ConnectionEntry entry, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _id = entry.Id;
        _createdUtc = entry.CreatedUtc;
        Name = entry.Name;
        ServerName = entry.ServerName;
        DatabaseName = entry.DatabaseName;
        ConnectionStringTemplate = entry.ConnectionStringTemplate;
        EnvironmentTag = entry.EnvironmentTag;
        EnvironmentColorHex = entry.EnvironmentColorHex;
        IsPinned = entry.IsPinned;
        Password = password ?? "";
        UserName = ExtractUserId(entry.ConnectionStringTemplate) ?? "sa";
    }

    /// <summary>True when the form is creating a brand-new entry.
    /// Used by the dialog to hide the "Elimina" button.</summary>
    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private string _databaseName = "";
    [ObservableProperty] private string _connectionStringTemplate = "";
    [ObservableProperty] private string _environmentTag = "";
    [ObservableProperty] private string _environmentColorHex = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isPasswordVisible;

    [ObservableProperty]
    private string _userName = "sa";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string? _scanStatusMessage;

    [ObservableProperty]
    private ObservableCollection<DiscoveredServer> _serverSuggestions = [];

    [ObservableProperty]
    private bool _hasServerSuggestions;

    [ObservableProperty]
    private ObservableCollection<string> _availableDatabases = [];

    [ObservableProperty]
    private bool _hasDatabases;

    [ObservableProperty]
    private bool _isLoadingDatabases;

    [ObservableProperty]
    private string? _connectionStatusMessage;

    public IReadOnlyList<EnvironmentColorOption> ColorPalette => EnvironmentColorPalette.All;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(EnvironmentColorHex)
        && EnvironmentColorHex.StartsWith('#');

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsValid));
    partial void OnEnvironmentColorHexChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnServerNameChanged(string value) => LoadDatabasesCommand.NotifyCanExecuteChanged();
    partial void OnUserNameChanged(string value) => LoadDatabasesCommand.NotifyCanExecuteChanged();
    partial void OnPasswordChanged(string value) => LoadDatabasesCommand.NotifyCanExecuteChanged();
    partial void OnIsLoadingDatabasesChanged(bool value) => LoadDatabasesCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    public void PickColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            EnvironmentColorHex = hex;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    public async Task LoadServerSuggestionsAsync()
    {
        IsScanning = true;
        HasServerSuggestions = false;
        ScanStatusMessage = "Scansione in corso…";
        try
        {
            IReadOnlyList<DiscoveredServer> list =
                await SqlServerDiscovery.EnumerateServersAsync(CancellationToken.None);
            ServerSuggestions.Clear();
            foreach (DiscoveredServer s in list) { ServerSuggestions.Add(s); }
            HasServerSuggestions = list.Count > 0;
            ScanStatusMessage = list.Count == 0
                ? "Nessun server rilevato (SQL Browser potrebbe essere disabilitato sui server di rete)."
                : $"Trovati {list.Count} server. Clicca su uno per selezionarlo.";
        }
        catch (Exception ex)
        {
            ScanStatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    public void PickServer(DiscoveredServer? server)
    {
        if (server is not null && !string.IsNullOrWhiteSpace(server.Name))
        {
            ServerName = server.Name;
        }
    }

    private bool CanScan() => !IsScanning;

    partial void OnIsScanningChanged(bool value) => LoadServerSuggestionsCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanLoadDatabases))]
    public async Task LoadDatabasesAsync()
    {
        IsLoadingDatabases = true;
        HasDatabases = false;
        AvailableDatabases.Clear();
        ConnectionStatusMessage = null;
        try
        {
            string raw = BuildConnectionString(includeDatabase: false);
            IReadOnlyList<string> dbs = await SqlServerDiscovery.ListDatabasesAsync(raw, CancellationToken.None);
            foreach (string db in dbs)
            {
                AvailableDatabases.Add(db);
            }

            HasDatabases = dbs.Count > 0;
            ConnectionStatusMessage = $"Connesso — {dbs.Count} database disponibili";
        }
        catch (Exception ex)
        {
            ConnectionStatusMessage = $"Errore: {DbDelta.Persistence.Util.ConnectionStringRedactor.Redact(ex.Message)}";
        }
        finally
        {
            IsLoadingDatabases = false;
        }
    }

    private bool CanLoadDatabases() =>
        !IsLoadingDatabases
        && !string.IsNullOrWhiteSpace(ServerName)
        && !string.IsNullOrWhiteSpace(UserName)
        && !string.IsNullOrWhiteSpace(Password);

    private string BuildConnectionString(bool includeDatabase)
    {
        string db = includeDatabase && !string.IsNullOrWhiteSpace(DatabaseName)
            ? $"Database={DatabaseName};"
            : "";
        return $"Server={ServerName};{db}User Id={UserName};Password={Password};TrustServerCertificate=True";
    }

    public ConnectionEntry ToEntry()
    {
        string template = $"Server={ServerName};Database={DatabaseName};User Id={UserName};Password={{PASSWORD}};TrustServerCertificate=True";
        return new ConnectionEntry(
            Id: _id,
            Name: Name,
            ServerName: ServerName,
            DatabaseName: DatabaseName,
            ConnectionStringTemplate: template,
            EnvironmentTag: EnvironmentTag,
            EnvironmentColorHex: EnvironmentColorHex,
            IsPinned: IsPinned,
            CreatedUtc: _createdUtc,
            LastUsedUtc: DateTime.UtcNow);
    }

    private static string? ExtractUserId(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }
        try
        {
            SqlConnectionStringBuilder b = new(template);
            string? uid = b.UserID;
            // Guard against placeholder values used by the "new entry" template.
            return string.IsNullOrWhiteSpace(uid) || IsPlaceholder(uid) ? null : uid;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPlaceholder(string value) =>
        value.StartsWith('{') && value.EndsWith('}');
}
