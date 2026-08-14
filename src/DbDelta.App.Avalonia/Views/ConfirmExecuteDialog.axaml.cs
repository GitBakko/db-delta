using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

    /// <summary>
    /// Puts the script on the clipboard, which is how it reaches a review, a
    /// ticket or SSMS. Silent when there is no clipboard (headless).
    /// </summary>
    private async void OnCopyScriptClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfirmExecuteViewModel vm || Clipboard is null) { return; }
        await Clipboard.SetValueAsync(DataFormat.Text, vm.Script).ConfigureAwait(true);
    }
}
