using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Reports;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// The HTML report, reachable from the app.
/// </summary>
/// <remarks>
/// The generator was finished, tested, and had one production caller —
/// <c>dbdelta report</c>. Getting the report out of the desktop app meant
/// installing the CLI and running the whole comparison a second time, against
/// both servers, to rebuild an input the app was already holding.
/// </remarks>
public class SaveReportTests
{
    private static ComparisonResult Sample() => new(
    [
        new DifferencePair(new ObjectIdentity("dbo", "Orders", "Table"),
            DifferenceStatus.Different, null, null),
        new DifferencePair(new ObjectIdentity("dbo", "Customers", "Table"),
            DifferenceStatus.Identical, null, null),
    ]);

    [AvaloniaFact]
    public void The_command_is_off_until_there_is_a_comparison()
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);

        vm.SaveReportCommand.CanExecute(null).Should().BeFalse();

        appState.PublishComparison(Sample(), "src", "tgt");

        vm.SaveReportCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// And it does NOT wait for a selection, unlike the two deploy buttons.
    /// </summary>
    /// <remarks>
    /// The report records what the two databases looked like; the selection is
    /// what the user intends to deploy. Tying the report to the ticks would
    /// make it describe an intention rather than a state.
    /// </remarks>
    [AvaloniaFact]
    public void It_needs_no_selection_while_the_deploy_buttons_do()
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);
        appState.PublishComparison(Sample(), "src", "tgt");

        vm.Rows.Should().AllSatisfy(r => r.IsSelected.Should().BeFalse());
        vm.SaveReportCommand.CanExecute(null).Should().BeTrue();
        vm.DeployCommand.CanExecute(null).Should().BeFalse(
            "the deploy path acts on the ticked rows and there are none");
    }

    /// <summary>
    /// The button exists in the realised tree and drives the command.
    /// </summary>
    /// <remarks>
    /// A view-model test cannot see a binding typo or a button that never got
    /// a column: the command would work perfectly and the user would have
    /// nothing to click, which is the exact shape of the defect this closes.
    /// </remarks>
    [AvaloniaFact]
    public void The_action_bar_carries_a_wired_button()
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);
        appState.PublishComparison(Sample(), "src", "tgt");
        MainWindow window = new() { DataContext = vm };
        window.Show();

        Button button = window.GetVisualDescendants().OfType<Button>()
            .Should().ContainSingle(b => ReferenceEquals(b.Command, vm.SaveReportCommand),
                "one button, bound to the report command")
            .Subject;

        button.IsVisible.Should().BeTrue();
        button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
            .Should().Contain("Salva report",
                "the load/save verb in this app is Salva — never Apri");
    }

    /// <summary>
    /// What gets written is the report of the whole comparison.
    /// </summary>
    [AvaloniaFact]
    public void The_report_covers_every_row_not_only_the_selected_ones()
    {
        AppStateViewModel appState = new();
        appState.PublishComparison(Sample(), "src", "tgt");

        string html = new HtmlReportGenerator().Generate(appState.LastComparisonRaw!);

        html.Should().Contain("Orders").And.Contain("Customers");
    }
}
