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
/// The two select-all boxes live in XAML, so the view-model tests cannot see
/// them: a binding typo, a column renumbering or a lost Header leaves the
/// commands perfectly working and the user with nothing to click. These
/// assertions are about the CONTROLS existing and being wired.
/// </summary>
public class ResultsGridSelectionTests
{
    private static (ResultsGridView View, MainWindowViewModel Vm) Build()
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);
        DifferenceDto[] diffs =
        [
            new("Table", "dbo", "Orders", "Different"),
            new("Table", "dbo", "Unchanged", "Identical"),
        ];
        DifferencePair[] pairs = [.. diffs.Select(d => new DifferencePair(
            Identity: new ObjectIdentity(d.SchemaName, d.ObjectName, d.Kind),
            Status: d.Status == "Identical" ? DifferenceStatus.Identical : DifferenceStatus.Different,
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

    /// <summary>
    /// The global box sits in the checkbox column's header, above the per-row
    /// boxes it drives. It was briefly in the action bar instead, which read as
    /// unrelated to the column it controls.
    /// </summary>
    [AvaloniaFact]
    public void The_checkbox_column_header_shows_a_wired_select_all_box()
    {
        (ResultsGridView view, MainWindowViewModel vm) = Build();

        // Found in the REALISED visual tree, not on the column object: a live
        // CheckBox assigned to Header passed that weaker check while never
        // appearing on screen, because a control has one visual parent and the
        // header row is rebuilt.
        CheckBox box = Grid(view).GetVisualDescendants()
            .OfType<DataGridColumnHeader>()
            .SelectMany(h => h.GetVisualDescendants().OfType<CheckBox>())
            .Should().ContainSingle("the checkbox column's header carries the select-all box")
            .Subject;

        box.Command.Should().BeSameAs(vm.ToggleAllVisibleCommand,
            "the header box must drive the bulk command, not just look like a checkbox");
        box.IsThreeState.Should().BeTrue("a partial selection has to be distinguishable from none");
        box.IsVisible.Should().BeTrue();
        box.Bounds.Width.Should().BeGreaterThan(0, "a zero-width box is one nobody can click");
    }

    /// <summary>
    /// The checkbox column is the one the group header mirrors, so its width
    /// and position are load-bearing: the group template hard-codes the same
    /// column list and nothing enforces that they stay in step.
    /// </summary>
    [AvaloniaFact]
    public void The_grid_columns_still_match_what_the_group_header_template_mirrors()
    {
        (ResultsGridView view, _) = Build();

        double?[] widths = [.. Grid(view).Columns.Select(c => c.Width.IsAbsolute ? c.Width.Value : (double?)null)];

        widths.Should().Equal(
            [100, 140, null, 40, null, 140],
            "ResultsGridView's group-header template hard-codes 10,100,140,*,40,*,140 to line its "
            + "select-all box up with the row boxes — change one and you must change the other");
    }
}
