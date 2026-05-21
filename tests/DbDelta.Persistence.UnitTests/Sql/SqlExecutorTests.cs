using DbDelta.Persistence.Sql;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Sql;

/// <summary>
/// Unit-level tests for <see cref="SqlExecutor"/>. These tests exercise the
/// script-splitting logic and error-reporting without requiring a real SQL Server
/// instance (connection failures are tested via deliberate bad connection strings).
/// </summary>
public class SqlExecutorTests
{
    // ── SplitOnGo ────────────────────────────────────────────────────────────

    [Fact]
    public void Splits_batches_on_GO_at_start_of_line()
    {
        string script = """
            SELECT 1
            GO
            SELECT 2
            GO
            SELECT 3
            """;

        string[] batches = SqlExecutor.SplitOnGo(script);

        batches.Should().HaveCount(3);
        batches[0].Should().Contain("SELECT 1");
        batches[1].Should().Contain("SELECT 2");
        batches[2].Should().Contain("SELECT 3");
    }

    [Fact]
    public void Splits_batches_case_insensitive_GO()
    {
        string script = "SELECT 1\ngo\nSELECT 2\nGO\nSELECT 3\nGo\nSELECT 4";

        string[] batches = SqlExecutor.SplitOnGo(script);

        batches.Should().HaveCount(4);
    }

    [Fact]
    public void Splits_batches_ignores_GO_in_middle_of_line()
    {
        // "GOTO" or inline "GO" references are not separators.
        string script = "PRINT 'This contains GO in the middle'\nGO\nSELECT 1";

        string[] batches = SqlExecutor.SplitOnGo(script);

        batches.Should().HaveCount(2);
    }

    [Fact]
    public void Splits_empty_script_returns_no_batches()
    {
        string[] batches = SqlExecutor.SplitOnGo(string.Empty);

        batches.Should().BeEmpty();
    }

    [Fact]
    public void Splits_whitespace_only_returns_no_batches()
    {
        string[] batches = SqlExecutor.SplitOnGo("  \n  \n  ");

        batches.Should().BeEmpty();
    }

    [Fact]
    public void Splits_no_GO_returns_single_batch()
    {
        string script = "SELECT 1; SELECT 2;";

        string[] batches = SqlExecutor.SplitOnGo(script);

        batches.Should().ContainSingle();
    }

    [Fact]
    public void Splits_trailing_GO_does_not_create_empty_batch()
    {
        string script = "SELECT 1\nGO\n";

        string[] batches = SqlExecutor.SplitOnGo(script);

        batches.Should().ContainSingle();
    }

    // ── ExecuteAsync — error paths (no live DB required) ────────────────────

    [Fact]
    public async Task ExecuteAsync_empty_script_returns_success_with_zero_batches()
    {
        // An empty/whitespace script has nothing to execute — should succeed without
        // even trying to open a connection.
        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            "Server=tcp:127.0.0.1,59999;Database=NoSuchDb;User Id=sa;Password=wrong;Encrypt=False;Connect Timeout=1",
            string.Empty,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.BatchesExecuted.Should().Be(0);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_invalid_connection_returns_error_message_redacted()
    {
        // A deliberately bad connection string with a plain-text password in the
        // error path should have the password scrubbed by ConnectionStringRedactor.
        const string cs = "Server=tcp:127.0.0.1,59999;Database=NoSuchDb;User Id=sa;Password=SuperSecret123;Encrypt=False;Connect Timeout=1";

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            cs,
            "SELECT 1",
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        // The raw password must NOT appear in the error message.
        result.ErrorMessage.Should().NotContain("SuperSecret123");
    }
}
