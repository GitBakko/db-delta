using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Spec §1.2 13th object kind — table-type UDT (UDTT). Distinct from alias
/// UDT: this is a typed row set, commonly used as TVPs to stored procedures.
/// </summary>
public class TableTypeUdtTests
{
    private static readonly ScriptGenerator Sut = new();

    [Fact]
    public void OnlyInA_TableType_emits_CREATE_TYPE_AS_TABLE()
    {
        TableTypeUdt udt = new("dbo", "OrderItemTvp",
        [
            new Column("ProductId", "int", isNullable: false, ordinal: 1),
            new Column("Quantity", "int", isNullable: false, ordinal: 2),
        ]);
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInA, udt, null),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("CREATE TYPE [dbo].[OrderItemTvp] AS TABLE");
        sql.Should().Contain("[ProductId] [int] NOT NULL");
        sql.Should().Contain("[Quantity] [int] NOT NULL");
    }

    [Fact]
    public void OnlyInB_TableType_emits_DROP_TYPE()
    {
        TableTypeUdt udt = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            new DifferencePair(udt.Identity, DifferenceStatus.OnlyInB, null, udt),
        ]);

        string sql = Sut.Generate(result);

        sql.Should().Contain("DROP TYPE [dbo].[OrderItemTvp]");
    }

    [Fact]
    public void Different_TableType_emits_DROP_then_CREATE_in_correct_order()
    {
        TableTypeUdt src = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "bigint", isNullable: false, ordinal: 1)]);
        TableTypeUdt tgt = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "int", isNullable: false, ordinal: 1)]);
        ComparisonResult result = new(
        [
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt),
        ]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP TYPE [dbo].[OrderItemTvp]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE TYPE [dbo].[OrderItemTvp] AS TABLE", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
    }

    [Fact]
    public void TableTypes_appear_in_prologue_after_alias_UDTs_and_before_tables()
    {
        UserDefinedType aliasUdt = new("dbo", "ShortDescription", "nvarchar", 200, 0, 0, true);
        TableTypeUdt tt = new("dbo", "OrderItemTvp",
            [new Column("ProductId", "int", isNullable: false, ordinal: 1)]);
        Table table = new("dbo", "Orders",
            [new Column("Id", "int", isNullable: false, ordinal: 1)]);

        ComparisonResult result = new(
        [
            new DifferencePair(aliasUdt.Identity, DifferenceStatus.OnlyInA, aliasUdt, null),
            new DifferencePair(tt.Identity, DifferenceStatus.OnlyInA, tt, null),
            new DifferencePair(table.Identity, DifferenceStatus.OnlyInA, table, null),
        ]);

        string sql = Sut.Generate(result);

        int aliasIdx = sql.IndexOf("CREATE TYPE [dbo].[ShortDescription]", StringComparison.Ordinal);
        int ttIdx = sql.IndexOf("CREATE TYPE [dbo].[OrderItemTvp] AS TABLE", StringComparison.Ordinal);
        int tableIdx = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);

        aliasIdx.Should().BeGreaterThan(0);
        ttIdx.Should().BeGreaterThan(aliasIdx);
        tableIdx.Should().BeGreaterThan(ttIdx);
    }

    [Fact]
    public void Identical_TableTypes_are_reported_by_engine_with_no_DDL()
    {
        TableTypeUdt udt = new("dbo", "Tvp",
            [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [udt] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [udt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);
        string sql = Sut.Generate(result);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Identical);
        sql.Should().NotContain("CREATE TYPE");
        sql.Should().NotContain("DROP TYPE");
    }

    [Fact]
    public void Engine_classifies_TableType_with_different_columns_as_Different()
    {
        TableTypeUdt src = new("dbo", "Tvp",
            [new Column("Id", "bigint", isNullable: false, ordinal: 1)]);
        TableTypeUdt tgt = new("dbo", "Tvp",
            [new Column("Id", "int", isNullable: false, ordinal: 1)]);
        Database a = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [src] };
        Database b = new("Db", [new Schema("dbo")], []) { TableTypeUdts = [tgt] };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, Options.ComparisonOptions.Default);

        result.Differences.Should().ContainSingle(d => d.Identity.Kind == "TableType")
            .Which.Status.Should().Be(DifferenceStatus.Different);
    }
}
