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
    public void Selected_rows_drive_deploy_filter_all_selected_by_default()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));

        vm.Rows.Should().AllSatisfy(r => r.IsSelected.Should().BeTrue());
    }

    [AvaloniaFact]
    public void Selected_rows_drive_deploy_filter_deselecting_one()
    {
        MainWindowViewModel vm = BuildVm(
            MakeDto("Orders", "Different"),
            MakeDto("Customers", "Different"));

        vm.Rows.First().IsSelected = false;

        IEnumerable<DifferenceRowViewModel> selected = vm.Rows.Where(r => r.IsSelected);
        selected.Count().Should().Be(1);
        selected.Single().ObjectName.Should().Be("Customers");
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
    public void LastModifiedDisplay_formats_correctly_when_present()
    {
        DateTime dt = new(2025, 11, 3, 14, 30, 0, DateTimeKind.Utc);
        DifferenceRowViewModel row = MakeRowVm(new DifferenceDto("Table", "dbo", "X", "Different", dt, null));

        row.LastModifiedSourceDisplay.Should().Be("2025-11-03 14:30");
        row.LastModifiedTargetDisplay.Should().BeEmpty();
    }
}
