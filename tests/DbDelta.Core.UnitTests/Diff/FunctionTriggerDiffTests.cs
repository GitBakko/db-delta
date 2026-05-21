using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class FunctionTriggerDiffTests
{
    // Module diff tests pass ComparisonOptions.None on purpose — BodyNormalizer
    // is invoked unconditionally inside ClassifyModule (see ModuleDiffTests).
    private static Database DbWithFns(params Function[] fns) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: [], Functions: fns, Triggers: []);

    [Fact]
    public void Function_only_in_A_is_OnlyInA()
    {
        Database a = DbWithFns(new Function("dbo", "fnSum", "BODY", IsEncrypted: false, FunctionKind: FunctionKind.Scalar));
        Database b = DbWithFns();
        DifferencePair pair = new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Function");
        pair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Function_with_identical_bodies_is_Identical()
    {
        Database a = DbWithFns(new Function("dbo", "fn", "SELECT 1", false, FunctionKind.Scalar));
        Database b = DbWithFns(new Function("dbo", "fn", "SELECT 1", false, FunctionKind.Scalar));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Function")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Function_with_substantively_different_bodies_is_Different()
    {
        Database a = DbWithFns(new Function("dbo", "fn", "SELECT 1", false, FunctionKind.Scalar));
        Database b = DbWithFns(new Function("dbo", "fn", "SELECT 2", false, FunctionKind.Scalar));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Function")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    private static Database DbWithTriggers(params Trigger[] triggers) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: [], Procedures: [], Functions: [], Triggers: triggers);

    [Fact]
    public void Trigger_only_in_A_is_OnlyInA()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "BODY", false, "dbo", "T", false, false));
        Database b = DbWithTriggers();
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Trigger_with_identical_body_and_state_is_Identical()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", false, false));
        Database b = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", false, false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void Trigger_with_identical_body_but_disabled_only_on_one_side_is_Different()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", IsDisabled: false, IsNotForReplication: false));
        Database b = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", IsDisabled: true, IsNotForReplication: false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Trigger_with_substantively_different_body_is_Different()
    {
        Database a = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 1", false, "dbo", "T", false, false));
        Database b = DbWithTriggers(new Trigger("dbo", "trg", "SELECT 2", false, "dbo", "T", false, false));
        new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "Trigger")
            .Status.Should().Be(DifferenceStatus.Different);
    }
}
