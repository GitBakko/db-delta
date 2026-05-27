using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Dependency;

public class DependencyCycleExceptionTests
{
    [Fact]
    public void Message_lists_the_cycle_path()
    {
        ObjectIdentity a = new("dbo", "A", "View");
        ObjectIdentity b = new("dbo", "B", "View");

        DependencyCycleException ex = new([a, b, a]);

        ex.Cycle.Should().Equal(a, b, a);
        ex.Message.Should().Contain("dbo.A").And.Contain("dbo.B");
    }
}
