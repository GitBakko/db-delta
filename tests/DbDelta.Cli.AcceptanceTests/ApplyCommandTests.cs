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

        using var sqlScript = TempFile.Sql();
        await File.WriteAllTextAsync(sqlScript.Path,
            "CREATE TABLE dbo.Probe (Id int NOT NULL);\nGO\n", ct);

        int exit = await RunCli(["apply",
            "--target", ConnectionFor(tgtDb),
            "--script", sqlScript.Path,
            "--dry-run"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        (await ObjectExistsAsync(tgtDb, "dbo.Probe", ct)).Should().BeFalse();
    }

    // Renamed: this only ever proved the change lands. It asserted no atomicity
    // whatsoever — one batch, no failure — while its old name
    // ("Applies_script_inside_a_transaction_and_...") claimed it did. The real
    // atomicity check is the test below it.
    [Fact]
    public async Task Applies_script_and_target_picks_up_the_change()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string tgtDb = "DbDeltaApplyRealTgt";
        await CreateDb(tgtDb, ct);

        using var sqlScript = TempFile.Sql();
        await File.WriteAllTextAsync(sqlScript.Path,
            "CREATE TABLE dbo.AppliedByCli (Id int NOT NULL);\nGO\n", ct);

        int exit = await RunCli(["apply",
            "--target", ConnectionFor(tgtDb),
            "--script", sqlScript.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        (await ObjectExistsAsync(tgtDb, "dbo.AppliedByCli", ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_batch_rolls_back_the_earlier_batches_of_a_plain_script()
    {
        // The only genuine half-migration hole in the product: `apply` passed
        // useOwnTransaction:false unconditionally, so a script without its own
        // BEGIN TRANSACTION envelope — hand-written, from another tool, or a
        // generated one whose envelope was edited out — got no transaction at
        // all. Batch 1 committed, batch 2 failed, and the database was left
        // half-migrated. This test fails on that behaviour.
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string tgtDb = "DbDeltaApplyAtomicTgt";
        await CreateDb(tgtDb, ct);

        using var sqlScript = TempFile.Sql();
        await File.WriteAllTextAsync(sqlScript.Path, """
            CREATE TABLE dbo.FirstBatch (Id int NOT NULL);
            GO
            CREATE TABLE dbo.SecondBatch (Id int NOT NULL, Oops int NOT NULL REFERENCES dbo.NoSuchTable(Id));
            GO
            CREATE TABLE dbo.ThirdBatch (Id int NOT NULL);
            GO
            """, ct);

        int exit = await RunCli(["apply",
            "--target", ConnectionFor(tgtDb),
            "--script", sqlScript.Path], ct);

        exit.Should().Be(ExpectedExitCodes.DeploymentFailure);
        (await ObjectExistsAsync(tgtDb, "dbo.FirstBatch", ct)).Should()
            .BeFalse("batch 1 must be rolled back when batch 2 fails");
        (await ObjectExistsAsync(tgtDb, "dbo.ThirdBatch", ct)).Should().BeFalse();
    }

    [Fact]
    public async Task No_transaction_opt_out_leaves_the_earlier_batches_applied()
    {
        // The escape hatch for scripts that cannot run inside a transaction.
        // Asserting it explicitly so the default above cannot silently become
        // the only behaviour.
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string tgtDb = "DbDeltaApplyNoTxTgt";
        await CreateDb(tgtDb, ct);

        using var sqlScript = TempFile.Sql();
        await File.WriteAllTextAsync(sqlScript.Path, """
            CREATE TABLE dbo.KeptBatch (Id int NOT NULL);
            GO
            CREATE TABLE dbo.BadBatch (Id int NOT NULL, Oops int NOT NULL REFERENCES dbo.NoSuchTable(Id));
            GO
            """, ct);

        int exit = await RunCli(["apply",
            "--target", ConnectionFor(tgtDb),
            "--script", sqlScript.Path,
            "--no-transaction"], ct);

        exit.Should().Be(ExpectedExitCodes.DeploymentFailure);
        (await ObjectExistsAsync(tgtDb, "dbo.KeptBatch", ct)).Should()
            .BeTrue("--no-transaction means exactly that");
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
