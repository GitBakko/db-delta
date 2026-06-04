using Avalonia.Controls;
using Avalonia.Interactivity;
using DbDelta.App.ViewModels;

namespace DbDelta.App.Views;

/// <summary>
/// Direct-execution modal: confirmation summary (what runs, from where, to
/// where), in-dialog execution with busy state, and the success / failure
/// outcome panel. The owner reads <see cref="ConfirmExecuteViewModel.Result"/>
/// after the dialog closes — <c>null</c> means the user cancelled before
/// executing. The window cannot be closed while the script is running.
/// </summary>
public partial class ConfirmExecuteDialog : Window
{
    public ConfirmExecuteDialog()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is ConfirmExecuteViewModel { IsRunning: true })
        {
            e.Cancel = true; // the script owns a transaction — no mid-flight close
        }
    }

    private void OnAnnullaClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnChiudiClick(object? sender, RoutedEventArgs e) => Close(null);
}
