using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

/// <summary>
/// M5 — Sequence + Synonym + alias UDT coverage: diff, emit, identity.
/// Mirrors the assertion style used for M1-M4 kinds (no Verify dependency
/// — short, focused, name-the-expected-output style).
/// </summary>
public class M5KindsTests
{
    // ── Sequence ───────────────────────────────────────────────────────────

    [Fact]
    public void Sequence_create_emits_canonical_form()
    {
        Sequence seq = new(
            Schema: "dbo",
            Name: "OrderNumber",
            DataType: "int",
            StartValue: 1000,
            Increment: 1,
            MinValue: 1000,
            MaxValue: 999_999_999,
            IsCycling: false,
            IsCached: true,
            CacheSize: 50);

        string sql = new SequenceScriptEmitter().EmitCreate(seq);

        sql.Should().Be(
            "CREATE SEQUENCE [dbo].[OrderNumber] AS int START WITH 1000 INCREMENT BY 1 " +
            "MINVALUE 1000 MAXVALUE 999999999 NO CYCLE CACHE 50;");
    }

    [Fact]
    public void Sequence_create_with_defaults_emits_no_minvalue_no_cache()
    {
        Sequence seq = new(
            Schema: "dbo", Name: "S", DataType: "bigint",
            StartValue: 1, Increment: 1, MinValue: null, MaxValue: null,
            IsCycling: true, IsCached: false, CacheSize: null);

        string sql = new SequenceScriptEmitter().EmitCreate(seq);
        sql.Should().Contain("NO MINVALUE").And.Contain("NO MAXVALUE")
           .And.Contain(" CYCLE").And.Contain("NO CACHE");
    }

    [Fact]
    public void Sequence_drop_emits_drop()
    {
        Sequence seq = new("dbo", "S", "int", 1, 1, null, null, false, false, null);
        new SequenceScriptEmitter().EmitDrop(seq).Should().Be("DROP SEQUENCE [dbo].[S];");
    }

    [Fact]
    public void Sequence_diff_only_in_source()
    {
        Database a = new("d", [], [], [], [], [], [])
        { Sequences = [new("dbo", "S", "int", 1, 1, null, null, false, true, null)] };
        Database b = new("d", [], [], [], [], [], []);

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(p =>
            p.Identity.Kind == "Sequence" && p.Status == DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Sequence_diff_increment_changed_flags_different()
    {
        Sequence sA = new("dbo", "S", "int", 1, 1, null, null, false, true, null);
        Sequence sB = sA with { Increment = 2 };
        Database a = new("d", [], [], [], [], [], []) { Sequences = [sA] };
        Database b = new("d", [], [], [], [], [], []) { Sequences = [sB] };

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(p =>
            p.Identity.Kind == "Sequence" && p.Status == DifferenceStatus.Different);
    }

    // ── Synonym ────────────────────────────────────────────────────────────

    [Fact]
    public void Synonym_create_emits_for_clause()
    {
        Synonym syn = new("dbo", "Cust", "[other_db].[dbo].[Customer]",
            null, "other_db", "dbo", "Customer");

        string sql = new SynonymScriptEmitter().EmitCreate(syn);
        sql.Should().Be("CREATE SYNONYM [dbo].[Cust] FOR [other_db].[dbo].[Customer];");
    }

    [Fact]
    public void Synonym_diff_changed_target_flags_different()
    {
        Synonym a = new("dbo", "Cust", "[a].[dbo].[Customer]", null, "a", "dbo", "Customer");
        Synonym b = a with { BaseObjectName = "[b].[dbo].[Customer]" };
        Database dA = new("d", [], [], [], [], [], []) { Synonyms = [a] };
        Database dB = new("d", [], [], [], [], [], []) { Synonyms = [b] };

        ComparisonResult r = new ComparisonEngine().Compare(dA, dB, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(p =>
            p.Identity.Kind == "Synonym" && p.Status == DifferenceStatus.Different);
    }

    // ── User-Defined Type ──────────────────────────────────────────────────

    [Fact]
    public void Udt_create_emits_from_clause_with_size_and_not_null()
    {
        UserDefinedType udt = new(
            Schema: "dbo", Name: "PhoneNumber",
            BaseTypeName: "nvarchar", MaxLength: 40, Precision: 0, Scale: 0,
            IsNullable: false);

        string sql = new UserDefinedTypeScriptEmitter().EmitCreate(udt);
        // MaxLength 40 with nvarchar means 20 chars per SQL Server convention.
        sql.Should().Be("CREATE TYPE [dbo].[PhoneNumber] FROM nvarchar(20) NOT NULL;");
    }

    [Fact]
    public void Udt_diff_basetype_changed_flags_different()
    {
        UserDefinedType a = new("dbo", "T", "varchar", 50, 0, 0, true);
        UserDefinedType b = a with { BaseTypeName = "nvarchar" };
        Database dA = new("d", [], [], [], [], [], []) { UserDefinedTypes = [a] };
        Database dB = new("d", [], [], [], [], [], []) { UserDefinedTypes = [b] };

        ComparisonResult r = new ComparisonEngine().Compare(dA, dB, ComparisonOptions.Default);
        r.Differences.Should().ContainSingle(p =>
            p.Identity.Kind == "UserDefinedType" && p.Status == DifferenceStatus.Different);
    }

    [Fact]
    public void Udt_identity_kind_is_userdefinedtype()
    {
        UserDefinedType udt = new("dbo", "T", "int", 4, 10, 0, true);
        udt.Identity.Kind.Should().Be("UserDefinedType");
    }
}
