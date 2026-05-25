using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Parity-driven fix (M13-FIX.5 / task #31): scenario 08 of the 2026-05-25
/// Redgate parity run exposed that DbDelta's DROP+CREATE breaks any
/// column with <c>DEFAULT NEXT VALUE FOR &lt;seq&gt;</c>. SQL Server can
/// change every sequence property except the data type via
/// <c>ALTER SEQUENCE</c>; emit the minimum-clause ALTER when the data
/// type stays the same, and fall back to DROP+CREATE only when the data
/// type itself changes.
/// </summary>
public class SequenceAlterTests
{
    private static readonly ScriptGenerator Sut = new();

    [Fact]
    public void Different_Sequence_with_only_seed_change_emits_ALTER_RESTART_WITH()
    {
        Sequence src = Seq(start: 100, inc: 1);
        Sequence tgt = Seq(start: 1, inc: 1);
        ComparisonResult result = Result(src, tgt);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER SEQUENCE [dbo].[OrderNo] RESTART WITH 100");
        sql.Should().NotContain("DROP SEQUENCE");
        sql.Should().NotContain("CREATE SEQUENCE");
    }

    [Fact]
    public void Different_Sequence_with_only_increment_change_emits_ALTER_INCREMENT_BY()
    {
        Sequence src = Seq(start: 1, inc: 5);
        Sequence tgt = Seq(start: 1, inc: 1);
        ComparisonResult result = Result(src, tgt);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER SEQUENCE [dbo].[OrderNo] INCREMENT BY 5");
        sql.Should().NotContain("DROP SEQUENCE");
    }

    [Fact]
    public void Different_Sequence_with_seed_AND_increment_change_emits_single_ALTER_with_both_clauses()
    {
        Sequence src = Seq(start: 100, inc: 5);
        Sequence tgt = Seq(start: 1, inc: 1);
        ComparisonResult result = Result(src, tgt);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER SEQUENCE [dbo].[OrderNo] RESTART WITH 100 INCREMENT BY 5");
        sql.Should().NotContain("DROP SEQUENCE");
    }

    [Fact]
    public void Different_Sequence_data_type_change_still_uses_DROP_then_CREATE()
    {
        Sequence src = Seq(dataType: "int", start: 1, inc: 1);
        Sequence tgt = Seq(dataType: "bigint", start: 1, inc: 1);
        // Force ComparisonEngine to classify as Different even though the
        // helper has default seeds matching — bypass the engine and pass
        // the pair directly. The script generator does not re-validate
        // equality, only the status.
        ComparisonResult result = new(
        [
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt),
        ]);

        string sql = Sut.Generate(result);

        int dropIdx = sql.IndexOf("DROP SEQUENCE [dbo].[OrderNo]", StringComparison.Ordinal);
        int createIdx = sql.IndexOf("CREATE SEQUENCE [dbo].[OrderNo]", StringComparison.Ordinal);
        dropIdx.Should().BeGreaterThan(0);
        createIdx.Should().BeGreaterThan(dropIdx);
        sql.Should().NotContain("ALTER SEQUENCE");
    }

    [Fact]
    public void Different_Sequence_with_cycling_toggle_emits_ALTER_CYCLE_or_NO_CYCLE()
    {
        Sequence src = Seq(start: 1, inc: 1) with { IsCycling = true };
        Sequence tgt = Seq(start: 1, inc: 1) with { IsCycling = false };
        ComparisonResult result = Result(src, tgt);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER SEQUENCE [dbo].[OrderNo] CYCLE");
        sql.Should().NotContain("DROP SEQUENCE");
    }

    [Fact]
    public void Different_Sequence_with_cache_size_change_emits_ALTER_CACHE_n()
    {
        Sequence src = Seq(start: 1, inc: 1) with { IsCached = true, CacheSize = 50 };
        Sequence tgt = Seq(start: 1, inc: 1) with { IsCached = true, CacheSize = 20 };
        ComparisonResult result = Result(src, tgt);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER SEQUENCE [dbo].[OrderNo] CACHE 50");
        sql.Should().NotContain("DROP SEQUENCE");
    }

    [Fact]
    public void Different_Sequence_with_cache_disable_emits_NO_CACHE()
    {
        Sequence src = Seq(start: 1, inc: 1) with { IsCached = false, CacheSize = null };
        Sequence tgt = Seq(start: 1, inc: 1) with { IsCached = true, CacheSize = 20 };
        ComparisonResult result = Result(src, tgt);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER SEQUENCE [dbo].[OrderNo] NO CACHE");
    }

    [Fact]
    public void Different_Sequence_with_minvalue_maxvalue_change_emits_minimal_ALTER()
    {
        Sequence src = Seq(start: 1, inc: 1) with { MinValue = 10, MaxValue = 1000 };
        Sequence tgt = Seq(start: 1, inc: 1) with { MinValue = null, MaxValue = null };
        ComparisonResult result = Result(src, tgt);

        string sql = Sut.Generate(result);

        sql.Should().Contain("ALTER SEQUENCE [dbo].[OrderNo] MINVALUE 10 MAXVALUE 1000");
    }

    private static Sequence Seq(string dataType = "bigint", long start = 1, long inc = 1) =>
        new(
            Schema: "dbo",
            Name: "OrderNo",
            DataType: dataType,
            StartValue: start,
            Increment: inc,
            MinValue: null,
            MaxValue: null,
            IsCycling: false,
            IsCached: true,
            CacheSize: 20);

    private static ComparisonResult Result(Sequence src, Sequence tgt) => new(
    [
        new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt),
    ]);
}
