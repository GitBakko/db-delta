using System.Text;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class DeployErrorHandlingTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task Failing_step_aborts_rolls_back_and_reports_failure()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DeployErr", ct);

        const string script = """
            SET XACT_ABORT ON;
            GO
            BEGIN TRANSACTION;
            GO
            IF @@ERROR <> 0 SET NOEXEC ON;
            GO
            PRINT N'Creating [dbo].[ErrA]';
            GO
            CREATE TABLE [dbo].[ErrA] (Id int NOT NULL);
            GO
            IF @@ERROR <> 0 SET NOEXEC ON;
            GO
            PRINT N'Creating [dbo].[ErrB] (will fail)';
            GO
            CREATE TABLE [dbo].[ErrB] (Id int NOT NULL CONSTRAINT bad FOREIGN KEY REFERENCES [dbo].[DoesNotExist](Id));
            GO
            IF @@ERROR <> 0 SET NOEXEC ON;
            GO
            PRINT N'Creating [dbo].[ErrC] (must be skipped)';
            GO
            CREATE TABLE [dbo].[ErrC] (Id int NOT NULL);
            GO
            IF @@ERROR <> 0 SET NOEXEC ON;
            GO
            COMMIT TRANSACTION;
            GO
            IF @@ERROR <> 0 SET NOEXEC ON;
            GO
            DECLARE @Success AS BIT
            SET @Success = 1
            SET NOEXEC OFF
            IF (@Success = 1) PRINT 'The database update succeeded'
            ELSE BEGIN
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION
                PRINT 'The database update failed'
            END
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(conn, script, ct, useOwnTransaction: false);
        result.Success.Should().BeFalse("the failing FK step must surface as a failure");
        (await ObjectExistsAsync(conn, "dbo.ErrA", ct)).Should().BeFalse("rolled back");
        (await ObjectExistsAsync(conn, "dbo.ErrC", ct)).Should().BeFalse(
            "never sent at all: the executor stops at the batch that throws, so the "
            + "NOEXEC gate this script writes never gets to run");
    }

    [Fact]
    public async Task Clean_script_commits_and_objects_persist()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DeployOk", ct);
        const string script = """
            SET XACT_ABORT ON;
            GO
            BEGIN TRANSACTION;
            GO
            PRINT N'Creating [dbo].[OkA]';
            GO
            CREATE TABLE [dbo].[OkA] (Id int NOT NULL);
            GO
            IF @@ERROR <> 0 SET NOEXEC ON;
            GO
            COMMIT TRANSACTION;
            GO
            DECLARE @Success AS BIT
            SET @Success = 1
            SET NOEXEC OFF
            IF (@Success = 1) PRINT 'The database update succeeded'
            ELSE BEGIN
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION
                PRINT 'The database update failed'
            END
            GO
            """;
        SqlBatchResult result = await SqlExecutor.ExecuteAsync(conn, script, ct, useOwnTransaction: false);
        result.Success.Should().BeTrue(result.ErrorMessage);
        (await ObjectExistsAsync(conn, "dbo.OkA", ct)).Should().BeTrue("committed");
    }

    /// <summary>
    /// The two channels, from a real server, on the shape of script the app
    /// actually deploys. Errors arrive on a SqlException and every PRINT arrives
    /// on InfoMessage, which throws nothing at all — keeping only
    /// <c>ex.Message</c> is why the app could show two lines where SSMS shows a
    /// hundred, and why an error could not be attributed to the object the
    /// script was working on.
    /// </summary>
    [Fact]
    public async Task A_failed_deploy_keeps_every_error_and_every_print()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DeployMsgs", ct);

        const string script = """
            SET XACT_ABORT ON;
            GO
            PRINT N'Creating [dbo].[MsgA]';
            GO
            CREATE TABLE [dbo].[MsgA] (Id int NOT NULL);
            GO
            PRINT N'Altering [dbo].[MsgA] (will fail)';
            GO
            ALTER TABLE [dbo].[MsgA] ADD CONSTRAINT [CK_MsgA] CHECK ([NoSuchColumn] > 0);
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(conn, script, ct, useOwnTransaction: false);

        result.Success.Should().BeFalse();
        result.Messages.Select(m => m.Text).Should().Contain(
            t => t.Contains("Creating [dbo].[MsgA]", StringComparison.Ordinal),
            "the PRINT that names the object is what attributes the error that follows");
        result.Messages.Select(m => m.Text).Should().Contain(
            t => t.Contains("Altering [dbo].[MsgA]", StringComparison.Ordinal));
        result.Errors.Should().NotBeEmpty("the server raised at least one");
        result.Errors.Should().AllSatisfy(e =>
        {
            e.Number.Should().BeGreaterThan(0);
            e.Severity.Should().BeGreaterThan(10);
            e.Header.Should().StartWith("Msg ");
        });
    }

    /// <summary>
    /// A clean run keeps its PRINTs too: they are the record of what ran, and
    /// the only one the app has once the dialog is closed.
    /// </summary>
    [Fact]
    public async Task A_clean_deploy_keeps_its_prints_and_raises_no_errors()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DeployMsgsOk", ct);

        const string script = """
            PRINT N'Creating [dbo].[MsgOk]';
            GO
            CREATE TABLE [dbo].[MsgOk] (Id int NOT NULL);
            GO
            """;

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(conn, script, ct, useOwnTransaction: false);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Errors.Should().BeEmpty();
        result.Messages.Should().ContainSingle()
            .Which.Text.Should().Contain("Creating [dbo].[MsgOk]");
    }

    /// <summary>
    /// The mode every generated script deploys in, and the one nothing asserted
    /// before: the script carries its own transaction, so
    /// <c>useOwnTransaction</c> is false and <see cref="SqlExecutor"/> owns no
    /// transaction to roll back. <c>RolledBack</c> there reports whether the
    /// transaction was still open when the executor asked, and that is only
    /// legible next to the target's real state — so both rows assert the pair,
    /// on a script built by the real <see cref="DeploymentScriptWriter"/> rather
    /// than a hand-typed lookalike that could drift from what the app ships.
    /// </summary>
    /// <remarks>
    /// The two rows are deliberately the SAME SHAPE — one object created, then
    /// one statement that fails — so the only thing that varies is how hard the
    /// server failed. Dropping an object that is not there raises Msg 3701 at
    /// severity 11, which does not abort the transaction: it is still open, the
    /// rollback really is issued, and <c>true</c> is accurate. Creating an object
    /// that already exists raises Msg 2714 at severity 16, which does abort it:
    /// nothing is left to roll back and <c>false</c> is the deliberate under-claim
    /// of a flag documented as "known to be unchanged". The target is intact in
    /// both, which is why asserting <c>RolledBack</c> on its own would pin half a
    /// contract — and why the label PRINT is asserted too, since a clean target
    /// proves nothing unless the earlier batch actually ran.
    /// <para>
    /// Measured on <c>mssql/server:2022-latest</c> 16.0.4265.3 before it was
    /// written. The backlog entry that opened this said <c>RolledBack</c> is
    /// "always false" in this mode; it is not. Two things the split is NOT, also
    /// measured: the message number — Msg 3701 is severity 11 for "the object is
    /// not there" and severity 14 for "you do not have permission", and only the
    /// second aborts — and <c>XACT_ABORT</c>, since every case came out identical
    /// with the flag ON and with it OFF. The <c>IF @@ERROR</c> gate plays no part
    /// either: SqlClient throws on the first error of severity 11 or higher and
    /// the executor stops at that batch, so neither the gate nor the closing
    /// verdict ever runs.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Sev11", "DROP TABLE [dbo].[NotThere];", 3701, 11, true)]
    [InlineData("Sev16", "CREATE TABLE [dbo].[RbA] (Id int NOT NULL);", 2714, 16, false)]
    public async Task Script_mode_reports_rollback_by_severity_with_the_target_intact(
        string tag, string failingBody, int number, int severity, bool expectedRolledBack)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync($"DeployRb{tag}", ct);

        StringBuilder sb = new();
        DeploymentScriptWriter writer = new(sb, useTransaction: true);
        writer.WritePreamble(includeHeader: false);
        writer.WriteBatch("Creating [dbo].[RbA]", "CREATE TABLE [dbo].[RbA] (Id int NOT NULL);");
        writer.WriteBatch("The step that fails", failingBody);
        writer.WriteVerdict();
        string script = sb.ToString();

        SqlExecutor.ScriptManagesItsOwnTransaction(script).Should().BeTrue(
            "apply reads the marker to decide, and this is the shape it decides on");

        SqlBatchResult result = await SqlExecutor.ExecuteAsync(
            conn, script, ct, useOwnTransaction: false);

        result.Success.Should().BeFalse();
        result.Messages.Select(m => m.Text).Should().Contain(
            t => t.Contains("Creating [dbo].[RbA]", StringComparison.Ordinal),
            "the earlier batch has to have run, or a clean target would prove nothing");
        result.Errors.Should().Contain(
            e => e.Number == number && e.Severity == severity,
            "this row is about that failure and no other");
        (await ObjectExistsAsync(conn, "dbo.RbA", ct)).Should().BeFalse(
            "the script's COMMIT was never reached, so the earlier batch does not survive");
        result.RolledBack.Should().Be(
            expectedRolledBack,
            "the field says whether a rollback was ISSUED, not whether the target is clean");
    }

    private async Task<string> FreshDbAsync(string db, CancellationToken ct)
    {
        await using (SqlConnection b = new(fixture.ConnectionString))
        {
            await b.OpenAsync(ct);
            await Exec(b, $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", ct);
        }
        return new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = db }.ConnectionString;
    }
    private static async Task<bool> ObjectExistsAsync(string conn, string name, CancellationToken ct)
    {
        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new($"SELECT OBJECT_ID(N'{name}')", c);
        object? r = await cmd.ExecuteScalarAsync(ct);
        return r is not null && r != DBNull.Value;
    }
    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
