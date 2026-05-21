using Avalonia.Controls;
using Avalonia.Interactivity;
using DbDelta.App.ViewModels;
using DbDelta.Core.Abstractions;

namespace DbDelta.App.Views;

public partial class ConnectionManagerDialog : Window
{
    public ConnectionManagerDialog()
    {
        InitializeComponent();
    }

    private async void OnNewClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionStoreViewModel cs)
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

        ConnectionEditViewModel vm = new(blank, password: "") { IsNew = true };
        ConnectionEditDialog dialog = new() { DataContext = vm };
        ConnectionEditDialog.Result result = await dialog.ShowDialog<ConnectionEditDialog.Result>(this);
        if (result == ConnectionEditDialog.Result.Save)
        {
            await cs.UpsertExplicitAsync(vm.ToEntry(), vm.Password, System.Threading.CancellationToken.None);
        }
    }

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid id || DataContext is not ConnectionStoreViewModel cs)
        {
            return;
        }

        ConnectionEntry? entry = null;
        foreach (ConnectionEntry candidate in cs.Entries)
        {
            if (candidate.Id == id)
            {
                entry = candidate;
                break;
            }
        }

        if (entry is null)
        {
            return;
        }

        string? password = await cs.GetSecretPasswordAsync(entry.Id, System.Threading.CancellationToken.None);
        ConnectionEditViewModel vm = new(entry, password) { IsNew = false };
        ConnectionEditDialog dialog = new() { DataContext = vm };
        ConnectionEditDialog.Result result = await dialog.ShowDialog<ConnectionEditDialog.Result>(this);
        if (result == ConnectionEditDialog.Result.Save)
        {
            await cs.UpsertExplicitAsync(vm.ToEntry(), vm.Password, System.Threading.CancellationToken.None);
        }
        else if (result == ConnectionEditDialog.Result.Delete)
        {
            await cs.DeleteAsync(entry.Id, System.Threading.CancellationToken.None);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid id || DataContext is not ConnectionStoreViewModel cs)
        {
            return;
        }

        await cs.DeleteAsync(id, System.Threading.CancellationToken.None);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
