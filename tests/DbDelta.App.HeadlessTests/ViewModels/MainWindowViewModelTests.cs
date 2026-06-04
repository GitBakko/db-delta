using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
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
            appState.LastComparisonRaw = new ComparisonResult(pairs);
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
    public void QualifiedName_includes_schema_prefix()
    {
        DifferenceRowViewModel row = MakeRowVm(new DifferenceDto("Table", "dbo", "Orders", "Different"));

        row.QualifiedName.Should().Be("dbo.Orders");
    }

    [AvaloniaFact]
    public void LastModifiedDisplay_formats_in_italian_locale_and_local_time()
    {
        // Updated UX rule (feedback round 4): user wants IT date format
        // (dd/MM/yyyy HH:mm). DTO timestamp is in UTC and we convert to local
        // time for display — pick a stable instant + compute the expected
        // local-time string up-front so the test is host-timezone agnostic.
        DateTime utc = new(2025, 11, 3, 14, 30, 0, DateTimeKind.Utc);
        string expected = utc
            .ToLocalTime()
            .ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));

        DifferenceRowViewModel row = MakeRowVm(new DifferenceDto("Table", "dbo", "X", "Different", utc, null));

        row.LastModifiedSourceDisplay.Should().Be(expected);
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
}
