using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views.Controls;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// C1 — results must stop being actionable the moment they stop describing the
/// configured endpoints. The scenario: compare DEV against STAGING, tick rows
/// of which some are DROPs, repoint the target at PROD, and hit execute. The
/// rows were computed against a server that has nothing to do with PROD.
/// </summary>
public class StaleResultsTests
{
    private const string Dev = "Server=dev;Database=App;Integrated Security=true";
    private const string Staging = "Server=staging;Database=App;Integrated Security=true";
    private const string Prod = "Server=prod;Database=App;Integrated Security=true";

    /// <summary>
    /// Puts the view-model in the state a completed comparison leaves behind:
    /// endpoints set, rows built, one row ticked.
    /// </summary>
    private static (AppStateViewModel State, MainWindowViewModel Vm) AfterAComparison(string source = Dev)
    {
        AppStateViewModel appState = new()
        {
            SourceConnectionString = source,
            TargetConnectionString = Staging,
        };
        MainWindowViewModel vm = new(appState);

        DifferencePair[] pairs =
        [
            new(
                Identity: new ObjectIdentity("dbo", "Clienti", "Table"),
                Status: DifferenceStatus.OnlyInB,
                SideA: null,
                SideB: null),
        ];
        appState.PublishComparison(new ComparisonResult(pairs), source, Staging);

        vm.Rows.Single().IsSelected = true;
        return (appState, vm);
    }

    [AvaloniaFact]
    public void A_completed_comparison_is_actionable()
    {
        (AppStateViewModel state, MainWindowViewModel vm) = AfterAComparison();

        state.ResultsAreStale.Should().BeFalse();
        vm.DeployCommand.CanExecute(null).Should().BeTrue();
        vm.ExecuteOnTargetCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact]
    public void Repointing_the_target_disables_both_commands()
    {
        (AppStateViewModel state, MainWindowViewModel vm) = AfterAComparison();

        state.TargetConnectionString = Prod;

        state.ResultsAreStale.Should().BeTrue();
        vm.ExecuteOnTargetCommand.CanExecute(null).Should()
            .BeFalse("the ticked DROPs were computed against staging, not prod");
        vm.DeployCommand.CanExecute(null).Should()
            .BeFalse("a script saved now would carry the same wrong pairs to whoever runs it later");
    }

    [AvaloniaFact]
    public void Repointing_the_source_disables_both_commands_too()
    {
        (AppStateViewModel state, MainWindowViewModel vm) = AfterAComparison();

        state.SourceConnectionString = Prod;

        state.ResultsAreStale.Should().BeTrue();
        vm.ExecuteOnTargetCommand.CanExecute(null).Should().BeFalse();
        vm.DeployCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// A comparison that fails leaves the previous rows on screen. They describe
    /// the same endpoints, but not the state the user just asked to measure, so
    /// they stop being actionable until a comparison completes.
    /// </summary>
    [AvaloniaFact]
    public async Task A_failed_comparison_disables_both_commands()
    {
        // The endpoints must already equal the published pair, or changing them
        // is what makes the results stale and the clear-on-start this test
        // exists for goes unexercised — which is exactly what the first version
        // of this test did.
        const string unparseable = "not==a==connection==string";
        (AppStateViewModel state, MainWindowViewModel vm) = AfterAComparison(source: unparseable);
        state.ResultsAreStale.Should().BeFalse("precondition: the pair on screen matches the endpoints");

        // CompareAsync bails on the unparseable source before any I/O.
        await state.CompareAsync(CancellationToken.None);

        state.LastError.Should().NotBeNullOrEmpty();
        state.LastComparisonRaw.Should().NotBeNull("the grid still shows the previous run");
        state.ResultsAreStale.Should().BeTrue();
        vm.ExecuteOnTargetCommand.CanExecute(null).Should().BeFalse();
        vm.DeployCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact]
    public void Restoring_the_original_endpoints_makes_the_results_actionable_again()
    {
        (AppStateViewModel state, MainWindowViewModel vm) = AfterAComparison();

        state.TargetConnectionString = Prod;
        state.TargetConnectionString = Staging;

        state.ResultsAreStale.Should().BeFalse();
        vm.ExecuteOnTargetCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact]
    public void Nothing_is_stale_before_the_first_comparison()
    {
        AppStateViewModel state = new() { SourceConnectionString = Dev, TargetConnectionString = Staging };

        state.ResultsAreStale.Should().BeFalse("there is nothing on screen to be stale");
    }

    // ── The scope of the verdict ──────────────────────────────────────────────

    /// <summary>
    /// "Nessuna differenza" means none among the thirteen modelled kinds, and
    /// the grid has to say so when the endpoints hold anything else.
    /// </summary>
    [AvaloniaFact]
    public void The_scope_caveat_appears_only_when_something_was_not_examined()
    {
        AppStateViewModel plain = new() { SourceConnectionString = Dev, TargetConnectionString = Staging };
        plain.PublishComparison(new ComparisonResult([]), Dev, Staging);
        plain.UnexaminedSummary.Should().BeNull("nothing was skipped");

        AppStateViewModel skipped = new() { SourceConnectionString = Dev, TargetConnectionString = Staging };
        skipped.PublishComparison(
            new ComparisonResult([]) { Unexamined = new([new("INDEX_NON_ROWSTORE", 3)]) },
            Dev,
            Staging);

        skipped.UnexaminedSummary.Should().NotBeNull();
        skipped.UnexaminedSummary.Should().Contain("3 opzioni di indici non rowstore");
    }

    /// <summary>
    /// Rendered, not just computed: the band is bound AND placed in its own grid
    /// row. A property nothing shows is a property that ships broken — the lesson
    /// the error banner already taught, when LastError was bound only inside a
    /// view that was never instantiated and every failed compare set a message
    /// nobody could see.
    /// </summary>
    [AvaloniaFact]
    public void The_scope_caveat_is_actually_rendered_by_the_shell()
    {
        AppStateViewModel state = new() { SourceConnectionString = Dev, TargetConnectionString = Staging };
        Views.MainWindow window = new() { DataContext = new MainWindowViewModel(state) };
        window.Show();

        NoticeBand band = window.GetVisualDescendants()
            .OfType<NoticeBand>()
            .Single(b => b.Name == "ScopeBand");
        band.IsVisible.Should().BeFalse("no comparison has run yet");

        state.PublishComparison(
            new ComparisonResult([]) { Unexamined = new([new("ASSEMBLY", 4)]) },
            Dev,
            Staging);
        window.UpdateLayout();

        band.Message.Should().Contain("4 assembly CLR");
        band.IsVisible.Should().BeTrue();
    }
}
