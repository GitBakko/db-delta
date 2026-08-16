using FluentAssertions;
using Microsoft.Data.SqlClient;
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

        using var sqlOut = TempFile.Sql();
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

        using var sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
        string content = await File.ReadAllTextAsync(sqlOut.Path, ct);
        content.Should().Contain("BEGIN TRANSACTION");
        content.Should().NotContain("CREATE TABLE");
    }

    /// <summary>
    /// The refusal, end to end and through the process boundary. Source and
    /// target differ by an IDENTITY flag, which forces the temp-table rebuild:
    /// CREATE <c>_tmp</c>, copy, DROP the original — and the columnstore both
    /// sides carry goes with the DROP, with no CREATE able to bring it back.
    /// </summary>
    /// <remarks>
    /// Exit 30 is the point. The pre-existing behaviour for anything a verb
    /// throws is exit 99 with "this is unexpected — open an issue", which for a
    /// deliberate refusal is both wrong and unactionable; and a pipeline gating
    /// on 0/1 would have taken the throw for ordinary drift.
    /// </remarks>
    [Fact]
    public async Task Refuses_with_exit_30_when_a_rebuild_would_drop_a_columnstore_index()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptCsSrc";
        const string tgtDb = "DbDeltaScriptCsTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await Exec(srcDb, """
            IF OBJECT_ID('dbo.Fatti') IS NULL
            BEGIN
                CREATE TABLE dbo.Fatti (
                    Id      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Fatti PRIMARY KEY,
                    Importo decimal(18,2) NOT NULL
                );
                CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Fatti ON dbo.Fatti (Importo);
            END
            """, ct);
        await Exec(tgtDb, """
            IF OBJECT_ID('dbo.Fatti') IS NULL
            BEGIN
                CREATE TABLE dbo.Fatti (
                    Id      int NOT NULL CONSTRAINT PK_Fatti PRIMARY KEY,
                    Importo decimal(18,2) NOT NULL
                );
                CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Fatti ON dbo.Fatti (Importo);
            END
            """, ct);

        using var sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.ScriptGenerationFailure);
        File.Exists(sqlOut.Path).Should().BeFalse(
            "the refusal happens during generation, so there is no script to write");
    }

    private async Task Exec(string db, string sql, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string ConnectionFor(string db) => CliRunner.ConnectionFor(fixture.ConnectionString, db);
    private Task CreateDb(string db, CancellationToken ct) => CliRunner.CreateDb(fixture.ConnectionString, db, ct);
    private Task CreateCustomerTable(string db, CancellationToken ct) => CliRunner.CreateCustomerTable(fixture.ConnectionString, db, ct);
    private static Task<int> RunCli(string[] args, CancellationToken ct) => CliRunner.Run(args, ct);
}
