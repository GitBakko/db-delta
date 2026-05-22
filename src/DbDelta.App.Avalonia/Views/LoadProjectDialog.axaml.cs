using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbDelta.Persistence.Json;

namespace DbDelta.App.Views;

/// <summary>
/// Load-project dialog. Shows the MRU list with save-date and lets the user
/// pick one entry, or browse a .dbd file from disk for back-compat /
/// special workflows. Dialog result is the resolved absolute path on OK,
/// <see langword="null"/> on cancel.
/// </summary>
public partial class LoadProjectDialog : Window
{
    public LoadProjectDialog()
    {
        InitializeComponent();
    }

    /// <summary>Populates the MRU list. Pass the full RecentProject record so
    /// the per-row template can render the save-date column.</summary>
    public void SetRecentProjects(IReadOnlyList<RecentProject> projects)
    {
        ListBox list = this.FindControl<ListBox>("ProjectsList")!;
        list.ItemsSource = projects;
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        AcceptSelection();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        AcceptSelection();
    }

    private void AcceptSelection()
    {
        ListBox list = this.FindControl<ListBox>("ProjectsList")!;
        if (list.SelectedItem is RecentProject rp && !string.IsNullOrWhiteSpace(rp.Path))
        {
            Close(rp.Path);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        FilePickerOpenOptions opts = new()
        {
            Title = "Carica progetto DbDelta",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Progetto DbDelta") { Patterns = ["*.dbd"] },
            ],
        };
        IReadOnlyList<IStorageFile> picked =
            await StorageProvider.OpenFilePickerAsync(opts).ConfigureAwait(true);
        if (picked.Count == 0) { return; }
        Close(picked[0].Path.LocalPath);
    }
}
