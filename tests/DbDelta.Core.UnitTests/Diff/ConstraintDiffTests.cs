using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ConstraintDiffTests
{
    private static Database DbWithTable(Table t) =>
        new("X", [new Schema("dbo")], [t]);

    private static Table TableWith(params Constraint[] constraints) =>
        new("dbo", "Customer",
            Columns: [new Column("Id", "int", false, 1)],
            Constraints: constraints,
            Indexes: []);

    [Fact]
    public void Identical_PK_yields_Identical()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));
        Database b = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Should().ContainSingle().Which.Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Different_PK_column_order_yields_Different()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id", "TenantId"], true)));
        Database b = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["TenantId", "Id"], true)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Missing_PK_on_target_yields_Different()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK_Customer", ["Id"], true)));
        Database b = DbWithTable(TableWith());

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Different_FK_referenced_table_yields_Different()
    {
        ForeignKey fkA = new("FK", ["CustomerId"], "dbo", "Customer", ["Id"],
            ReferentialAction.NoAction, ReferentialAction.NoAction, false, false);
        ForeignKey fkB = new("FK", ["CustomerId"], "dbo", "Client", ["Id"],
            ReferentialAction.NoAction, ReferentialAction.NoAction, false, false);

        Database a = DbWithTable(TableWith(fkA));
        Database b = DbWithTable(TableWith(fkB));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Check_constraint_expression_change_yields_Different()
    {
        Database a = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=0)", false, false)));
        Database b = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=1)", false, false)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Check_constraint_expression_differing_only_in_whitespace_is_Identical()
    {
        // sys.check_constraints.definition keeps server-dependent whitespace —
        // cosmetic newline/spacing drift must never classify as Different
        // (regression: un-flattenable diffs that reappeared right after
        // applying the generated alignment script).
        Database a = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=0\r\nAND [Age]<=120)", false, false)));
        Database b = DbWithTable(TableWith(new CheckConstraint("CK", "([Age]>=0 AND [Age]<=120)", false, false)));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Default_constraint_expression_differing_only_in_whitespace_is_Identical()
    {
        Database a = DbWithTable(TableWith(new DefaultConstraint("DF", "Id", "(getdate())\r\n")));
        Database b = DbWithTable(TableWith(new DefaultConstraint("DF", "Id", "(getdate())")));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }

    // ── Auto-named constraints (roadmap item 12) ───────────────────────────
    //
    // SQL Server derives the suffix of a name it minted itself from the
    // constraint's object_id, so the two servers never agree on it. Paired by
    // name, every table with an inline DEFAULT or an unnamed CHECK/PK is
    // Different forever and can never be flattened.

    private static DefaultConstraint AutoDefault(string name, string column, string expression) =>
        new(name, column, expression) { IsSystemNamed = true };

    [Fact]
    public void Auto_named_defaults_with_the_same_expression_are_Identical_despite_the_hash()
    {
        Database a = DbWithTable(TableWith(AutoDefault("DF__Ordini__Stato__3B75D760", "Stato", "((0))")));
        Database b = DbWithTable(TableWith(AutoDefault("DF__Ordini__Stato__1A14E395", "Stato", "((0))")));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Auto_named_defaults_with_a_different_expression_are_still_Different()
    {
        Database a = DbWithTable(TableWith(AutoDefault("DF__Ordini__Stato__3B75D760", "Stato", "((0))")));
        Database b = DbWithTable(TableWith(AutoDefault("DF__Ordini__Stato__1A14E395", "Stato", "((1))")));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Auto_named_defaults_on_different_columns_are_Different()
    {
        Database a = DbWithTable(TableWith(AutoDefault("DF__Ordini__Stato__3B75D760", "Stato", "((0))")));
        Database b = DbWithTable(TableWith(AutoDefault("DF__Ordini__Tipo__1A14E395", "Tipo", "((0))")));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    /// <summary>
    /// One side asked for a name and the other did not. That IS a difference —
    /// the target holds a name the source never declared — and the script that
    /// removes it is the one that makes the two converge.
    /// </summary>
    [Fact]
    public void An_auto_named_constraint_does_not_pair_with_an_explicitly_named_one()
    {
        Database a = DbWithTable(TableWith(AutoDefault("DF__Ordini__Stato__3B75D760", "Stato", "((0))")));
        Database b = DbWithTable(TableWith(new DefaultConstraint("DF_Ordini_Stato", "Stato", "((0))")));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Auto_named_primary_keys_on_the_same_columns_are_Identical()
    {
        Database a = DbWithTable(TableWith(
            new PrimaryKey("PK__Ordini__3214EC0762B6F0DA", ["Id"], true) { IsSystemNamed = true }));
        Database b = DbWithTable(TableWith(
            new PrimaryKey("PK__Ordini__3214EC07A1B2C3D4", ["Id"], true) { IsSystemNamed = true }));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void An_auto_named_primary_key_that_stopped_being_clustered_is_Different()
    {
        Database a = DbWithTable(TableWith(
            new PrimaryKey("PK__Ordini__3214EC0762B6F0DA", ["Id"], true) { IsSystemNamed = true }));
        Database b = DbWithTable(TableWith(
            new PrimaryKey("PK__Ordini__3214EC07A1B2C3D4", ["Id"], false) { IsSystemNamed = true }));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    /// <summary>
    /// Shape pairing has to consume its match. Two source CHECKs sharing an
    /// expression must not both claim the target's single one and leave the
    /// target's OTHER constraint unexamined — the counts match either way, so
    /// without consume-once the table comes out Identical with a CHECK on each
    /// side that the other does not have.
    /// </summary>
    [Fact]
    public void Two_auto_named_checks_of_the_same_shape_cannot_both_claim_one_target_check()
    {
        Database a = DbWithTable(TableWith(
            new CheckConstraint("CK__Ordini__A", "([Qta]>(0))", false, false) { IsSystemNamed = true },
            new CheckConstraint("CK__Ordini__B", "([Qta]>(0))", false, false) { IsSystemNamed = true }));
        Database b = DbWithTable(TableWith(
            new CheckConstraint("CK__Ordini__C", "([Qta]>(0))", false, false) { IsSystemNamed = true },
            new CheckConstraint("CK__Ordini__D", "([Prezzo]>(0))", false, false) { IsSystemNamed = true }));

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void IgnoreKeys_option_skips_PK_diff()
    {
        Database a = DbWithTable(TableWith(new PrimaryKey("PK", ["Id"], true)));
        Database b = DbWithTable(TableWith());

        ComparisonResult r = new ComparisonEngine()
            .Compare(a, b, ComparisonOptions.Default | ComparisonOptions.IgnoreKeys);

        r.Differences.Single().Status.Should().Be(DifferenceStatus.Identical);
    }
}
