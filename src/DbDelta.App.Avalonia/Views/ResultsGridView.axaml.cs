using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using DbDelta.App.ViewModels;

namespace DbDelta.App.Views;

public partial class ResultsGridView : UserControl
{
    public ResultsGridView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initialises the "Identici" status group COLLAPSED when grouping by
    /// difference type — identical rows are confirmation noise the user opens
    /// on demand. Other groups (and the per-kind grouping) keep the DataGrid
    /// default of expanded. Collapsing is posted to the dispatcher because the
    /// group header is still being materialised while LoadingRowGroup fires.
    /// </summary>
    private void OnLoadingRowGroup(object? sender, DataGridRowGroupHeaderEventArgs e)
    {
        if (e.RowGroupHeader.DataContext is DataGridCollectionViewGroup group
            && group.Key is DifferenceRowViewModel.IdenticalGroupLabel)
        {
            Dispatcher.UIThread.Post(
                () => ResultsGrid.CollapseRowGroup(group, collapseAllSubgroups: true),
                DispatcherPriority.Background);
        }
    }
}
