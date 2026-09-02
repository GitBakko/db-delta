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

    // ── Transaction-mode detection ──────────────────────────────────────────
    // The two modes are mutually exclusive: a client transaction on top of the
    // script's own gives @@TRANCOUNT = 2, so the script's COMMIT becomes a bare
    // decrement and the client then commits work the script's failure gate
    // believed it had blocked. `apply` has to pick the right side for a script
    // whose provenance it does not know.

    [Theory]
    [InlineData("BEGIN TRANSACTION\nSELECT 1;")]
    [InlineData("BEGIN TRAN\nSELECT 1;")]
    [InlineData("begin transaction\nSELECT 1;")]
    [InlineData("SET XACT_ABORT ON;\nGO\nBEGIN TRANSACTION\nSELECT 1;")]
    [InlineData("SELECT 1;\n  BEGIN TRANSACTION  \nSELECT 2;")]
    public void ScriptManagesItsOwnTransaction_detects_a_self_contained_script(string script) =>
        SqlExecutor.ScriptManagesItsOwnTransaction(script).Should().BeTrue();

    [Theory]
    [InlineData("CREATE TABLE dbo.T (Id int NOT NULL);")]
    [InlineData("SELECT 1;\nGO\nSELECT 2;")]
    [InlineData("")]
    // "BEGIN" opening a block is not "BEGIN TRANSACTION".
    [InlineData("IF 1 = 1\nBEGIN\n  SELECT 1;\nEND")]
    // A trailing `-- BEGIN TRANSACTION` does not match because `--` is not
    // whitespace, so the keyword is not at the start of the line. That is ALL
    // this row proves — see the over-detection test below for what the pattern
    // does with a mention that IS at the start of a line.
    [InlineData("SELECT 1; -- BEGIN TRANSACTION would go here")]
    public void ScriptManagesItsOwnTransaction_is_false_for_a_plain_script(string script) =>
        SqlExecutor.ScriptManagesItsOwnTransaction(script).Should().BeFalse();

    // Known limitation, asserted so it is a fact instead of a belief. The
    // pattern is purely line-anchored, so BEGIN TRANSACTION at the start of a
    // line inside a block comment — or inside a procedure body, where it opens a
    // transaction at CALL time and not at deploy time — reads as a
    // self-contained script. `apply` then skips the client transaction and a
    // failure at batch 3 of 5 leaves the target half-migrated. Distinguishing
    // those needs a parser, not a regex; recording it here beats a comment
    // claiming comments are handled.
    [Theory]
    [InlineData("/*\nBEGIN TRANSACTION\n*/\nSELECT 1;")]
    [InlineData("CREATE PROCEDURE dbo.p AS\nBEGIN TRANSACTION\n  UPDATE dbo.T SET x = 1;\n  COMMIT")]
    public void ScriptManagesItsOwnTransaction_over_detects_a_mention_at_the_start_of_a_line(string script) =>
        SqlExecutor.ScriptManagesItsOwnTransaction(script).Should().BeTrue();

    [Fact]
    public void ScriptManagesItsOwnTransaction_trusts_the_dbdelta_provenance_marker()
    {
        // No line-anchored BEGIN TRANSACTION anywhere, so the syntactic
        // fallback cannot fire: only the marker can answer this. Spelled out as
        // a literal rather than through the constant — it is a wire format that
        // has to keep matching scripts written by other versions.
        const string script = """
            -- dbdelta:transaction=script
            EXEC('BEGIN TRANSACTION');
            SELECT 1;
            """;

        SqlExecutor.ScriptManagesItsOwnTransaction(script).Should().BeTrue();
    }

    [Fact]
    public void ScriptDeclaresNoTransaction_trusts_the_marker_and_only_the_marker()
    {
        // Literal, not the constant: a wire format that has to keep matching
        // scripts written by other versions.
        SqlExecutor.ScriptDeclaresNoTransaction("-- dbdelta:transaction=none\nSELECT 1;")
            .Should().BeTrue();
        SqlExecutor.ScriptDeclaresNoTransaction("-- dbdelta:transaction=script\nBEGIN TRANSACTION\nSELECT 1;")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("CREATE TABLE dbo.T (Id int NOT NULL);")]
    [InlineData("SELECT 1;\nGO\nSELECT 2;")]
    [InlineData("")]
    // The marker has to be where the writer puts it. A script that merely
    // CONTAINS those characters — in a comment copied out of a procedure body,
    // or in a string literal — has declared nothing, and a substring match would
    // silently take its client transaction away with no flag to put it back.
    [InlineData("PRINT '-- dbdelta:transaction=none';\nCREATE TABLE dbo.T (Id int NOT NULL);")]
    [InlineData("CREATE PROCEDURE dbo.p AS\n-- dbdelta:transaction=none\nSELECT 1;")]
    // The DELIBERATE asymmetry with its twin: no syntactic fallback either. A
    // script that simply has no BEGIN TRANSACTION has declared nothing, and it
    // is exactly the case that needs a client transaction — reading its silence
    // as an opt-out would reopen the half-migration hole.
    public void ScriptDeclaresNoTransaction_is_false_when_nothing_says_so(string script) =>
        SqlExecutor.ScriptDeclaresNoTransaction(script).Should().BeFalse();

    [Fact]
    public void Both_answers_can_be_true_which_is_why_apply_orders_them()
    {
        // A CREATE PROCEDURE body carries a line-anchored BEGIN TRANSACTION
        // perfectly innocently, and the syntactic fallback cannot tell. So a
        // script that DECLARES no transaction still reads as self-managed.
        // ApplyCommand resolves it by letting the declaration win; without that
        // order the run would report "transaction": "script" while running with
        // no transaction at all.
        const string script = """
            -- dbdelta:transaction=none
            CREATE PROCEDURE dbo.p AS
            BEGIN TRANSACTION
              UPDATE dbo.T SET x = 1;
              COMMIT
            """;

        SqlExecutor.ScriptDeclaresNoTransaction(script).Should().BeTrue();
        SqlExecutor.ScriptManagesItsOwnTransaction(script).Should().BeTrue(
            "the fallback over-detects, which is the whole reason the order matters");
    }

    [Fact]
    public async Task ExecuteAsync_rejects_a_negative_command_timeout()
    {
        // 0 means unlimited; negative is a caller bug, not "very short".
        Func<Task> act = () => SqlExecutor.ExecuteAsync(
            "Server=localhost;Database=X;Connect Timeout=1",
            "SELECT 1",
            CancellationToken.None,
            useOwnTransaction: true,
            commandTimeoutSeconds: -1);

        // `await` is load-bearing: without it ThrowAsync returns a Task that is
        // dropped on the floor, and because the test method was not async there
        // was no CS4014 to warn about it. The assertion never ran.
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
