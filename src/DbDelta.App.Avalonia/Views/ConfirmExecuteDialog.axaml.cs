using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbDelta.App.Views;

/// <summary>
/// Confirmation modal shown before executing an alignment script directly against
/// the target database. Returns <see langword="true"/> when the user confirms,
/// or <see langword="null"/> / <see langword="false"/> when cancelled.
/// </summary>
public partial class ConfirmExecuteDialog : Window
{
    public ConfirmExecuteDialog()
    {
        InitializeComponent();
    }

    private void OnAnnullaClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnEseguiClick(object? sender, RoutedEventArgs e) => Close(true);
}
