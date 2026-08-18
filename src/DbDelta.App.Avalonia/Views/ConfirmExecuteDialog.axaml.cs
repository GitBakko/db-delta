using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    /// <summary>
    /// Puts the script pane back at its first line when the section opens.
    /// </summary>
    /// <remarks>
    /// Without this it opens at the BOTTOM-RIGHT — measured live, not guessed:
    /// UI Automation reported the pane at <c>HorizontalScrollPercent = 100</c>
    /// and <c>VerticalScrollPercent = 100</c> the moment it expanded, so the
    /// reader met the last lines of the script with the first characters of
    /// each one clipped. This section exists so someone can check what is about
    /// to run against a live database; landing at the end of it defeats the one
    /// job it has.
    /// </remarks>
    /// <remarks>
    /// Posted at <see cref="DispatcherPriority.Loaded"/> rather than set inline.
    /// A plain <c>ScrollToHome()</c> in this handler is undone: the expansion
    /// realises the content and the layout pass that follows puts the offset
    /// back — measured, the pane returned to Y = 59538 with the inline call in
    /// place. The reset has to happen after that pass, not before it.
    /// </remarks>
    private void OnScriptExpanded(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => ScriptPane.Offset = default, DispatcherPriority.Loaded);

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
