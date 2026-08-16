using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// The generator refuses to script a table whose rebuild would drop an index it
/// cannot write back. The app has to turn that into the error banner — an
/// exception out of a <c>[RelayCommand]</c> is rethrown on the UI thread and
/// takes the window with it, which is the one outcome worse than not deploying.
/// </summary>
public class UnscriptableIndexBannerTests
{
    private const string Dev = "Server=dev;Database=App;Integrated Security=true";
    private const string Staging = "Server=staging;Database=App;Integrated Security=true";

    private static readonly IReadOnlyDictionary<(string Schema, string Table, string Column), string>
        NoBackfill = new Dictionary<(string, string, string), string>();

    /// <summary>
    /// Plain int Id on the target, IDENTITY on the source: the temp-table
    /// rebuild, the statement that does the destroying.
    /// </summary>
    private static (AppStateViewModel State, MainWindowViewModel Vm, ComparisonResult Result)
        AfterComparing(TableIndex index)
    {
        Column plainId = new("Id", "int", isNullable: false, ordinal: 1);
        Column identityId = new("Id", "int", isNullable: false, ordinal: 1,
            isIdentity: true, identitySeed: 1, identityIncrement: 1);
        Column importo = new("Importo", "decimal(18,2)", isNullable: false, ordinal: 2);

        Table target = new("dbo", "Fatti", [plainId, importo], [], [index]);
        Table source = new("dbo", "Fatti", [identityId, importo], [], [index]);
        ComparisonResult result = new(
            [new DifferencePair(source.Identity, DifferenceStatus.Different, source, target)]);

        AppStateViewModel state = new()
        {
            SourceConnectionString = Dev,
            TargetConnectionString = Staging,
        };
        state.PublishComparison(result, Dev, Staging);
        return (state, new MainWindowViewModel(state), result);
    }

    private static TableIndex Index(string? typeDesc) => new(
        Name: "NCCI_Fatti",
        IsUnique: false,
        IsClustered: false,
        FilterExpression: null,
        KeyColumns: [new IndexColumn("Importo", false)],
        IncludedColumns: [],
        TypeDesc: typeDesc);

    [AvaloniaFact]
    public void A_refused_script_lands_in_the_error_banner_instead_of_killing_the_window()
    {
        (AppStateViewModel state, MainWindowViewModel vm, ComparisonResult result) =
            AfterComparing(Index("NONCLUSTERED COLUMNSTORE"));

        string? script = vm.TryBuildDeployScript(result.Differences, NoBackfill);

        script.Should().BeNull();
        state.LastError.Should().NotBeNull();
        state.LastError.Should().Contain("NCCI_Fatti").And.Contain("dbo.Fatti");
        vm.StatusText.Should().NotBeNullOrWhiteSpace("the status bar must not read as if a script exists");
    }

    /// <summary>
    /// The same table with an ordinary index still produces a script and leaves
    /// the banner alone — without this the banner could be unconditional and
    /// the test above would still be green.
    /// </summary>
    [AvaloniaFact]
    public void A_rowstore_index_still_gets_its_script_and_no_banner()
    {
        (AppStateViewModel state, MainWindowViewModel vm, ComparisonResult result) =
            AfterComparing(Index("NONCLUSTERED"));

        string? script = vm.TryBuildDeployScript(result.Differences, NoBackfill);

        script.Should().NotBeNullOrWhiteSpace();
        script.Should().Contain("DROP TABLE [dbo].[Fatti];");
        state.LastError.Should().BeNull();
    }
}
