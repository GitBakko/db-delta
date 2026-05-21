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

        Button reveal = this.FindControl<Button>("RevealButton")!;
        reveal.AddHandler(PointerPressedEvent, OnRevealPressed, RoutingStrategies.Tunnel);
        reveal.AddHandler(PointerReleasedEvent, OnRevealReleased, RoutingStrategies.Tunnel);

        // After the VM finishes its scan, pop the server dropdown via the
        // dispatcher so layout has a chance to settle. The inline ListBox
        // fallback below the input is the guaranteed visibility path; the
        // popup is a convenience.
        this.DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.ConnectionEditViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ViewModels.ConnectionEditViewModel.IsScanning)
                        && !vm.IsScanning
                        && vm.ServerSuggestions.Count > 0)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            AutoCompleteBox? box = this.FindControl<AutoCompleteBox>("ServerBox");
                            if (box is null) { return; }
                            box.Focus();
                            box.IsDropDownOpen = true;
                        }, DispatcherPriority.Background);
                    }
                };
            }
        };
    }

    private void OnServerDropToggleClick(object? sender, RoutedEventArgs e)
    {
        AutoCompleteBox? box = this.FindControl<AutoCompleteBox>("ServerBox");
        if (box is null) { return; }
        box.Focus();
        box.IsDropDownOpen = !box.IsDropDownOpen;
    }

    private void OnRevealPressed(object? sender, PointerPressedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("PasswordBox")!;
        box.PasswordChar = '\0';
    }

    private void OnRevealReleased(object? sender, PointerReleasedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("PasswordBox")!;
        box.PasswordChar = '•';
    }

    private void OnColorSwatchTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is Avalonia.Controls.Border b
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
