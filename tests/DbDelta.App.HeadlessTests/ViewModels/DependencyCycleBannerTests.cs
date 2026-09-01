using Avalonia.Headless.XUnit;
using DbDelta.App.ViewModels;
using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using FluentAssertions;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// A CHECK that calls a function reading its own table is a legal schema and a
/// dependency cycle. The app has to turn that into the error banner, for the
/// same reason the four refusals do: <c>TryBuildDeployScript</c> has no general
/// catch, so an exception with no clause of its own escapes a
/// <c>[RelayCommand]</c>, is rethrown on the UI thread, and the last-resort
/// handler shows a message about the project not being saved — wrong text, and
/// the window is already gone.
/// </summary>
/// <remarks>
/// The cycle is a property of the EDGES, not of the objects, which is why this
/// is the one case that cannot be built from the object model alone.
/// </remarks>
public class DependencyCycleBannerTests
{
    private const string Dev = "Server=dev;Database=App;Integrated Security=true";
    private const string Staging = "Server=staging;Database=App;Integrated Security=true";

    private static readonly IReadOnlyDictionary<(string Schema, string Table, string Column), string>
        NoBackfill = new Dictionary<(string, string, string), string>();

    private static ObjectIdentity Tbl => new("dbo", "Righe", "Table");
    private static ObjectIdentity Fn => new("dbo", "fnRowCount", "Function");

    private static (AppStateViewModel State, MainWindowViewModel Vm, ComparisonResult Result)
        AfterComparing(bool withCycle)
    {
        Table t = new("dbo", "Righe",
            [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        Function f = new("dbo", "fnRowCount",
            "CREATE FUNCTION dbo.fnRowCount() RETURNS int AS BEGIN RETURN 1 END",
            IsEncrypted: false, FunctionKind: FunctionKind.Scalar);
        ComparisonResult result = new(
        [
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null),
            new DifferencePair(f.Identity, DifferenceStatus.OnlyInA, f, null),
        ]);

        AppStateViewModel state = new()
        {
            SourceConnectionString = Dev,
            TargetConnectionString = Staging,
        };
        state.PublishComparison(result, Dev, Staging);
        state.SourceDependencies = withCycle
            ? [new DependencyEdge(Fn, Tbl, EdgeKind.ModuleReference),
               new DependencyEdge(Tbl, Fn, EdgeKind.CheckConstraint)]
            : [new DependencyEdge(Tbl, Fn, EdgeKind.CheckConstraint)];
        return (state, new MainWindowViewModel(state), result);
    }

    [AvaloniaFact]
    public void A_dependency_cycle_lands_in_the_error_banner_instead_of_killing_the_window()
    {
        (AppStateViewModel state, MainWindowViewModel vm, ComparisonResult result) =
            AfterComparing(withCycle: true);

        string? script = vm.TryBuildDeployScript(result.Differences, NoBackfill);

        script.Should().BeNull();
        state.LastError.Should().NotBeNull();
        state.LastError.Should().Contain("ciclo").And.Contain("ALTER TABLE");
        vm.StatusText.Should().NotBeNullOrWhiteSpace("the status bar must not read as if a script exists");
    }

    /// <summary>
    /// The same two objects with only the one-way edge still produce a script
    /// and leave the banner alone — without this the banner could be
    /// unconditional and the test above would still be green.
    /// </summary>
    [AvaloniaFact]
    public void The_same_objects_without_the_closing_edge_still_get_their_script()
    {
        (AppStateViewModel state, MainWindowViewModel vm, ComparisonResult result) =
            AfterComparing(withCycle: false);

        string? script = vm.TryBuildDeployScript(result.Differences, NoBackfill);

        script.Should().NotBeNullOrWhiteSpace();
        state.LastError.Should().BeNull();
    }
}
