using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// A table rebuild leaves every non-schemabound view and table-valued function
/// that reads it holding the column list it cached at CREATE time, and they go
/// on answering SELECTs, so nothing looks wrong.
/// </summary>
/// <remarks>
/// Measured on <c>mssql/server:2022-latest</c> by running the rebuild dance
/// statement by statement: base column <c>int</c> → <c>bigint</c>, and
/// <c>sys.columns</c> for the view still reporting <c>int</c> afterwards, with
/// a clean <c>SELECT</c> through it. This is the only SILENT failure on the
/// rebuild path — the schemabound case refuses the <c>DROP TABLE</c> outright
/// with Msg 3729, loudly, and is a separate open entry.
/// </remarks>
public class ModuleRefreshTests
{
    private static readonly ScriptGenerator Sut = new();

    private static Table Plain(string name) =>
        new("dbo", name,
            [new Column("Id", "int", isNullable: false, ordinal: 1),
             new Column("Nota", "nvarchar(50)", isNullable: true, ordinal: 2)],
            [], []);

    /// <summary>The same table with Id turned into an identity — the one change that rebuilds.</summary>
    private static Table Rebuilt(string name) =>
        new("dbo", name,
            [new Column("Id", "int", isNullable: false, ordinal: 1,
                isIdentity: true, identitySeed: 1, identityIncrement: 1),
             new Column("Nota", "nvarchar(50)", isNullable: true, ordinal: 2)],
            [], []);

    private static ObjectIdentity Id(string name, string kind) => new("dbo", name, kind);

    private static DependencyEdge Reads(string reader, string readerKind, string readName, string readKind) =>
        new(Id(reader, readerKind), Id(readName, readKind), EdgeKind.ModuleReference);

    private static string Run(string table, params DependencyEdge[] edges)
    {
        Table oldT = Plain(table);
        Table newT = Rebuilt(table);
        ComparisonResult result = new(
        [
            new DifferencePair(newT.Identity, DifferenceStatus.Different, newT, oldT),
        ]);
        return Sut.Generate(result, dependencies: edges);
    }

    [Fact]
    public void A_view_over_a_rebuilt_table_is_refreshed()
    {
        Run("Ordine", Reads("vOrdine", "View", "Ordine", "Table"))
            .Should().Contain("EXEC sys.sp_refreshsqlmodule N'[dbo].[vOrdine]';");
    }

    [Fact]
    public void A_table_valued_function_over_a_rebuilt_table_is_refreshed_too()
    {
        // Measured: an inline TVF's sys.columns row went stale exactly like a
        // view's. sp_refreshview would not even accept it — it takes views
        // only — which is why the verb here is sp_refreshsqlmodule.
        Run("Ordine", Reads("fOrdine", "Function", "Ordine", "Table"))
            .Should().Contain("EXEC sys.sp_refreshsqlmodule N'[dbo].[fOrdine]';");
    }

    [Fact]
    public void A_view_over_a_view_is_refreshed_after_the_one_it_reads()
    {
        // Measured: refreshing the inner view did NOT fix the outer one, so the
        // walk has to reach it — and reach it second. Refreshing the outer one
        // first would re-cache the inner one's stale answer.
        string sql = Run("Ordine",
            Reads("vInterna", "View", "Ordine", "Table"),
            Reads("vEsterna", "View", "vInterna", "View"));

        int inner = sql.IndexOf("N'[dbo].[vInterna]'", StringComparison.Ordinal);
        int outer = sql.IndexOf("N'[dbo].[vEsterna]'", StringComparison.Ordinal);

        inner.Should().BeGreaterThanOrEqualTo(0);
        outer.Should().BeGreaterThan(inner);
    }

    [Fact]
    public void A_module_reached_only_through_a_procedure_is_still_refreshed()
    {
        // The walk does not stop at a kind it will not refresh: a procedure is
        // not stale itself but can stand between the table and a view that is.
        Run("Ordine",
            Reads("pMezzo", "Procedure", "Ordine", "Table"),
            Reads("vFinale", "View", "pMezzo", "Procedure"))
            .Should().Contain("N'[dbo].[vFinale]'");
    }

    // ── negative controls ────────────────────────────────────────────────

    [Fact]
    public void A_procedure_is_not_refreshed()
    {
        // Measured, and it is why this is not "refresh everything that reads
        // it": asked through sys.dm_exec_describe_first_result_set a procedure
        // over the rebuilt table already reported the NEW type. A statement
        // that changes nothing is noise in a script a human has to approve.
        Run("Ordine", Reads("pOrdine", "Procedure", "Ordine", "Table"))
            .Should().NotContain("sp_refreshsqlmodule");
    }

    [Fact]
    public void A_view_over_a_table_that_was_not_rebuilt_is_left_alone()
    {
        Run("Ordine", Reads("vAltra", "View", "Cliente", "Table"))
            .Should().NotContain("sp_refreshsqlmodule");
    }

    [Fact]
    public void Nothing_is_emitted_when_no_table_is_rebuilt()
    {
        Table t = Plain("Ordine");
        ComparisonResult result = new(
        [
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null),
        ]);

        Sut.Generate(result, dependencies: [Reads("vOrdine", "View", "Ordine", "Table")])
           .Should().NotContain("sp_refreshsqlmodule");
    }

    [Fact]
    public void A_dependency_cycle_between_modules_does_not_hang_or_repeat()
    {
        // Two views that read each other cannot exist on a live server, but the
        // edge list is data from a catalog and the walk must not trust it.
        string sql = Run("Ordine",
            Reads("vA", "View", "Ordine", "Table"),
            Reads("vB", "View", "vA", "View"),
            Reads("vA", "View", "vB", "View"));

        sql.Split("N'[dbo].[vA]'").Length.Should().Be(2, "each module is refreshed exactly once");
    }

    [Fact]
    public void The_module_name_is_bracket_quoted_inside_a_string_literal()
    {
        // Two quoting rules at once: sp_refreshsqlmodule takes a LITERAL, so an
        // apostrophe in the name must double, while the name inside it is still
        // a bracketed identifier.
        Run("Ordine", Reads("v'Brien", "View", "Ordine", "Table"))
            .Should().Contain("N'[dbo].[v''Brien]';");
    }
}
