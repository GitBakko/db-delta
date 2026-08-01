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
        (await ObjectExistsAsync(conn, "dbo.ErrC", ct)).Should().BeFalse("skipped under NOEXEC");
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
