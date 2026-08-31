using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// The generator refuses to script a memory-optimized table type. The app has
/// to turn that into the error banner, for the same reason the index refusal
/// does: <c>TryBuildDeployScript</c> has no general catch, so an exception with
/// no clause of its own escapes a <c>[RelayCommand]</c>, is rethrown on the UI
/// thread, and the last-resort handler then shows a message about the project
/// not being saved — wrong text for a refusal, and the window is already gone.
/// </summary>
public class UnscriptableTableTypeBannerTests
{
    private const string Dev = "Server=dev;Database=App;Integrated Security=true";
    private const string Staging = "Server=staging;Database=App;Integrated Security=true";

    private static readonly IReadOnlyDictionary<(string Schema, string Table, string Column), string>
        NoBackfill = new Dictionary<(string, string, string), string>();

    private static (AppStateViewModel State, MainWindowViewModel Vm, ComparisonResult Result)
        AfterComparing(bool memoryOptimized)
    {
        TableTypeUdt source = new("dbo", "OrderTvp",
            [new Column("Id", "int", isNullable: false, ordinal: 1)])
        {
            IsMemoryOptimized = memoryOptimized,
        };
        ComparisonResult result = new(
            [new DifferencePair(source.Identity, DifferenceStatus.OnlyInA, source, null)]);

        AppStateViewModel state = new()
        {
            SourceConnectionString = Dev,
            TargetConnectionString = Staging,
        };
        state.PublishComparison(result, Dev, Staging);
        return (state, new MainWindowViewModel(state), result);
    }

    [AvaloniaFact]
    public void A_refused_memory_optimized_table_type_lands_in_the_error_banner()
    {
        (AppStateViewModel state, MainWindowViewModel vm, ComparisonResult result) =
            AfterComparing(memoryOptimized: true);

        string? script = vm.TryBuildDeployScript(result.Differences, NoBackfill);

        script.Should().BeNull();
        state.LastError.Should().NotBeNull();
        state.LastError.Should().Contain("OrderTvp").And.Contain("memory-optimized");
        vm.StatusText.Should().NotBeNullOrWhiteSpace("the status bar must not read as if a script exists");
    }

    /// <summary>
    /// The same type on disk still produces a script and leaves the banner
    /// alone — without this the banner could be unconditional and the test
    /// above would still be green.
    /// </summary>
    [AvaloniaFact]
    public void A_disk_based_table_type_still_gets_its_script_and_no_banner()
    {
        (AppStateViewModel state, MainWindowViewModel vm, ComparisonResult result) =
            AfterComparing(memoryOptimized: false);

        string? script = vm.TryBuildDeployScript(result.Differences, NoBackfill);

        script.Should().NotBeNullOrWhiteSpace();
        script.Should().Contain("CREATE TYPE [dbo].[OrderTvp] AS TABLE");
        state.LastError.Should().BeNull();
    }
}
