using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbDelta.App.Views;

public partial class ConnectionEditDialog : Window
{
    public enum Result { Save, Delete, Cancel }

    public ConnectionEditDialog()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(Result.Save);
    private void OnDeleteClick(object? sender, RoutedEventArgs e) => Close(Result.Delete);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(Result.Cancel);
}
