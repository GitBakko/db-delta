using Avalonia.Controls;
using DbDelta.App.ViewModels;

namespace DbDelta.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Routes a user pick on the topbar "I miei progetti" combo through the
    /// MainWindowViewModel's MRU command. We resolve Window + selected path
    /// here (XAML <c>CommandParameter</c> can't easily express a tuple) and
    /// hand both to OpenRecentProjectCommand.
    /// </summary>
    private async void OnProjectMruSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) { return; }
        if (DataContext is not MainWindowViewModel vm) { return; }
        if (combo.SelectedItem is not string path) { return; }

        // Reset the combo so the same entry can be re-selected later.
        combo.SelectedItem = null;

        await vm.OpenRecentProjectCommand
            .ExecuteAsync((this, path))
            .ConfigureAwait(true);
    }
}
