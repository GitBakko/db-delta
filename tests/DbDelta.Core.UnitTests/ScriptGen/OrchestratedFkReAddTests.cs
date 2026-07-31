using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// The up-front drop pass claims foreign keys it has to remove and a single late
/// pass puts them back. Which SHAPE it puts back, and whether it may emit an
/// ALTER TABLE at all, both depend on what the run is actually doing to the
/// holder — and both were decided from the source side unconditionally.
/// </summary>
public class OrchestratedFkReAddTests
{
    private static ForeignKey Fk(string name, string refTable, ReferentialAction onDelete) => new(
        Name: name,
        Columns: ["ParentId"],
        ReferencedSchema: "dbo",
        ReferencedTable: refTable,
        ReferencedColumns: ["Id"],
        OnDelete: onDelete,
        OnUpdate: ReferentialAction.NoAction,
        IsDisabled: false,
        IsNotForReplication: false);

    private static Table Child(string name, ForeignKey fk) =>
        new("dbo", name, [new Column("ParentId", "int", false, 1)], [fk], []);

    /// <summary>
    /// The user retypes the parent's key and leaves the child alone. The child's
    /// foreign key is dropped to free the column, so it must come back — but in
    /// the shape the TARGET holds, because the script changes nothing else about
    /// that table. Restoring the source's shape quietly applied an ON DELETE the
    /// user never ticked, under a success verdict.
    /// </summary>
    [Fact]
    public void An_unselected_holder_gets_its_own_foreign_key_back_not_the_sources()
    {
        Table parentSrc = new("dbo", "Parent",
            [new Column("Id", "bigint", false, 1)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);
        Table parentTgt = new("dbo", "Parent",
            [new Column("Id", "int", false, 1)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);

        Table childSrc = Child("Child", Fk("FK_Child_Parent", "Parent", ReferentialAction.Cascade));
        Table childTgt = Child("Child", Fk("FK_Child_Parent", "Parent", ReferentialAction.NoAction));

        DifferencePair parentPair = new(parentSrc.Identity, DifferenceStatus.Different, parentSrc, parentTgt);
        ComparisonResult result = new(
        [
            parentPair,
            new DifferencePair(childSrc.Identity, DifferenceStatus.Different, childSrc, childTgt),
        ]);

        // Only the parent is ticked.
        string sql = new ScriptGenerator().Generate(result, selection: [parentPair]);

        sql.Should().Contain("DROP CONSTRAINT [FK_Child_Parent]");
        sql.Should().Contain("ADD CONSTRAINT [FK_Child_Parent]");
        sql.Should().NotContain(
            "ON DELETE CASCADE",
            "the child was not selected, so the deploy may not change its cascade rule");
    }

    /// <summary>
    /// A source-only table the user did not tick is never created, so an
    /// ALTER TABLE against it is Msg 4902 in the middle of the deploy — and with
    /// XACT_ABORT that rolls back the rebuild that did succeed.
    /// </summary>
    [Fact]
    public void A_source_only_table_left_unselected_gets_no_add_constraint()
    {
        // An identity flip makes Parent a rebuild target, which is what pulls
        // inbound foreign keys into the orchestrated drop/re-add pair.
        Table parentSrc = new("dbo", "Parent",
            [new Column("Id", "int", false, 1, isIdentity: true)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);
        Table parentTgt = new("dbo", "Parent",
            [new Column("Id", "int", false, 1)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);

        Table newChild = Child("NewChild", Fk("FK_NewChild_Parent", "Parent", ReferentialAction.NoAction));

        DifferencePair parentPair = new(parentSrc.Identity, DifferenceStatus.Different, parentSrc, parentTgt);
        ComparisonResult result = new(
        [
            parentPair,
            new DifferencePair(newChild.Identity, DifferenceStatus.OnlyInA, newChild, null),
        ]);

        string sql = new ScriptGenerator().Generate(result, selection: [parentPair]);

        sql.Should().NotContain("CREATE TABLE [dbo].[NewChild]", "the user did not select it");
        sql.Should().NotContain(
            "[dbo].[NewChild]",
            "no statement may name a table this script never creates");
    }

    /// <summary>
    /// The mirror case: the holder IS in the selection and IS being dropped, so
    /// there is no table left to carry the constraint by the time the re-add pass
    /// runs.
    /// </summary>
    [Fact]
    public void A_holder_the_script_drops_gets_no_add_constraint()
    {
        Table parentSrc = new("dbo", "Parent",
            [new Column("Id", "int", false, 1, isIdentity: true)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);
        Table parentTgt = new("dbo", "Parent",
            [new Column("Id", "int", false, 1)],
            [new PrimaryKey("PK_Parent", ["Id"], IsClustered: true)], []);

        Table goner = Child("Goner", Fk("FK_Goner_Parent", "Parent", ReferentialAction.NoAction));

        DifferencePair parentPair = new(parentSrc.Identity, DifferenceStatus.Different, parentSrc, parentTgt);
        DifferencePair gonerPair = new(goner.Identity, DifferenceStatus.OnlyInB, null, goner);
        ComparisonResult result = new([parentPair, gonerPair]);

        string sql = new ScriptGenerator().Generate(result, selection: [parentPair, gonerPair]);

        sql.Should().Contain("DROP TABLE [dbo].[Goner];");
        sql.Should().NotContain(
            "ADD CONSTRAINT [FK_Goner_Parent]",
            "the table it would sit on has just been dropped");
    }
}
