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

        // 1, not 0. This assertion used to say SuccessNoDifferences while the
        // script it checks below creates a table — the verb returned 0 whatever
        // it found, so a pipeline gating on the exit code never saw pending
        // work. compare and report have always distinguished the two.
        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
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

    /// <summary>
    /// The bound-type refusal, through the process boundary: an alias type whose
    /// base changed, with a table column on both sides still declared with it.
    /// </summary>
    /// <remarks>
    /// The table compares Identical, so it is filtered out before generation and
    /// nothing drops it; and no ordering would help anyway, because the type's
    /// DROP and CREATE are one body at a slot that precedes every kind that can
    /// bind it. Without the guard the CLI writes a script that dies on the
    /// operator's server with Msg 3732.
    /// </remarks>
    [Fact]
    public async Task Refuses_with_exit_30_when_something_still_uses_a_type_being_rebuilt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptTypeSrc";
        const string tgtDb = "DbDeltaScriptTypeTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        foreach ((string db, string baseType) in new[] { (srcDb, "bigint"), (tgtDb, "int") })
        {
            await Exec(db, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);
            await Exec(db, $"IF TYPE_ID('app.Codice') IS NULL CREATE TYPE app.Codice FROM {baseType};", ct);
            await Exec(db, "IF OBJECT_ID('dbo.Ordini') IS NULL CREATE TABLE dbo.Ordini (Id int NOT NULL, C app.Codice NOT NULL);", ct);
        }

        using var sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.ScriptGenerationFailure);
        File.Exists(sqlOut.Path).Should().BeFalse(
            "the refusal happens during generation, so there is no script to write");
    }

    /// <summary>
    /// The schemabound refusal, through the process boundary. The IDENTITY flip
    /// forces the rebuild; the SCHEMABINDING view on the TARGET is what the
    /// DROP TABLE would run into.
    /// </summary>
    /// <remarks>
    /// Exit 30 and no file written is the whole contract. Without the guard the
    /// CLI writes a script that looks fine, and it dies on the operator's server
    /// with Msg 3729 naming a view they never chose to touch — then XACT_ABORT
    /// rolls the entire deploy back. This is also the only test that proves the
    /// CLI passes the TARGET's edges: the guard reads dropDependencies, and with
    /// the source's it would never fire.
    /// </remarks>
    [Fact]
    public async Task Refuses_with_exit_30_when_a_schemabound_module_blocks_a_rebuild()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptSbSrc";
        const string tgtDb = "DbDeltaScriptSbTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await Exec(srcDb, """
            IF OBJECT_ID('dbo.Ordini') IS NULL
                CREATE TABLE dbo.Ordini (Id bigint IDENTITY(1,1) NOT NULL, Amt decimal(18,2) NOT NULL);
            """, ct);
        await Exec(srcDb, "CREATE OR ALTER VIEW dbo.vOrdiniSb WITH SCHEMABINDING AS SELECT Id, Amt FROM dbo.Ordini;", ct);
        await Exec(tgtDb, """
            IF OBJECT_ID('dbo.Ordini') IS NULL
                CREATE TABLE dbo.Ordini (Id bigint NOT NULL, Amt decimal(18,2) NOT NULL);
            """, ct);
        await Exec(tgtDb, "CREATE OR ALTER VIEW dbo.vOrdiniSb WITH SCHEMABINDING AS SELECT Id, Amt FROM dbo.Ordini;", ct);

        using var sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.ScriptGenerationFailure);
        File.Exists(sqlOut.Path).Should().BeFalse(
            "the refusal happens during generation, so there is no script to write");
    }

    /// <summary>
    /// The fourth refusal, through the same boundary. Source has a
    /// memory-optimized table type the target lacks; DbDelta writes no
    /// MEMORY_OPTIMIZED clause, so without the refusal it emits a plain
    /// CREATE TYPE that RUNS and leaves a disk-based type of the same name.
    /// </summary>
    /// <remarks>
    /// This one has to be asserted here and not only in Core: the CLI dispatches
    /// on the concrete exception type — three sibling catch blocks, no shared
    /// base — so a fourth exception with no catch of its own falls through to
    /// the general handler and exits 99 with "open an issue". Nothing in the
    /// Core suite can see that.
    /// </remarks>
    [Fact]
    public async Task Refuses_with_exit_30_when_the_source_has_a_memory_optimized_table_type()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptMoSrc";
        const string tgtDb = "DbDeltaScriptMoTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);

        // The stock image ships no MEMORY_OPTIMIZED_DATA filegroup, and without
        // one the CREATE TYPE fails with Msg 41337 rather than producing the
        // shape under test. sys.filegroups is per-database, so the check runs
        // against srcDb itself.
        await Exec(srcDb, """
            IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE type = 'FX')
            BEGIN
                EXEC sp_executesql N'
                    ALTER DATABASE CURRENT ADD FILEGROUP [MemOptFg] CONTAINS MEMORY_OPTIMIZED_DATA;';
                EXEC sp_executesql N'
                    ALTER DATABASE CURRENT ADD FILE (NAME = N''DbDeltaScriptMoSrc_mod'',
                        FILENAME = N''/var/opt/mssql/data/DbDeltaScriptMoSrc_mod'') TO FILEGROUP [MemOptFg];';
            END
            """, ct);
        await Exec(srcDb, """
            IF TYPE_ID('dbo.MemOptTvp') IS NULL
                CREATE TYPE dbo.MemOptTvp AS TABLE (
                    Id   int NOT NULL,
                    Code nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    PRIMARY KEY NONCLUSTERED HASH (Id) WITH (BUCKET_COUNT = 8)
                ) WITH (MEMORY_OPTIMIZED = ON);
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

    /// <summary>
    /// A CHECK constraint that calls a function reading the same table is a
    /// legal schema, and it is a dependency CYCLE for anything that writes the
    /// constraint inside CREATE TABLE — which DbDelta does. Exit 31, the code
    /// §4.3 already reserves for it, never 99 with "open an issue".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on mssql/server:2022-latest: the table, the function and the
    /// constraint all create, and the table then accepts rows. DbDelta's own
    /// reader query returns the two edges that close the loop —
    /// <c>fnRowCount [FN] -&gt; Righe [U]</c> and <c>Righe [U] -&gt;
    /// fnRowCount [FN]</c>, the second one because a CHECK's references are
    /// attributed to its parent table.
    /// </para>
    /// <para>
    /// It has to be asserted here and not only in Core, for the same reason the
    /// fourth refusal does: the CLI dispatches on the concrete exception type,
    /// so an exception with no catch of its own falls through to the general
    /// handler and exits 99. Nothing in the Core suite can see that.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Refuses_with_exit_31_when_a_CHECK_calls_a_function_that_reads_its_own_table()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptCycSrc";
        const string tgtDb = "DbDeltaScriptCycTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);

        await Exec(srcDb, "IF OBJECT_ID('dbo.Righe','U') IS NULL CREATE TABLE dbo.Righe (Id int NOT NULL, Qta int NOT NULL);", ct);
        await Exec(srcDb, """
            IF OBJECT_ID('dbo.fnRowCount','FN') IS NULL
                EXEC sp_executesql N'CREATE FUNCTION dbo.fnRowCount() RETURNS int AS
                BEGIN RETURN (SELECT COUNT(*) FROM dbo.Righe); END;';
            """, ct);
        await Exec(srcDb, """
            IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Righe_Max')
                ALTER TABLE dbo.Righe ADD CONSTRAINT CK_Righe_Max CHECK (dbo.fnRowCount() < 100);
            """, ct);

        // The mechanism before the verdict: this schema is legal and holds data.
        await Exec(srcDb, "INSERT dbo.Righe (Id, Qta) VALUES (1, 10);", ct);

        using var sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path], ct);

        exit.Should().Be(ExpectedExitCodes.UnresolvableDependencyCycle);
        File.Exists(sqlOut.Path).Should().BeFalse(
            "the refusal happens during generation, so there is no script to write");
    }

    /// <summary>
    /// The other half of the no-transaction contract, through the process
    /// boundary: <c>--no-transaction</c> on <c>script</c> produces the marker
    /// that <c>apply</c> already knows how to read.
    /// </summary>
    /// <remarks>
    /// <c>ComparisonOptions.NoTransactions</c> was honoured end to end from
    /// <c>ScriptGenerator</c> to <c>apply</c>, but no front end could ask
    /// for it: every call site passed <c>ComparisonOptions.Default</c>, so the
    /// only way to get a <c>=none</c> script out of the published binaries was
    /// to type the line by hand. This asserts the option reaches the flag —
    /// a Core unit test cannot, because the CLI is where the gap was.
    /// </remarks>
    [Fact]
    public async Task Writes_a_script_that_declares_no_transaction_when_the_flag_is_passed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaScriptNoTxSrc";
        const string tgtDb = "DbDeltaScriptNoTxTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerTable(srcDb, ct);

        using var sqlOut = TempFile.Sql();
        int exit = await RunCli(["script",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--out", sqlOut.Path,
            "--no-transaction"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
        string content = await File.ReadAllTextAsync(sqlOut.Path, ct);

        // StartWith, not Contain: apply reads the FIRST line, on purpose, so a
        // marker anywhere else would not be honoured and this test would be
        // asserting something no one can use.
        content.Should().StartWith("-- dbdelta:transaction=none");
        content.Should().NotContain("BEGIN TRANSACTION",
            "the marker has to describe what the script actually does");
        content.Should().Contain("CREATE TABLE [dbo].[Customer]",
            "and it still has to be a deploy script");
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
