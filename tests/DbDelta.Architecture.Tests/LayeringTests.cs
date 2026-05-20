using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace DbDelta.Architecture.Tests;

public class LayeringTests
{
    private static readonly Assembly CoreAssembly =
        Assembly.Load("DbDelta.Core");

    private static readonly Assembly ProvidersLiveDbAssembly =
        Assembly.Load("DbDelta.Providers.LiveDb");

    [Fact]
    public void Core_must_not_reference_SqlClient_or_any_io_namespace()
    {
        string[] forbiddenNamespaces =
        [
            "Microsoft.Data.SqlClient",
            "System.Data.SqlClient",
            "System.Net.Http",
            "System.IO.Pipes",
            "Microsoft.AspNetCore"
        ];

        NetArchTest.Rules.TestResult result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Core must remain pure (no I/O dependencies). Offenders: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Providers_LiveDb_may_reference_SqlClient_and_Core_only()
    {
        NetArchTest.Rules.TestResult result = Types.InAssembly(ProvidersLiveDbAssembly)
            .Should()
            .HaveDependencyOnAny("DbDelta.Core", "Microsoft.Data.SqlClient", "System")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
