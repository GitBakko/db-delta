using DbDelta.Persistence.Sql;
using FluentAssertions;
using Testcontainers.MsSql;
using Xunit;

namespace DbDelta.Persistence.IntegrationTests.Sql;

/// <summary>
/// Integration tests for <see cref="SqlExecutor"/> that require a live SQL Server
/// instance. The test spins up a Testcontainers MSSQL container; if Docker is not
/// available, each test is skipped via <c>Assert.Skip</c> (xunit.v3 native).
/// </summary>
public sealed class SqlExecutorTests : IAsyncLifetime
{
    private MsSqlContainer? _container;
    private string? _connectionString;

    public async ValueTask InitializeAsync()
    {
        bool dockerAvailable = await IsDockerAvailableAsync();
        if (!dockerAvailable)
        {
            return;
        }

        // Match the image used by LiveDbFixture so a single docker pull
        // services every integration test project; the default unpinned
        // MsSqlBuilder() requires the CU-suffixed image to be pre-cached
        // and Assert.Skip cannot intercept DockerImageNotFoundException.
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .WithPassword("Y0urStrong!Pass")
            .Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecuteAsync_simple_create_drop_script_succeeds()
    {
        if (_connectionString is null)
        {
            Assert.Skip("Docker not available — skipping SQL executor integration test.");
        }

        const string script = """
            CREATE TABLE t_executor_test (id INT NOT NULL);
            GO
            DROP TABLE t_executor_test;
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            _connectionString,
            script,
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage ?? "unexpected failure");
        result.BatchesExecuted.Should().Be(2);
        result.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ExecuteAsync_failing_batch_rolls_back_and_reports_error()
    {
        if (_connectionString is null)
        {
            Assert.Skip("Docker not available — skipping SQL executor integration test.");
        }

        // First batch succeeds; second batch will fail — the whole transaction
        // should be rolled back, so the table must not exist after the call.
        const string script = """
            CREATE TABLE t_rollback_test (id INT NOT NULL);
            GO
            INSERT INTO t_rollback_test VALUES (NULL);
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            _connectionString,
            script,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        // Table must not exist because the transaction was rolled back.
        SqlBatchResult checkResult = await SqlExecutor.ExecuteAsync(
            _connectionString,
            """
            IF OBJECT_ID(N'dbo.t_rollback_test') IS NOT NULL
                THROW 50000, 'Table still exists — rollback failed!', 1;
            """,
            CancellationToken.None);
        checkResult.Success.Should().BeTrue("table should have been rolled back");
    }

    [Fact]
    public async Task ExecuteAsync_without_own_transaction_lets_script_self_manage_rollback()
    {
        if (_connectionString is null) { Assert.Skip("Docker not available."); }

        const string script = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            GO
            CREATE TABLE t_selfmanage_test (id INT NOT NULL);
            GO
            INSERT INTO t_selfmanage_test VALUES (NULL);
            GO
            COMMIT TRANSACTION;
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            _connectionString!, script, CancellationToken.None, useOwnTransaction: false);

        result.Success.Should().BeFalse();
        SqlBatchResult check = await SqlExecutor.ExecuteAsync(
            _connectionString!,
            "IF OBJECT_ID(N'dbo.t_selfmanage_test') IS NOT NULL THROW 50000, 'leaked', 1;",
            CancellationToken.None);
        check.Success.Should().BeTrue("the script's own XACT_ABORT must have rolled back");
    }

    private static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            using System.Net.Sockets.TcpClient tc = new();
            await tc.ConnectAsync("localhost", 2375).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch
        {
            // Docker daemon not reachable on localhost:2375 — also try the named pipe.
            return File.Exists(@"\\.\pipe\docker_engine");
        }
    }
}
