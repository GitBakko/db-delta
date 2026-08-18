using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbDelta.App.ViewModels;
using DbDelta.App.Views;
using DbDelta.Persistence.Sql;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

public class ConfirmExecuteViewModelTests
{
    private static ConfirmExecuteViewModel MakeVm(
        SqlBatchResult result,
        IReadOnlyList<string>? dropped = null,
        string script = "DROP TABLE dbo.Vecchia;\nGO\n") =>
        new(
            objectCount: 3,
            differentCount: 1,
            droppedObjects: dropped ?? ["dbo.Vecchia  (Table)"],
            onlyInSourceCount: 1,
            sourceSummary: "Server=src;Database=A",
            targetSummary: "Server=tgt;Database=B",
            script: script,
            executeAsync: () => Task.FromResult(result));

    [AvaloniaFact]
    public void Starts_in_idle_confirmation_phase()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(true, null, 1, 10));

        vm.IsIdle.Should().BeTrue();
        vm.IsRunning.Should().BeFalse();
        vm.IsDone.Should().BeFalse();
        vm.Result.Should().BeNull();
        vm.ExecuteCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task Successful_run_transitions_to_done_success_with_outcome_message()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(true, null, 7, 123));

        await vm.ExecuteCommand.ExecuteAsync(null);

        vm.IsDone.Should().BeTrue();
        vm.IsDoneSuccess.Should().BeTrue();
        vm.IsDoneFailure.Should().BeFalse();
        vm.IsRunning.Should().BeFalse();
        vm.Result.Should().NotBeNull();
        vm.ResultMessage.Should().Be("Esecuzione completata — 7 batch in 123 ms.");
        vm.ExecuteCommand.CanExecute(null).Should().BeFalse(); // no re-run from the outcome phase
    }

    [AvaloniaFact]
    public async Task Failed_run_transitions_to_done_failure_with_sql_error()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(false, "Invalid object name 'dbo.X'.", 2, 50));

        await vm.ExecuteCommand.ExecuteAsync(null);

        vm.IsDoneFailure.Should().BeTrue();
        vm.IsDoneSuccess.Should().BeFalse();
        vm.ResultMessage.Should().Be("Esecuzione fallita: Invalid object name 'dbo.X'.");
    }

    [AvaloniaFact]
    public void Exposes_selection_breakdown_and_redacted_endpoints()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(true, null, 1, 1));

        vm.ObjectCount.Should().Be(3);
        vm.DifferentCount.Should().Be(1);
        vm.OnlyInTargetCount.Should().Be(1);
        vm.OnlyInSourceCount.Should().Be(1);
        vm.SourceSummary.Should().Be("Server=src;Database=A");
        vm.TargetSummary.Should().Be("Server=tgt;Database=B");
    }

    // ── The last gate before an irreversible change ───────────────────────────

    /// <summary>
    /// The dialog carries the exact SQL it will run. It used to take only counts,
    /// while the caller had the script as a local one statement above.
    /// </summary>
    [AvaloniaFact]
    public void Carries_the_script_it_will_run_and_counts_its_lines()
    {
        ConfirmExecuteViewModel vm = MakeVm(
            new SqlBatchResult(true, null, 1, 1),
            script: "DROP TABLE dbo.A;\nGO\nDROP TABLE dbo.B;\nGO\n");

        vm.Script.Should().Contain("DROP TABLE dbo.A;");
        vm.ScriptLineCount.Should().Be(4, "a trailing newline does not open a fifth line");
        vm.ScriptToggleLabel.Should().Be("Mostra lo script (4 righe)");
    }

    /// <summary>A script with no trailing newline still counts its last line.</summary>
    [AvaloniaFact]
    public void Counts_the_last_line_of_a_script_that_does_not_end_in_a_newline()
    {
        ConfirmExecuteViewModel vm = MakeVm(new SqlBatchResult(true, null, 1, 1), script: "SELECT 1;\nGO");

        vm.ScriptLineCount.Should().Be(2);
    }

    /// <summary>
    /// The drop count comes from the names themselves, so the tally and the list
    /// cannot disagree — and the wording says what happens, not where the objects
    /// live.
    /// </summary>
    [AvaloniaFact]
    public void Names_what_gets_deleted_and_says_so_in_the_warning()
    {
        ConfirmExecuteViewModel vm = MakeVm(
            new SqlBatchResult(true, null, 1, 1),
            dropped: ["dbo.Vecchia  (Table)", "dbo.vObsoleta  (View)"]);

        vm.OnlyInTargetCount.Should().Be(2);
        vm.HasDrops.Should().BeTrue();
        vm.DroppedObjectsText.Should().Contain("dbo.Vecchia").And.Contain("dbo.vObsoleta");
        vm.DropWarning.Should().Contain("ELIMINATI").And.Contain("non è annullabile");
    }

    /// <summary>Singular wording, and no danger panel when nothing is dropped.</summary>
    [AvaloniaFact]
    public void Uses_singular_wording_for_one_drop_and_hides_the_panel_for_none()
    {
        ConfirmExecuteViewModel one = MakeVm(new SqlBatchResult(true, null, 1, 1), dropped: ["dbo.Sola  (Table)"]);
        one.DropWarning.Should().StartWith("1 oggetto verrà ELIMINATO");

        ConfirmExecuteViewModel none = MakeVm(new SqlBatchResult(true, null, 1, 1), dropped: []);
        none.HasDrops.Should().BeFalse();
        none.OnlyInTargetCount.Should().Be(0);
        none.DroppedObjectsText.Should().BeEmpty();
    }

    /// <summary>
    /// The one thing headless CAN check about layout, and the risk the script
    /// pane introduces: the window is SizeToContent="Height", so an unbounded
    /// pane would grow it past the screen. Both the script and the drop list sit
    /// in capped ScrollViewers.
    /// </summary>
    /// <remarks>
    /// The bound moved from 260 to 440 on 2026-08-18, deliberately: the pane's
    /// cap went 220 → 420 and the window became resizable, because seen live at
    /// 620 wide and fixed, a line of DDL was clipped on both sides with no way
    /// for the reader to widen it. What the net asserts is unchanged — a huge
    /// script scrolls INSIDE a bounded pane instead of growing the window
    /// without limit. If a future change needs this number raised again, check
    /// that it is still a cap and not the removal of one.
    /// </remarks>
    [AvaloniaFact]
    public void A_huge_script_does_not_grow_the_window_past_the_screen()
    {
        string huge = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"-- riga {i}"));
        ConfirmExecuteDialog dlg = new()
        {
            DataContext = new ConfirmExecuteViewModel(
                1, 1, [.. Enumerable.Range(0, 200).Select(i => $"dbo.T{i}  (Table)")], 0,
                "src", "tgt", huge,
                () => Task.FromResult(new SqlBatchResult(true, null, 1, 1))),
        };
        dlg.Show();

        Expander expander = dlg.GetVisualDescendants().OfType<Expander>().Single();
        expander.IsExpanded = true;
        dlg.UpdateLayout();

        // The window's own Bounds do NOT answer this in headless — measured with
        // the cap removed, they stay small either way, so asserting on them proves
        // nothing. The pane's own height does.
        ScrollViewer scriptPane = dlg.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(s => s.Name == "ScriptPane");

        scriptPane.Bounds.Height.Should().BeLessThan(440,
            "5000 script lines must scroll inside the pane, not grow the window past the screen");
    }

    /// <summary>
    /// The script section opens at its FIRST line, not its last.
    /// </summary>
    /// <remarks>
    /// Measured on the running app before it was fixed: UI Automation reported
    /// the pane at HorizontalScrollPercent = 100 and VerticalScrollPercent = 100
    /// the instant it expanded, so the reader met the tail of the script with
    /// the first characters of every line clipped. The section exists to let
    /// someone check what is about to run against a live database — landing at
    /// the end of it defeats its only purpose. Headless cannot see the clipping,
    /// but it can see the offset.
    /// </remarks>
    [AvaloniaFact]
    public void The_script_section_opens_at_the_top_left()
    {
        string huge = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"-- riga molto lunga numero {i} con testo a sufficienza per scorrere"));
        ConfirmExecuteDialog dlg = new()
        {
            DataContext = new ConfirmExecuteViewModel(
                1, 1, ["dbo.T  (Table)"], 0, "src", "tgt", huge,
                () => Task.FromResult(new SqlBatchResult(true, null, 1, 1))),
        };
        dlg.Show();

        // The pane does not exist until the section is opened once — the
        // Expander realises its content lazily.
        Expander expander = dlg.GetVisualDescendants().OfType<Expander>().Single();
        expander.IsExpanded = true;
        dlg.UpdateLayout();
        ScrollViewer scriptPane = dlg.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(s => s.Name == "ScriptPane");

        // Drive it to the corner the live app opened at, then close and reopen.
        // Asserting on the first expansion alone would pass without the handler,
        // because headless happens to start at zero — it is the RETURN from a
        // scrolled state that proves something resets it.
        scriptPane.ScrollToEnd();
        dlg.UpdateLayout();
        scriptPane.Offset.Y.Should().BeGreaterThan(0, "the test must start from a scrolled pane to mean anything");

        expander.IsExpanded = false;
        dlg.UpdateLayout();
        expander.IsExpanded = true;
        dlg.UpdateLayout();
        // The reset is posted at Loaded priority, so the queue has to run.
        Dispatcher.UIThread.RunJobs();

        scriptPane.Offset.Y.Should().Be(0, "the reader must land on the first statement, not the last");
        scriptPane.Offset.X.Should().Be(0, "a horizontal offset clips the start of every line");
    }
}
