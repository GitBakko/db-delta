using System.Diagnostics;
using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Shared.Dtos;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// The grid at 10.000 objects, which is the size nobody had ever put in front
/// of it. The engine emits one pair per object — Identical ones included — so
/// this is not an unreasonable catalog, it is a medium one.
/// </summary>
/// <remarks>
/// <para>
/// The bounds are deliberately loose. A number that fails on a busy CI runner
/// teaches everyone to re-run the build, which is worse than no number at all;
/// what these catch is a change of ORDER, the accidental O(n²) that turns a
/// tenth of a second into a minute. The measured figures at the time of writing
/// are in the test output.
/// </para>
/// <para>
/// This is also what the debounce question waits on. A refresh per keystroke is
/// one predicate call per row, and the entry proposing to debounce it was
/// filed before anyone knew what that cost.
/// </para>
/// </remarks>
public class ResultsGridScaleTests(ITestOutputHelper output)
{
    private const int Objects = 10_000;

    private static (AppStateViewModel State, MainWindowViewModel Vm) Build()
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);
        DifferenceDto[] diffs =
        [
            .. Enumerable.Range(0, Objects).Select(i => new DifferenceDto(
                Kind: i % 3 == 0 ? "Table" : i % 3 == 1 ? "View" : "Procedure",
                SchemaName: i % 5 == 0 ? "sales" : "dbo",
                ObjectName: $"Object{i:D5}",
                Status: i % 4 == 0 ? "Different" : "Identical"))
        ];
        DifferencePair[] pairs = [.. diffs.Select(d => new DifferencePair(
            Identity: new ObjectIdentity(d.SchemaName, d.ObjectName, d.Kind),
            Status: d.Status == "Identical" ? DifferenceStatus.Identical : DifferenceStatus.Different,
            SideA: null,
            SideB: null))];
        appState.PublishComparison(new ComparisonResult(pairs), "src", "tgt");
        appState.LastComparison = new ComparisonResultDto([.. diffs]);
        return (appState, vm);
    }

    [AvaloniaFact]
    public void Ten_thousand_objects_reach_the_grid()
    {
        var sw = Stopwatch.StartNew();
        (_, MainWindowViewModel vm) = Build();
        int rows = vm.RowsView.Cast<DifferenceRowViewModel>().Count();
        sw.Stop();

        output.WriteLine($"build + first enumeration of {Objects} rows: {sw.ElapsedMilliseconds} ms");
        rows.Should().Be(Objects);
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000);
    }

    [AvaloniaFact]
    public void Typing_in_the_search_box_filters_ten_thousand_rows_per_keystroke()
    {
        (_, MainWindowViewModel vm) = Build();
        _ = vm.RowsView.Cast<DifferenceRowViewModel>().Count();

        // Six keystrokes, each one a full refresh of the view: this is exactly
        // what the debounce would remove, measured instead of assumed.
        var sw = Stopwatch.StartNew();
        foreach (string typed in (string[])["O", "Ob", "Obj", "Obje", "Objec", "Object0999"])
        {
            vm.SearchText = typed;
        }
        int visible = vm.RowsView.Cast<DifferenceRowViewModel>().Count();
        sw.Stop();

        output.WriteLine($"six keystrokes over {Objects} rows: {sw.ElapsedMilliseconds} ms "
                       + $"({sw.ElapsedMilliseconds / 6.0:F1} ms per keystroke), {visible} rows left");
        visible.Should().Be(10, "Object09990 through Object09999 match the last query");
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000);
    }

    [AvaloniaFact]
    public void Grouping_ten_thousand_rows_stays_within_the_same_order()
    {
        (_, MainWindowViewModel vm) = Build();
        _ = vm.RowsView.Cast<DifferenceRowViewModel>().Count();

        var sw = Stopwatch.StartNew();
        vm.GroupingMode = "Tipo di differenza";
        int groups = vm.RowsView.Groups?.Count ?? 0;
        sw.Stop();

        output.WriteLine($"grouping {Objects} rows: {sw.ElapsedMilliseconds} ms into {groups} groups");
        groups.Should().Be(2, "the fixture holds Identical and Different rows only");
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000);
    }
}
