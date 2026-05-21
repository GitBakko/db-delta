using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.ViewModels;

/// <summary>
/// Per-endpoint slot: wraps AppState.SourceConnectionString /
/// TargetConnectionString + a combo selection + the inline Test button.
/// </summary>
public sealed partial class ConnectionPickerSlot(AppStateViewModel state, bool isSource) : ObservableObject
{
    public string ConnectionString
    {
        get => isSource ? state.SourceConnectionString : state.TargetConnectionString;
        set
        {
            if (isSource) { state.SourceConnectionString = value; }
            else { state.TargetConnectionString = value; }
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private ConnectionEntry? _selectedEntry;

    [ObservableProperty]
    private string? _testResultMessage;

    [ObservableProperty]
    private bool _isTesting;

    async partial void OnSelectedEntryChanged(ConnectionEntry? value)
    {
        if (value is null || state.Connections is null)
        {
            return;
        }
        string? materialised = await state.Connections.MaterialiseAsync(value, CancellationToken.None);
        if (materialised is not null)
        {
            ConnectionString = materialised;
        }
    }

    [RelayCommand]
    public async Task TestConnectionAsync()
    {
        IsTesting = true;
        TestResultMessage = null;
        ConnectionTester.TestResult result = await ConnectionTester.TestAsync(ConnectionString, CancellationToken.None);
        TestResultMessage = result.Success
            ? $"✓ {result.Message}" + (result.ServerVersion is null ? "" : $" — {result.ServerVersion}")
            : $"✗ {result.Message}";
        IsTesting = false;
    }
}
