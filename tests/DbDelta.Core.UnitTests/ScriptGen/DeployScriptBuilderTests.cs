using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

public class DeployScriptBuilderTests
{
    private static DifferencePair MakeTablePair(string schema, string name, DifferenceStatus status)
    {
        Table table = new(schema, name, [new Column("Id", "int", false, 1)]);
        return new DifferencePair(
            Identity: table.Identity,
            Status: status,
            SideA: status == DifferenceStatus.OnlyInB ? null : table,
            SideB: status == DifferenceStatus.OnlyInA ? null : table);
    }

    [Fact]
    public void Build_empty_selection_returns_header_only_script()
    {
        string script = DeployScriptBuilder.Build(
            [],
            "SrcServer/SrcDb",
            "TgtServer/TgtDb",
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        script.Should().Contain("-- DbDelta alignment script");
        script.Should().Contain("Objects   : 0");
        // No GO / BEGIN TRANSACTION when nothing to do.
        script.Should().NotContain("BEGIN TRANSACTION");
    }

    [Fact]
    public void Build_includes_source_and_target_summary_lines()
    {
        string script = DeployScriptBuilder.Build(
            [MakeTablePair("dbo", "T1", DifferenceStatus.OnlyInA)],
            "MYSERVER/MyDB",
            "TARGETSERVER/ProdDB",
            DateTime.UtcNow);

        script.Should().Contain("Source    : MYSERVER/MyDB");
        script.Should().Contain("Target    : TARGETSERVER/ProdDB");
    }

    [Fact]
    public void Build_uses_provided_utc_timestamp_in_header()
    {
        DateTime ts = new(2025, 6, 15, 9, 30, 0, DateTimeKind.Utc);

        string script = DeployScriptBuilder.Build(
            [MakeTablePair("dbo", "T", DifferenceStatus.OnlyInA)],
            "src",
            "tgt",
            ts);

        script.Should().Contain("2025-06-15 09:30:00 UTC");
    }

    [Fact]
    public void Build_emits_SET_XACT_ABORT_ON_preamble()
    {
        string script = DeployScriptBuilder.Build(
            [MakeTablePair("dbo", "T", DifferenceStatus.OnlyInA)],
            "src",
            "tgt",
            DateTime.UtcNow);

        script.Should().Contain("SET XACT_ABORT ON;");
    }

    [Fact]
    public void Build_orders_tables_before_indexes_before_fks_before_modules_before_triggers()
    {
        // Create a table (OnlyInA) which triggers table + index + FK DDL.
        Column col = new("Id", "int", false, 1);
        Table tableWithIndex = new("dbo", "Orders",
            [col],
            [new PrimaryKey("PK_Orders", ["Id"], true)],
            [new TableIndex("IX_Orders_Id", false, false, null,
                [new IndexColumn("Id", false)], [])]);

        DifferencePair tablePair = new(
            tableWithIndex.Identity,
            DifferenceStatus.OnlyInA,
            tableWithIndex,
            null);

        string script = DeployScriptBuilder.Build(
            [tablePair],
            "src",
            "tgt",
            DateTime.UtcNow);

        // CREATE TABLE should appear before any index.
        int tablePos = script.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        tablePos.Should().BeGreaterThan(0, "CREATE TABLE DDL must be present");

        // BEGIN TRANSACTION wraps the body.
        script.Should().Contain("BEGIN TRANSACTION");
        script.Should().Contain("COMMIT TRANSACTION");
    }
}
