using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class DatabaseDependenciesTests
{
    [Fact]
    public void Dependencies_defaults_to_empty()
    {
        Database db = new("Db", Schemas: [], Tables: []);
        db.Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void Dependencies_round_trips_via_init()
    {
        DependencyEdge e = new(
            new("dbo", "Customer", "Table"),
            new("dbo", "fnX", "Function"),
            EdgeKind.ComputedColumn);

        Database db = new("Db", Schemas: [], Tables: []) { Dependencies = [e] };

        db.Dependencies.Should().ContainSingle().Which.Should().Be(e);
    }
}
