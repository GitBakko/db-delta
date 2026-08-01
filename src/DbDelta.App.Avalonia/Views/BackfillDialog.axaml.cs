using Avalonia.Controls;
using Avalonia.Interactivity;
using DbDelta.App.ViewModels;

namespace DbDelta.App.Views;

/// <summary>
/// Asks the operator what to put in the rows that already exist, for every
/// column a run would add as NOT NULL without a default (Msg 4901). Closes with
/// the map <c>ScriptGenerator</c> takes, or with <c>null</c> when the user
/// backed out — and backing out means no script and no execution, because the
/// alternative is a deploy that dies halfway through on the first populated
/// table.
/// </summary>
public partial class BackfillDialog : Window
{
    /// <summary>Creates the dialog. The data context is a <see cref="BackfillViewModel"/>.</summary>
    public BackfillDialog()
    {
        InitializeComponent();
    }

    private void OnAnnullaClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApplicaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is BackfillViewModel { CanConfirm: true } vm)
        {
            Close(vm.ToMap());
        }
    }
}
