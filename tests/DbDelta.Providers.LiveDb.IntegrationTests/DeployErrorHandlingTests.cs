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
