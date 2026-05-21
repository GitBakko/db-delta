using System.Diagnostics;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.IntegrationTests.Sql;

public class ConnectionTesterTests
{
    [Fact]
    public async Task Bad_connection_string_fails_fast_and_returns_failure()
    {
        var sw = Stopwatch.StartNew();
        ConnectionTester.TestResult result = await ConnectionTester.TestAsync(
            "Server=tcp:127.0.0.1,59999;Database=NoSuchDb;User Id=sa;Password=wrong;Encrypt=False;Connect Timeout=2",
            CancellationToken.None);
        sw.Stop();
        result.Success.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
        sw.Elapsed.TotalSeconds.Should().BeLessThan(15, "Connect Timeout caps the wait");
    }
}
