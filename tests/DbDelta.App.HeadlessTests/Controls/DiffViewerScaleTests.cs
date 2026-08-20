using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.Diff;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.Controls;

/// <summary>
/// The three diff panes at the size that used to kill the process. The
/// allocation half is fixed — LineDiffer trims the common head and tail before
/// building its table — and what is left is the question the entry proposing
/// virtualisation never answered: how long three non-virtualising ItemsControls
/// take to realise that many rows.
/// </summary>
/// <remarks>
/// Measured rather than assumed, the same way the results grid was. The bound
/// is loose on purpose: what it catches is a change of ORDER, not a busy runner.
/// </remarks>
public class DiffViewerScaleTests(ITestOutputHelper output)
{
    private const int Lines = 30_000;

    private static string Body(string marker) =>
        string.Join('\n', Enumerable.Range(0, Lines).Select(i =>
            i == Lines / 2 ? $"    SELECT {marker} AS Changed" : $"    SELECT {i} AS Col{i}"));

    [AvaloniaFact]
    public void Thirty_thousand_lines_reach_the_three_panes()
    {
        DiffViewerViewModel vm = new();
        var sw = Stopwatch.StartNew();
        vm.Rows = LineDiffer.Compute(Body("a"), Body("b"));
        long diffed = sw.ElapsedMilliseconds;

        DiffViewerView view = new() { DataContext = vm };
        Window host = new() { Content = view, Width = 1200, Height = 800 };
        host.Show();
        view.Measure(new Avalonia.Size(1200, 800));
        view.Arrange(new Avalonia.Rect(0, 0, 1200, 800));
        sw.Stop();

        int panes = view.GetVisualDescendants().OfType<ItemsControl>().Count();
        output.WriteLine($"diff of {Lines} lines: {diffed} ms; "
                       + $"+ layout of {panes} panes: {sw.ElapsedMilliseconds - diffed} ms "
                       + $"({sw.ElapsedMilliseconds} ms total)");

        vm.Rows.Should().HaveCountGreaterThanOrEqualTo(Lines);
        vm.SourceMarkRows.Should().ContainSingle("one line differs, so the strip has one mark to draw");
        vm.TargetMarkRows.Should().ContainSingle();

        // 70 s before the two minimap strips stopped drawing one rectangle per
        // LINE instead of one per CHANGE. The bound is loose on purpose: what
        // it has to catch is a return to that order of magnitude.
        // Measured at ~0,5 s. Three seconds leaves six times the headroom for a
        // busy runner and still fails if ONE of the two strips goes back to a
        // rectangle per line, which costs about 6 s on its own.
        sw.ElapsedMilliseconds.Should().BeLessThan(3_000);
    }
}
