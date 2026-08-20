using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbDelta.App.Views;

/// <summary>
/// Minimal name-only save dialog. The user supplies a project name; the
/// caller resolves the on-disk path via
/// <see cref="Persistence.Json.ProjectsFolder.ResolvePath(string)"/>.
/// Returns the typed name on OK, <see langword="null"/> on cancel. Invio and
/// Esc are IsDefault / IsCancel on the two buttons: they used to be a KeyDown
/// handler on the text box, so they answered only while it had focus.
/// </summary>
public partial class SaveProjectDialog : Window
{
    public SaveProjectDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            TextBox box = this.FindControl<TextBox>("NameBox")!;
            box.Focus();
            box.SelectAll();
        };
    }

    /// <summary>Pre-fills the name input. Pass the current project name so
    /// re-saving doesn't force the user to retype it.</summary>
    public void SetInitialName(string? name)
    {
        TextBox box = this.FindControl<TextBox>("NameBox")!;
        box.Text = name ?? string.Empty;
    }

    /// <summary>Sets the explanatory hint text shown under the input field
    /// (e.g. the resolved on-disk path so users know where it lands).</summary>
    public void SetHint(string? hint)
    {
        TextBlock hintText = this.FindControl<TextBlock>("HintText")!;
        hintText.Text = hint ?? string.Empty;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        TextBox box = this.FindControl<TextBox>("NameBox")!;
        string name = (box.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) { return; }
        Close(name);
    }
}
