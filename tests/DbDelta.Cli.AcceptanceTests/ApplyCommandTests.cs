using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Cli.AcceptanceTests;

[Collection(nameof(CliCollection))]
public class ApplyCommandTests(CliFixture fixture)
{
    [Fact]
    public async Task DryRun_reports_batch_count_without_touching_target()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string tgtDb = "DbDeltaApplyDryTgt";
        await CreateDb(tgtDb, ct);

        using TempFile sqlScript = TempFile.Sql();
        await File.WriteAllTextAsync(sqlScript.Path,
            "CREATE TABLE dbo.Probe (Id int NOT NULL);\nGO\n", ct);

        int exit = await RunCli(["apply",
            "--target", ConnectionFor(tgtDb),
            "--script", sqlScript.Path,
            "--dry-run"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        (await ObjectExistsAsync(tgtDb, "dbo.Probe", ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Applies_script_inside_a_transaction_and_target_picks_up_the_change()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string tgtDb = "DbDeltaApplyRealTgt";
        await CreateDb(tgtDb, ct);

        using TempFile sqlScript = TempFile.Sql();
        await File.WriteAllTextAsync(sqlScript.Path,
            "CREATE TABLE dbo.AppliedByCli (Id int NOT NULL);\nGO\n", ct);

        int exit = await RunCli(["apply",
            "--target", ConnectionFor(tgtDb),
            "--script", sqlScript.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        (await ObjectExistsAsync(tgtDb, "dbo.AppliedByCli", ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Returns_project_file_error_when_script_file_does_not_exist()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string tgtDb = "DbDeltaApplyMissingTgt";
        await CreateDb(tgtDb, ct);

        int exit = await RunCli(["apply",
            "--target", ConnectionFor(tgtDb),
            "--script", Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.sql")], ct);

        exit.Should().Be(ExpectedExitCodes.ProjectFileError);
    }

    private string ConnectionFor(string db) => CliRunner.ConnectionFor(fixture.ConnectionString, db);
    private Task CreateDb(string db, CancellationToken ct) => CliRunner.CreateDb(fixture.ConnectionString, db, ct);
    private static Task<int> RunCli(string[] args, CancellationToken ct) => CliRunner.Run(args, ct);

    private async Task<bool> ObjectExistsAsync(string db, string objectName, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new($"SELECT OBJECT_ID('{objectName}')", c);
        object? id = await cmd.ExecuteScalarAsync(ct);
        return id is not (null or DBNull);
    }
}
