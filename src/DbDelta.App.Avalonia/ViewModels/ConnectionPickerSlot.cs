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
            TestConnectionCommand.NotifyCanExecuteChanged();
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
        EditCommand.NotifyCanExecuteChanged();
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

    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanTest))]
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

    private bool CanTest() => !IsTesting && !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Opens the ConnectionEditDialog for the currently selected entry.
    /// Caller passes the owning Window so the modal can be parented correctly.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    public async Task EditAsync(Avalonia.Controls.Window? owner)
    {
        if (owner is null || state.Connections is null || SelectedEntry is null)
        {
            return;
        }
        string? password = null;
        try
        {
            Microsoft.Data.SqlClient.SqlConnectionStringBuilder b = new(ConnectionString);
            password = b.Password;
        }
        catch { /* best effort */ }

        ConnectionEditViewModel vm = new(SelectedEntry, password);
        Views.ConnectionEditDialog dialog = new() { DataContext = vm };
        Views.ConnectionEditDialog.Result result = await dialog.ShowDialog<Views.ConnectionEditDialog.Result>(owner);
        if (result == Views.ConnectionEditDialog.Result.Save)
        {
            ConnectionEntry updated = vm.ToEntry();
            await state.Connections.UpsertExplicitAsync(updated, vm.Password, CancellationToken.None);
            SelectedEntry = updated;
            string? materialised = await state.Connections.MaterialiseAsync(updated, CancellationToken.None);
            if (materialised is not null)
            {
                ConnectionString = materialised;
            }
        }
        else if (result == Views.ConnectionEditDialog.Result.Delete)
        {
            Guid id = SelectedEntry.Id;
            SelectedEntry = null;
            await state.Connections.DeleteAsync(id, CancellationToken.None);
        }
    }

    private bool CanEdit() => SelectedEntry is not null;

    /// <summary>
    /// Opens the ConnectionEditDialog for a brand-new entry (no Id collision).
    /// </summary>
    [RelayCommand]
    public async Task NewAsync(Avalonia.Controls.Window? owner)
    {
        if (owner is null || state.Connections is null)
        {
            return;
        }
        ConnectionEntry blank = new(
            Id: Guid.NewGuid(),
            Name: "Nuova connessione",
            ServerName: "",
            DatabaseName: "",
            ConnectionStringTemplate: "Server={SERVER};Database={DATABASE};User Id={USER};Password={PASSWORD};TrustServerCertificate=True",
            EnvironmentTag: "Dev",
            EnvironmentColorHex: "#0054BD",
            IsPinned: false,
            CreatedUtc: DateTime.UtcNow,
            LastUsedUtc: DateTime.UtcNow);
        ConnectionEditViewModel vm = new(blank, password: "");
        Views.ConnectionEditDialog dialog = new() { DataContext = vm };
        Views.ConnectionEditDialog.Result result = await dialog.ShowDialog<Views.ConnectionEditDialog.Result>(owner);
        if (result == Views.ConnectionEditDialog.Result.Save)
        {
            ConnectionEntry created = vm.ToEntry();
            await state.Connections.UpsertExplicitAsync(created, vm.Password, CancellationToken.None);
            SelectedEntry = created;
        }
    }
}
