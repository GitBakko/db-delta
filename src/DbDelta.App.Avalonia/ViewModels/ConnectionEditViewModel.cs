using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;

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
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private string _databaseName = "";
    [ObservableProperty] private string _connectionStringTemplate = "";
    [ObservableProperty] private string _environmentTag = "";
    [ObservableProperty] private string _environmentColorHex = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isPasswordVisible;

    public IReadOnlyList<EnvironmentColorOption> ColorPalette => EnvironmentColorPalette.All;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(EnvironmentColorHex)
        && EnvironmentColorHex.StartsWith('#');

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsValid));
    partial void OnEnvironmentColorHexChanged(string value) => OnPropertyChanged(nameof(IsValid));

    [RelayCommand]
    public void PickColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex)) { EnvironmentColorHex = hex; }
    }

    public ConnectionEntry ToEntry() => new(
        Id: _id,
        Name: Name,
        ServerName: ServerName,
        DatabaseName: DatabaseName,
        ConnectionStringTemplate: ConnectionStringTemplate,
        EnvironmentTag: EnvironmentTag,
        EnvironmentColorHex: EnvironmentColorHex,
        IsPinned: IsPinned,
        CreatedUtc: _createdUtc,
        LastUsedUtc: DateTime.UtcNow);
}
