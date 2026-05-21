using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
