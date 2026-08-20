using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Shared.Dtos;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// Five column headers carried <c>CanUserSort="True"</c> and no
/// <c>SortMemberPath</c>, which in a DataGridTemplateColumn is the only thing
/// that says WHAT to sort by: the header drew the arrow, the click did
/// nothing, and the grid promised an ordering it never delivered. These
/// assertions are about the promise being kept — and about it being kept on
/// the value, not on the string the cell prints.
/// </summary>
public class ResultsGridSortTests
{
    private static (ResultsGridView View, MainWindowViewModel Vm) Build()
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);
        DifferenceDto[] diffs =
        [
            // The dates are the trap: printed dd/MM/yyyy they sort in the
            // OPPOSITE order to the instants they stand for.
            new("View", "dbo", "Zeta", "Different", new DateTime(2025, 12, 31, 10, 0, 0), new DateTime(2025, 1, 1, 0, 0, 0)),
            new("Table", "dbo", "Alfa", "Different", new DateTime(2026, 2, 1, 10, 0, 0), new DateTime(2026, 6, 1, 0, 0, 0)),
        ];
        DifferencePair[] pairs = [.. diffs.Select(d => new DifferencePair(
            Identity: new ObjectIdentity(d.SchemaName, d.ObjectName, d.Kind),
            Status: DifferenceStatus.Different,
            SideA: null,
            SideB: null))];
        appState.PublishComparison(new ComparisonResult(pairs), "src", "tgt");
        appState.LastComparison = new ComparisonResultDto([.. diffs]);

        ResultsGridView view = new() { DataContext = vm };
        Window host = new() { Content = view, Width = 1200, Height = 600 };
        host.Show();
        return (view, vm);
    }

    private static DataGrid Grid(ResultsGridView view) =>
        view.GetVisualDescendants().OfType<DataGrid>().Single();

    private static string PathOf(ResultsGridView view, string header) =>
        Grid(view).Columns.Single(c => (c.Header as string) == header).SortMemberPath;

    private static IReadOnlyList<string> NamesSortedBy(MainWindowViewModel vm, string path)
    {
        vm.RowsView.SortDescriptions.Clear();
        vm.RowsView.SortDescriptions.Add(DataGridSortDescription.FromPath(path));
        return [.. vm.RowsView.Cast<DifferenceRowViewModel>().Select(r => r.ObjectName)];
    }

    [AvaloniaFact]
    public void Every_column_that_offers_a_sort_names_something_to_sort_by()
    {
        (ResultsGridView view, _) = Build();

        foreach (DataGridColumn column in Grid(view).Columns.Where(c => c.CanUserSort))
        {
            string header = column.Header as string ?? "(senza intestazione)";
            column.SortMemberPath.Should().NotBeNullOrWhiteSpace(
                $"the header \"{header}\" offers a sort, and a template column sorts by nothing else");
            typeof(DifferenceRowViewModel).GetProperty(column.SortMemberPath)
                .Should().NotBeNull($"\"{header}\" sorts by {column.SortMemberPath}, which has to be a real property");
        }
    }

    [AvaloniaFact]
    public void The_name_column_sorts_by_the_qualified_name()
    {
        (ResultsGridView view, MainWindowViewModel vm) = Build();

        NamesSortedBy(vm, PathOf(view, "Nome (orig)")).Should().Equal("Alfa", "Zeta");
    }

    [AvaloniaFact]
    public void The_date_columns_sort_by_the_instant_and_not_by_the_printed_string()
    {
        (ResultsGridView view, MainWindowViewModel vm) = Build();

        // "31/12/2025" sorts BEFORE "01/02/2026" only if the sort reads the
        // DateTime. As text it is the other way round, which is exactly the
        // ordering a SortMemberPath pointed at the display string produces.
        NamesSortedBy(vm, PathOf(view, "Ultima modifica (orig)")).Should().Equal("Zeta", "Alfa");
        NamesSortedBy(vm, PathOf(view, "Ultima modifica (dest)")).Should().Equal("Zeta", "Alfa");
    }

    [AvaloniaFact]
    public void The_checkbox_column_still_offers_no_sort()
    {
        // The negative control: a column with nothing to order by must keep
        // saying so, or the rule above turns into "give every column a path".
        (ResultsGridView view, _) = Build();

        Grid(view).Columns.Should().Contain(c => !c.CanUserSort);
    }
}
