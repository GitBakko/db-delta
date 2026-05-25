using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace DbDelta.App.Views;

public partial class ConnectionEditDialog : Window
{
    public enum Result { Save, Delete, Cancel }

    public ConnectionEditDialog()
    {
        InitializeComponent();

        // After the VM finishes its scan, pop the server dropdown via the
        // dispatcher so layout has a chance to settle. The inline ListBox
        // fallback below the input is the guaranteed visibility path; the
        // popup is a convenience.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.ConnectionEditViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ViewModels.ConnectionEditViewModel.IsScanning)
                        && !vm.IsScanning
                        && vm.ServerSuggestions.Count > 0)
                    {
                        OpenDropdownDeferred("ServerBox");
                    }
                    else if (args.PropertyName == nameof(ViewModels.ConnectionEditViewModel.IsLoadingDatabases)
                             && !vm.IsLoadingDatabases
                             && vm.HasDatabases)
                    {
                        OpenDropdownDeferred("DatabaseBox");
                    }
                };
            }
        };
    }

    private void OpenDropdownDeferred(string boxName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AutoCompleteBox? box = this.FindControl<AutoCompleteBox>(boxName);
            if (box is null) { return; }
            box.Focus();
            box.IsDropDownOpen = true;
        }, DispatcherPriority.Background);
    }

    private void OnServerDropToggleClick(object? sender, RoutedEventArgs e)
    {
        AutoCompleteBox? box = this.FindControl<AutoCompleteBox>("ServerBox");
        if (box is null) { return; }
        box.Focus();
        box.IsDropDownOpen = !box.IsDropDownOpen;
    }

    private void OnDatabaseDropToggleClick(object? sender, RoutedEventArgs e)
    {
        AutoCompleteBox? box = this.FindControl<AutoCompleteBox>("DatabaseBox");
        if (box is null) { return; }
        box.Focus();
        box.IsDropDownOpen = !box.IsDropDownOpen;
    }

    private void OnColorSwatchTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border b
            && b.Tag is string hex
            && DataContext is ViewModels.ConnectionEditViewModel vm)
        {
            vm.EnvironmentColorHex = hex;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(Result.Save);
    private void OnDeleteClick(object? sender, RoutedEventArgs e) => Close(Result.Delete);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(Result.Cancel);
}
