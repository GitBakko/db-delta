using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// Roadmap item 2, read half. Every catalog read used to sit on ADO.NET's 30 s
/// default, so one slow query killed a whole compare.
/// </summary>
/// <remarks>
/// No <c>[Collection]</c>, so no container: the assertion is on
/// <see cref="SqlConnection.CommandTimeout"/>, which is exactly what every
/// command created from the connection inherits, and reading it needs no
/// server. That property is get-only — the connection string is the only lever
/// there is, which is why the whole change lives in one place.
/// </remarks>
public class ConnectionTimeoutTests
{
    private const string Plain = "Server=x;Database=y;Integrated Security=true";

    [Fact]
    public void A_read_connection_carries_the_long_timeout()
    {
        new SqlConnection(ConnectionFactory.WithReadTimeout(Plain)).CommandTimeout
            .Should().Be(ConnectionFactory.ReadCommandTimeoutSeconds);
    }

    [Theory]
    // The user's own value wins even when it equals the ADO.NET default: they
    // asked for 30, and "did the caller set it" is not "does it differ".
    [InlineData("Command Timeout=30", 30)]
    // 0 means unlimited. Overwriting it with 300 would be the one change that
    // makes a working setup start failing.
    [InlineData("Command Timeout=0", 0)]
    [InlineData("Command Timeout=900", 900)]
    public void A_caller_who_asked_for_a_timeout_keeps_it(string keyword, int expected)
    {
        string original = $"{Plain};{keyword}";

        string result = ConnectionFactory.WithReadTimeout(original);

        // Untouched, not merely equivalent: nothing else in the string gets
        // normalised behind the caller's back either.
        result.Should().BeSameAs(original);
        new SqlConnection(result).CommandTimeout.Should().Be(expected);
    }
}
