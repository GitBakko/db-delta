using DbDelta.Core.Dependency;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// An identity change rebuilds the table — CREATE _tmp, copy, DROP TABLE,
/// sp_rename — and SQL Server refuses the DROP with Msg 3729 while a
/// SCHEMABINDING module references it. Under XACT_ABORT the whole deploy rolls
/// back and the target is left divergent.
/// </summary>
/// <remarks>
/// <para>
/// The two negative controls below are the point of this file, not padding. The
/// obvious predicate — "any schemabound edge over this table" — refuses tables
/// that deploy perfectly well, and the parity fixture CANNOT catch that: no
/// scenario in it puts a schemabound module over a table that gets rebuilt, so
/// a wrong predicate leaves it green. Both exclusions were measured on
/// mssql/server:2022-latest.
/// </para>
/// <para>
/// A plain CHECK (Amt &gt; 0), and a PERSISTED computed column, each produce a
/// dependency row with is_schema_bound_reference = 1 whose referencing entity
/// is the table ITSELF — DependencyReader manufactures it, attributing a C/D
/// constraint's references to its parent table — and both drop without
/// complaint.
/// </para>
/// </remarks>
public class SchemaboundRebuildRefusalTests
{
    private static readonly ScriptGenerator Sut = new();

    private static ObjectIdentity TblId => new("dbo", "Ordini", "Table");
    private static ObjectIdentity ViewId => new("dbo", "V_SB", "View");

    /// <summary>The identity seed changes, which is what forces the rebuild.</summary>
    private static Table Src => new("dbo", "Ordini",
        [new Column("Id", "int", isNullable: false, ordinal: 1, isIdentity: true, identitySeed: 100, identityIncrement: 1)]);

    private static Table Tgt => new("dbo", "Ordini",
        [new Column("Id", "int", isNullable: false, ordinal: 1, isIdentity: true, identitySeed: 1, identityIncrement: 1)]);

    private static ComparisonResult Rebuild() =>
        new([new DifferencePair(TblId, DifferenceStatus.Different, Src, Tgt)]);

    private static DependencyEdge Edge(ObjectIdentity dependent, ObjectIdentity referenced, bool schemaBound) =>
        new(dependent, referenced, EdgeKind.ModuleReference, schemaBound);

    [Fact]
    public void A_rebuild_under_a_schemabound_module_is_refused_by_name()
    {
        SchemaboundRebuildException ex = Assert.Throws<SchemaboundRebuildException>(() =>
            Sut.Generate(Rebuild(), dropDependencies: [Edge(ViewId, TblId, schemaBound: true)]));

        ex.Table.Should().Be(TblId);
        ex.Binder.Should().Be(ViewId);
        // Naming the module is the entire value of answering here rather than
        // letting the server answer halfway through.
        ex.Message.Should().Contain("dbo.V_SB").And.Contain("dbo.Ordini").And.Contain("3729");
    }

    /// <summary>
    /// An ordinary view over the same table does not block anything: the flag,
    /// not the edge, is what matters.
    /// </summary>
    [Fact]
    public void An_ordinary_module_over_the_rebuilt_table_does_not_refuse() =>
        Sut.Invoking(g => g.Generate(Rebuild(), dropDependencies: [Edge(ViewId, TblId, schemaBound: false)]))
           .Should().NotThrow();

    /// <summary>
    /// NEGATIVE CONTROL 1 — the self-referencing row. Measured: a table whose own
    /// CHECK or computed column produces this row drops perfectly well.
    /// </summary>
    [Fact]
    public void A_tables_own_CHECK_or_computed_column_does_not_refuse_its_rebuild() =>
        Sut.Invoking(g => g.Generate(Rebuild(), dropDependencies: [Edge(TblId, TblId, schemaBound: true)]))
           .Should().NotThrow("a CHECK or a computed column binds the table to itself, and that never blocks a DROP");

    /// <summary>
    /// NEGATIVE CONTROL 2 — a binder this very script drops first. The DROP pass
    /// runs before the CREATE pass that carries the rebuild, so an OnlyInB module
    /// is already gone by the time DROP TABLE runs. Measured: dropping the
    /// schemabound modules and then the table succeeds.
    /// </summary>
    [Fact]
    public void A_binder_this_script_drops_first_does_not_refuse()
    {
        ComparisonResult result = new([
            new DifferencePair(TblId, DifferenceStatus.Different, Src, Tgt),
            new DifferencePair(ViewId, DifferenceStatus.OnlyInB, null,
                new View("dbo", "V_SB", "CREATE VIEW dbo.V_SB WITH SCHEMABINDING AS SELECT Id FROM dbo.Ordini", IsEncrypted: false)),
        ]);

        Sut.Invoking(g => g.Generate(result, dropDependencies: [Edge(ViewId, TblId, schemaBound: true)]))
           .Should().NotThrow("the DROP pass removes it before the rebuild ever runs");
    }

    /// <summary>
    /// A schemabound module over a DIFFERENT table is nobody's business here.
    /// </summary>
    [Fact]
    public void A_schemabound_module_over_another_table_does_not_refuse() =>
        Sut.Invoking(g => g.Generate(
                Rebuild(),
                dropDependencies: [Edge(ViewId, new ObjectIdentity("dbo", "Altro", "Table"), schemaBound: true)]))
           .Should().NotThrow();

    /// <summary>
    /// No rebuild, no refusal — a schemabound module over a table being ALTERed
    /// in place is not a problem, because nothing drops the table.
    /// </summary>
    [Fact]
    public void A_table_that_is_altered_rather_than_rebuilt_does_not_refuse()
    {
        Table src = new("dbo", "Ordini",
            [new Column("Id", "int", isNullable: false, ordinal: 1),
             new Column("Nota", "nvarchar(16)", isNullable: true, ordinal: 2)]);
        Table tgt = new("dbo", "Ordini", [new Column("Id", "int", isNullable: false, ordinal: 1)]);

        ComparisonResult result = new([new DifferencePair(TblId, DifferenceStatus.Different, src, tgt)]);

        Sut.Invoking(g => g.Generate(result, dropDependencies: [Edge(ViewId, TblId, schemaBound: true)]))
           .Should().NotThrow("an ADD COLUMN never drops the table, so SCHEMABINDING is irrelevant");
    }

    /// <summary>
    /// The edges are the TARGET's. Passing none — the shape every hand-built
    /// caller uses — must not start refusing.
    /// </summary>
    [Fact]
    public void With_no_edges_at_all_nothing_is_refused() =>
        Sut.Invoking(g => g.Generate(Rebuild())).Should().NotThrow();
}
