using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DbDelta.App.ViewModels;
using DbDelta.Persistence.Sql;

namespace DbDelta.App.Views;

/// <summary>
/// Everything SQL Server said during the most recent execution — every error it
/// raised, and every PRINT that named the object being worked on when it did.
/// Read-only: the run is over.
/// </summary>
public partial class LastRunDialog : Window
{
    /// <summary>Creates the dialog. The data context is a <see cref="LastRunViewModel"/>.</summary>
    public LastRunDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Puts the whole transcript on the clipboard, which is how it reaches a
    /// ticket or a DBA. Silent when there is no clipboard (headless).
    /// </summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LastRunViewModel vm || Clipboard is null) { return; }
        await Clipboard.SetValueAsync(DataFormat.Text, Transcribe(vm)).ConfigureAwait(true);
    }

    /// <summary>
    /// Renders a run the way SSMS prints one: the header line above the message
    /// for errors, the bare text for a PRINT.
    /// </summary>
    public static string Transcribe(LastRunViewModel run)
    {
        ArgumentNullException.ThrowIfNull(run);
        StringBuilder sb = new();
        sb.AppendLine(run.Headline);
        sb.AppendLine(run.Subline);
        sb.AppendLine();
        foreach (SqlServerMessage message in run.Messages)
        {
            if (message.IsError) { sb.AppendLine(message.Header); }
            sb.AppendLine(message.Text);
        }
        if (run.Messages.Count == 0) { sb.AppendLine(run.EmptyText); }
        return sb.ToString();
    }
}
