using FluentAssertions;
using Xunit;

namespace DbDelta.Cli.AcceptanceTests;

[Collection(nameof(CliCollection))]
public class ScriptCommandTests(CliFixture fixture)
{
    [Fact]
    public async Task Writes_script_file_containing_CREATE_TABLE_when_source_has_extra_table()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptOutSrc";
        const string tgtDb = "DbDeltaScriptOutTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerTable(srcDb, ct);

        using TempFile sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        File.Exists(sqlOut.Path).Should().BeTrue();
        string content = await File.ReadAllTextAsync(sqlOut.Path, ct);
        content.Should().Contain("CREATE TABLE [dbo].[Customer]");
        content.Should().Contain("BEGIN TRANSACTION");
        content.Should().Contain("COMMIT TRANSACTION");
    }

    [Fact]
    public async Task Returns_zero_and_empty_script_when_databases_are_identical()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptEqSrc";
        const string tgtDb = "DbDeltaScriptEqTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);

        using TempFile sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        string content = await File.ReadAllTextAsync(sqlOut.Path, ct);
        content.Should().Contain("BEGIN TRANSACTION");
        content.Should().NotContain("CREATE TABLE");
    }

    private string ConnectionFor(string db) => CliRunner.ConnectionFor(fixture.ConnectionString, db);
    private Task CreateDb(string db, CancellationToken ct) => CliRunner.CreateDb(fixture.ConnectionString, db, ct);
    private Task CreateCustomerTable(string db, CancellationToken ct) => CliRunner.CreateCustomerTable(fixture.ConnectionString, db, ct);
    private static Task<int> RunCli(string[] args, CancellationToken ct) => CliRunner.Run(args, ct);
}
