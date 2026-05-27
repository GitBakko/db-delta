using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Dependency;

public class DependencyEdgeTests
{
    [Fact]
    public void Edge_carries_dependent_referenced_and_kind()
    {
        ObjectIdentity table = new("dbo", "Customer", "Table");
        ObjectIdentity fn = new("dbo", "fnFullName", "Function");

        DependencyEdge edge = new(Dependent: table, Referenced: fn, Kind: EdgeKind.ComputedColumn);

        edge.Dependent.Should().Be(table);
        edge.Referenced.Should().Be(fn);
        edge.Kind.Should().Be(EdgeKind.ComputedColumn);
    }
}
