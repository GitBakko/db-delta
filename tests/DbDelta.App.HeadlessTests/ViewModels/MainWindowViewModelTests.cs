using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Persistence.Sql;
using DbDelta.Shared.Dtos;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

public class MainWindowViewModelTests
{
    private static MainWindowViewModel BuildVm(params DifferenceDto[] diffs)
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);

        if (diffs.Length > 0)
        {
            DifferencePair[] pairs = [.. diffs.Select(d => new DifferencePair(
                Identity: new ObjectIdentity(d.SchemaName, d.ObjectName, d.Kind),
                Status: DifferenceStatus.Different,
                SideA: null,
                SideB: null))];
            // Through the public entry point, not by assigning the raw result:
            // that is the one path that stamps the rows with the endpoints they
            // were computed from, and a test that skips it is a test proving the
            // invariant can be skipped. The DTO is then swapped for the one
            // these tests describe, which carries no provenance either way.
            appState.PublishComparison(new ComparisonResult(pairs), "src", "tgt");
            appState.LastComparison = new ComparisonResultDto([.. diffs]);
        }

        return vm;
    }

    private static DifferenceDto MakeDto(string name, string status, string kind = "Table") =>
        new(Kind: kind, SchemaName: "dbo", ObjectName: name, Status: status);

    // ── Search filter ────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Rows_filter_by_search_text()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));

        vm.SearchText = "ord";

        int visible = vm.RowsView.Cast<DifferenceRowViewModel>().Count();
        visible.Should().Be(1);
        vm.RowsView.Cast<DifferenceRowViewModel>().Single().ObjectName.Should().Be("Orders");
    }

    /// <summary>
    /// Typing what the grid shows finds it.
    /// </summary>
    /// <remarks>
    /// The status column renders StatusDisplayItalian and the grid groups by
    /// it, while the predicate compared only the raw enum — so searching
    /// "Diversi" returned nothing on a screen full of rows labelled Diverso.
    /// The Italian word is the one a user has in front of them; the English one
    /// stays searchable because scripts and logs use it.
    /// </remarks>
    [AvaloniaFact]
    public void Search_matches_the_status_word_the_grid_actually_shows()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Identical"));

        vm.SearchText = "Diversi";

        vm.RowsView.Cast<DifferenceRowViewModel>()
            .Should().ContainSingle().Which.ObjectName.Should().Be("Orders");
    }

    /// <summary>The raw enum keeps working — it is what a log or a script says.</summary>
    [AvaloniaFact]
    public void Search_still_matches_the_raw_status()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Identical"));

        vm.SearchText = "Different";

        vm.RowsView.Cast<DifferenceRowViewModel>()
            .Should().ContainSingle().Which.ObjectName.Should().Be("Orders");
    }

    [AvaloniaFact]
    public void Rows_filter_clears_when_search_text_emptied()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));

        vm.SearchText = "ord";
        vm.SearchText = "";

        vm.RowsView.Cast<DifferenceRowViewModel>().Count().Should().Be(2);
    }

    // ── Grouping ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Rows_group_by_status()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Invoices", "OnlyInA"));

        vm.GroupingMode = "Tipo di differenza";

        // DataGridCollectionView exposes groups via ICollectionViewWithItemCount or cast
        int groupCount = vm.RowsView.Groups?.Count ?? 0;
        groupCount.Should().Be(2);
    }

    [AvaloniaFact]
    public void Rows_group_by_kind()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different", "Table"),
            MakeDto("vw_Orders", "Different", "View"));

        vm.GroupingMode = "Tipo di oggetto";

        int groupCount = vm.RowsView.Groups?.Count ?? 0;
        groupCount.Should().Be(2);
    }

    [AvaloniaFact]
    public void No_grouping_produces_flat_list()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Invoices", "OnlyInA"));

        vm.GroupingMode = "Nessun gruppo";

        int groupCount = vm.RowsView.Groups?.Count ?? 0;
        groupCount.Should().Be(0);
    }

    // ── IsSelected → deploy filter ───────────────────────────────────────────

    [AvaloniaFact]
    public void Rows_default_to_unselected()
    {
        // Updated UX rule (feedback round 2): user explicitly opts in to what
        // gets aligned; no row starts selected.
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));

        vm.Rows.Should().AllSatisfy(r => r.IsSelected.Should().BeFalse());
    }

    [AvaloniaFact]
    public void Selecting_rows_updates_filter()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));

        vm.Rows.First().IsSelected = true;

        IEnumerable<DifferenceRowViewModel> selected = vm.Rows.Where(r => r.IsSelected);
        selected.Count().Should().Be(1);
        selected.Single().ObjectName.Should().Be("Orders");
    }

    [AvaloniaFact]
    public void Identical_rows_are_not_selectable()
    {
        DifferenceRowViewModel row = MakeRowVm(MakeDto("Tax", "Identical"));
        row.IsSelectable.Should().BeFalse();
        row.IsIdentical.Should().BeTrue();
    }

    // ── DifferenceRowViewModel helpers ───────────────────────────────────────

    private static DifferenceRowViewModel MakeRowVm(DifferenceDto dto) =>
        new(new DifferencePair(
                Identity: new ObjectIdentity(dto.SchemaName, dto.ObjectName, dto.Kind),
                Status: DifferenceStatus.Different,
                SideA: null,
                SideB: null),
            dto, "#000");

    [AvaloniaFact]
    public void SelectionBrushHex_correct_for_each_status()
    {
        static string Brush(string status)
        {
            return MakeRowVm(MakeDto("X", status)).SelectionBrushHex;
        }

        Brush("Different").Should().Be("#0064C8");
        Brush("OnlyInB").Should().Be("#B31220");
        Brush("OnlyInA").Should().Be("#007339");
        Brush("Identical").Should().Be("#9097A0");
    }

    [AvaloniaFact]
    public void Kind_label_and_sort_order_come_from_the_shared_catalog()
    {
        // The row view-model used to carry its own copy of the kind table, and
        // it had fallen behind twice: Schema and TableType were added to
        // KindCatalog and never here, so those rows rendered the raw English
        // kind and sorted to the bottom while the reports showed "Schemi" /
        // "Tipi tabella" in their proper place.
        DifferenceRowViewModel schema = MakeRowVm(MakeDto("vendite", "Different", "Schema"));
        DifferenceRowViewModel tableType = MakeRowVm(MakeDto("TipoRiga", "Different", "TableType"));
        DifferenceRowViewModel table = MakeRowVm(MakeDto("Orders", "Different", "Table"));

        schema.KindDisplayName.Should().Be("Schemi");
        tableType.KindDisplayName.Should().Be("Tipi tabella");
        schema.KindOrder.Should().BeLessThan(table.KindOrder, "schemas come first, as in every report");
        tableType.KindOrder.Should().BeLessThan(int.MaxValue, "it is a known kind, not an 'everything else'");
    }

    [AvaloniaFact]
    public void QualifiedName_includes_schema_prefix()
    {
        DifferenceRowViewModel row = MakeRowVm(new DifferenceDto("Table", "dbo", "Orders", "Different"));

        row.QualifiedName.Should().Be("dbo.Orders");
    }

    [AvaloniaFact]
    public void LastModifiedDisplay_formats_in_italian_locale_and_keeps_server_local_time()
    {
        // sys.objects.modify_date is the DB SERVER's local clock (Kind =
        // Unspecified) and must be rendered VERBATIM in IT format — no
        // ToLocalTime() shift into the client timezone (user bug report:
        // diff-table dates showed UTC instead of the DB server's time).
        DateTime serverLocal = new(2025, 11, 3, 14, 30, 0, DateTimeKind.Unspecified);

        DifferenceRowViewModel row = MakeRowVm(new DifferenceDto("Table", "dbo", "X", "Different", serverLocal, null));

        row.LastModifiedSourceDisplay.Should().Be("03/11/2025 14:30");
        row.LastModifiedTargetDisplay.Should().BeEmpty();
    }

    // ── Version pill ─────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void AppVersion_exposes_the_assembly_version_display()
    {
        MainWindowViewModel vm = BuildVm();
        vm.AppVersion.Should().Be(AppVersionInfo.Display);
        vm.AppVersion.Should().NotBeNullOrWhiteSpace();
    }

    [AvaloniaFact]
    public void OpenVersionHistory_command_exists_and_can_execute()
    {
        MainWindowViewModel vm = BuildVm();
        vm.OpenVersionHistoryCommand.Should().NotBeNull();
        vm.OpenVersionHistoryCommand.CanExecute(null).Should().BeTrue();
        // Deliberately NOT executed — it would open a real browser.
    }

    // ── After a direct execution ─────────────────────────────────────────────

    /// <summary>
    /// Reported from the app on the first successful live deploy: the script
    /// ran, and the grid went on showing the objects it had just aligned as
    /// different, with the ticks still on them. Evidence that the comparison was
    /// re-run is that it FAILED here — the endpoints these tests carry are not
    /// connection strings, so a compare that starts sets LastError, and a
    /// compare that never starts cannot.
    /// </summary>
    [AvaloniaFact]
    public async Task A_successful_execution_re_runs_the_comparison()
    {
        MainWindowViewModel vm = BuildVm(MakeDto("Orders", "Different"));
        vm.AppState.SourceConnectionString = "not a connection string";
        vm.AppState.TargetConnectionString = "nor is this";
        vm.AppState.LastError = null;

        await vm.AfterExecuteAsync(new SqlBatchResult(true, null, 3, 120), "Esecuzione completata.");

        vm.AppState.LastError.Should().NotBeNull("the comparison was re-run");
        vm.StatusText.Should().Contain("Esecuzione completata.");
        vm.LastRun.Should().NotBeNull("the status-bar pill outlives the dialog");
    }

    /// <summary>
    /// The failure is the case the pill exists for: the outcome panel dies with
    /// the dialog, and with it every error the server raised.
    /// </summary>
    [AvaloniaFact]
    public async Task A_failed_execution_is_still_kept_for_the_status_pill()
    {
        MainWindowViewModel vm = BuildVm(MakeDto("Orders", "Different"));

        await vm.AfterExecuteAsync(
            new SqlBatchResult(false, "Msg 207", 1, 40, Messages: [new(207, 16, 1, 3, null, "boom")]),
            "Esecuzione fallita: Msg 207");

        vm.LastRun!.Succeeded.Should().BeFalse();
        vm.LastRun.ErrorCount.Should().Be(1);
    }

    /// <summary>
    /// A failed run rolled itself back, so the rows still describe the target
    /// exactly — and throwing them away would cost the operator the selection
    /// they are about to retry.
    /// </summary>
    [AvaloniaFact]
    public async Task A_failed_execution_changes_nothing()
    {
        MainWindowViewModel vm = BuildVm(MakeDto("Orders", "Different"));
        vm.AppState.SourceConnectionString = "not a connection string";
        vm.AppState.TargetConnectionString = "nor is this";
        vm.AppState.LastError = null;
        vm.Rows[0].IsSelected = true;

        await vm.AfterExecuteAsync(
            new SqlBatchResult(false, "Msg 207", 1, 40, RolledBack: true), "Esecuzione fallita: Msg 207");

        vm.AppState.LastError.Should().BeNull("no comparison may be started");
        vm.Rows[0].IsSelected.Should().BeTrue("the selection is what the operator retries");
        vm.StatusText.Should().Be("Esecuzione fallita: Msg 207");
    }

    /// <summary>Cancelling the confirm dialog is not an execution.</summary>
    [AvaloniaFact]
    public async Task A_cancelled_execution_leaves_the_status_bar_alone()
    {
        MainWindowViewModel vm = BuildVm(MakeDto("Orders", "Different"));
        vm.StatusText = "Ready";

        await vm.AfterExecuteAsync(null, "ignored");

        vm.StatusText.Should().Be("Ready");
        vm.AppState.LastError.Should().BeNull();
    }

    // ── Bulk selection ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Toggle_all_selects_every_row_then_clears_them()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));

        vm.AllVisibleSelected.Should().Be(false, "nothing is ticked yet");

        vm.ToggleAllVisibleCommand.Execute(null);
        vm.SelectedCount.Should().Be(2);
        vm.AllVisibleSelected.Should().Be(true);

        vm.ToggleAllVisibleCommand.Execute(null);
        vm.SelectedCount.Should().Be(0);
        vm.AllVisibleSelected.Should().Be(false);
    }

    /// <summary>
    /// A partial selection must COMPLETE on the next click, not throw away what
    /// the user already ticked — the destructive reading of "toggle".
    /// </summary>
    [AvaloniaFact]
    public void Toggle_all_completes_a_partial_selection_instead_of_clearing_it()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));
        vm.Rows[0].IsSelected = true;

        vm.AllVisibleSelected.Should().BeNull("some but not all are ticked");

        vm.ToggleAllVisibleCommand.Execute(null);

        vm.SelectedCount.Should().Be(2);
    }

    /// <summary>
    /// The whole point of scoping the bulk action to the visible rows: a row
    /// filtered out of view must never be ticked behind the user's back, or it
    /// becomes a statement in the deploy script nobody reviewed.
    /// </summary>
    [AvaloniaFact]
    public void Toggle_all_ignores_rows_the_search_filter_hides()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));
        vm.SearchText = "ord";

        vm.ToggleAllVisibleCommand.Execute(null);

        vm.SelectedCount.Should().Be(1, "only the row matching the search may be ticked");
        vm.Rows.Single(r => r.ObjectName == "Customers").IsSelected.Should().BeFalse();
    }

    /// <summary>
    /// Identical rows carry no DDL and are not selectable, so a bulk action must
    /// leave them alone rather than inflate the count with rows the grid will
    /// not even show a checkbox for.
    /// </summary>
    [AvaloniaFact]
    public void Toggle_all_leaves_identical_rows_alone()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Unchanged", "Identical"));

        vm.ToggleAllVisibleCommand.Execute(null);

        vm.SelectedCount.Should().Be(1);
        vm.Rows.Single(r => r.ObjectName == "Unchanged").IsSelected.Should().BeFalse();
    }
}
