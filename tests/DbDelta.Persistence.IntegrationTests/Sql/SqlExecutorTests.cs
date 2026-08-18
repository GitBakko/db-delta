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
    private string? _skipReason;

    public async ValueTask InitializeAsync()
    {
        // Match the image used by LiveDbFixture so a single docker pull
        // services every integration test project; the default unpinned
        // MsSqlBuilder() requires the CU-suffixed image to be pre-cached
        // and Assert.Skip cannot intercept DockerImageNotFoundException.
        MsSqlContainer? container = null;
        try
        {
            // Build() is INSIDE the try, and that is the whole point. It calls
            // AbstractBuilder.Validate(), which throws ArgumentException
            // ("Docker is either not running or misconfigured", parameter
            // DockerEndpointAuthConfig) when no endpoint is configured at all.
            // Left outside, that escaped the catch below and failed the three
            // tests on the Windows job instead of skipping them — and only when
            // the runner happened to have no endpoint, so it read as a random
            // red on a commit that had not touched this file.
            container = new MsSqlBuilder()
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword("Y0urStrong!Pass")
                .Build();
            await container.StartAsync();
            _container = container;
            _connectionString = container.GetConnectionString();
        }
        catch (Exception ex)
        {
            // No daemon probe: guessing lied in BOTH directions — it saw the
            // Windows named pipe and let a Linux image fail the whole project,
            // and on Linux it never looked at the unix socket at all. Let the
            // container itself answer, and carry the real reason into the skip
            // so a green run still says why it did not assert anything.
            _skipReason = $"Testcontainers could not start MS SQL: {ex.Message}";
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// The live connection string, or a skip carrying why there is none.
    /// </summary>
    private string RequireSql()
    {
        if (_connectionString is null)
        {
            Assert.Skip(_skipReason ?? "Testcontainers MS SQL is unavailable.");
        }

        return _connectionString;
    }

    [Fact]
    public async Task ExecuteAsync_simple_create_drop_script_succeeds()
    {
        string conn = RequireSql();

        const string script = """
            CREATE TABLE t_executor_test (id INT NOT NULL);
            GO
            DROP TABLE t_executor_test;
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            conn,
            script,
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorMessage ?? "unexpected failure");
        result.BatchesExecuted.Should().Be(2);
        result.TotalDurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ExecuteAsync_failing_batch_rolls_back_and_reports_error()
    {
        string conn = RequireSql();

        // First batch succeeds; second batch will fail — the whole transaction
        // should be rolled back, so the table must not exist after the call.
        const string script = """
            CREATE TABLE t_rollback_test (id INT NOT NULL);
            GO
            INSERT INTO t_rollback_test VALUES (NULL);
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            conn,
            script,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        // Table must not exist because the transaction was rolled back.
        SqlBatchResult checkResult = await SqlExecutor.ExecuteAsync(
            conn,
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
        string conn = RequireSql();

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
            conn, script, CancellationToken.None, useOwnTransaction: false);

        result.Success.Should().BeFalse();
        SqlBatchResult check = await SqlExecutor.ExecuteAsync(
            conn,
            "IF OBJECT_ID(N'dbo.t_selfmanage_test') IS NOT NULL THROW 50000, 'leaked', 1;",
            CancellationToken.None);
        check.Success.Should().BeTrue("the script's own XACT_ABORT must have rolled back");
    }
}
