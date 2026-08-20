using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbDelta.App.Views;

/// <summary>
/// Yes/no confirmation for an action that throws work away. Returns
/// <see langword="true"/> when the user confirms, <see langword="false"/> on
/// cancel, Escape, or closing the window. Escape is <c>IsCancel</c> on the
/// cancel button itself — it used to be a KeyDown handler here, which is one
/// more place for the two to disagree. Enter is deliberately NOT bound: the
/// crimson button confirms something irreversible.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Fills the dialog. <paramref name="headline"/> states what is about to
    /// happen, <paramref name="body"/> what it costs, and
    /// <paramref name="confirmLabel"/> names the action on the crimson button
    /// (never "OK" — the button should say what it does).
    /// </summary>
    public static async Task<bool> AskAsync(
        Window owner,
        string title,
        string headline,
        string body,
        string confirmLabel)
    {
        ArgumentNullException.ThrowIfNull(owner);

        ConfirmDialog dialog = new() { Title = title };
        dialog.FindControl<TextBlock>("HeadlineText")!.Text = headline;
        dialog.FindControl<TextBlock>("BodyText")!.Text = body;
        dialog.FindControl<TextBlock>("ConfirmLabel")!.Text = confirmLabel;

        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);
}
