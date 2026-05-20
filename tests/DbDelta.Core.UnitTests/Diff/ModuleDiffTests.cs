using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class ModuleDiffTests
{
    // Module diff tests pass ComparisonOptions.None on purpose: BodyNormalizer
    // is invoked unconditionally inside ClassifyModule (any future IgnoreWhitespace
    // flag must therefore not gate the whitespace test below). Sibling table-diff
    // tests use ComparisonOptions.Default — that divergence is intentional.
    private static Database Db(params View[] views) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [], Views: views, Procedures: []);

    [Fact]
    public void View_only_in_A_is_OnlyInA()
    {
        Database a = Db(new View("dbo", "vCustomer", "SELECT 1", IsEncrypted: false));
        Database b = Db();
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        DifferencePair pair = result.Differences.Single(p => p.Identity.Kind == "View");
        pair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void View_only_in_B_is_OnlyInB()
    {
        Database a = Db();
        Database b = Db(new View("dbo", "vCustomer", "SELECT 1", IsEncrypted: false));
        DifferencePair pair = new ComparisonEngine().Compare(a, b, ComparisonOptions.None)
            .Differences.Single(p => p.Identity.Kind == "View");
        pair.Status.Should().Be(DifferenceStatus.OnlyInB);
    }

    [Fact]
    public void Identical_view_bodies_are_Identical()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences
            .Where(p => p.Identity.Kind == "View")
            .Should().OnlyContain(p => p.Status == DifferenceStatus.Identical);
    }

    [Fact]
    public void View_body_differing_only_in_whitespace_is_Identical()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1\r\nAS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT  1 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void View_body_differing_substantively_is_Different()
    {
        Database a = Db(new View("dbo", "v", "SELECT 1 AS Id", IsEncrypted: false));
        Database b = Db(new View("dbo", "v", "SELECT 2 AS Id", IsEncrypted: false));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Encrypted_view_compared_to_encrypted_view_is_Different_because_bodies_are_opaque()
    {
        // We cannot prove equality of opaque bodies; the safe default is Different.
        Database a = Db(new View("dbo", "v", Body: null, IsEncrypted: true));
        Database b = Db(new View("dbo", "v", Body: null, IsEncrypted: true));
        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.None);
        result.Differences.Single(p => p.Identity.Kind == "View")
            .Status.Should().Be(DifferenceStatus.Different);
    }
}
