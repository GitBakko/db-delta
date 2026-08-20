using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace DbDelta.Cli.AcceptanceTests;

/// <summary>
/// CLI acceptance tests own their own SQL container — they do NOT share the
/// integration project's fixture because xUnit collections do not cross
/// assembly boundaries.
/// </summary>
public sealed class CliFixture : IAsyncLifetime, IAsyncDisposable
{
    public MsSqlContainer Container { get; } = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("Y0urStrong!Pass")
        .Build();

    public string ConnectionString => Container.GetConnectionString() + ";TrustServerCertificate=True;";

    public ValueTask InitializeAsync() => new(Container.StartAsync());

    public async ValueTask DisposeAsync() => await Container.DisposeAsync();
}

[CollectionDefinition(nameof(CliCollection))]
public sealed class CliCollection : ICollectionFixture<CliFixture> { }

[Collection(nameof(CliCollection))]
public class CompareCommandTests(CliFixture fixture)
{
    [Fact]
    public async Task Returns_exit_code_1_when_source_has_an_extra_table()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaSrc";
        const string tgtDb = "DbDeltaTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerTable(srcDb, ct);

        string srcConn = ConnectionFor(srcDb);
        string tgtConn = ConnectionFor(tgtDb);

        int exitCode = await RunCli(["compare", "--source", srcConn, "--target", tgtConn, "--format", "json"], ct);

        exitCode.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    /// <summary>
    /// The field names <c>compare --format json</c> emits, pinned.
    /// </summary>
    /// <remarks>
    /// This shape is NOT the one <c>report --json</c> writes: compare says
    /// <c>schema</c> / <c>name</c> where report says <c>schemaName</c> /
    /// <c>objectName</c>, and report carries the two modify dates as well. Two
    /// contracts is one too many, and unifying them is a breaking change to a
    /// released CLI — the owner's call, recorded in the backlog. Until then the
    /// shape people actually script against is pinned here, because only
    /// report's was covered and this one could have drifted unnoticed.
    /// </remarks>
    [Fact]
    public async Task Compare_json_keeps_its_published_field_names()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaJsonShapeSrc";
        const string tgtDb = "DbDeltaJsonShapeTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerTable(srcDb, ct);

        (int exitCode, string stdout) = await CliRunner.RunCapturing(
            ["compare", "--source", ConnectionFor(srcDb), "--target", ConnectionFor(tgtDb),
             "--format", "json"], ct);

        exitCode.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
        using var doc = JsonDocument.Parse(stdout);
        JsonElement first = doc.RootElement.GetProperty("differences")[0];
        first.GetProperty("kind").GetString().Should().NotBeNull();
        first.GetProperty("schema").GetString().Should().NotBeNull();
        first.GetProperty("name").GetString().Should().NotBeNull();
        first.GetProperty("status").GetString().Should().NotBeNull();
    }

    [Fact]
    public async Task Returns_exit_code_0_when_both_databases_are_empty()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaEmptySrc";
        const string tgtDb = "DbDeltaEmptyTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);

        string srcConn = ConnectionFor(srcDb);
        string tgtConn = ConnectionFor(tgtDb);

        int exitCode = await RunCli(["compare", "--source", srcConn, "--target", tgtConn, "--format", "text"], ct);

        exitCode.Should().Be(ExpectedExitCodes.SuccessNoDifferences);
    }

    /// <summary>
    /// A comparison the engine refuses to represent: a case-SENSITIVE source
    /// holding both <c>dbo.T</c> and <c>dbo.t</c>, against a case-insensitive
    /// target that could never hold both. <c>MapByIdentity</c> throws rather
    /// than pick one.
    /// </summary>
    /// <remarks>
    /// System.CommandLine's default exception handler used to catch that and
    /// return 1 — which §4.3 defines as "succeeded, differences found". A CI
    /// pipeline read the crash as a normal drift report and moved on to a
    /// script that was never generated. The exit code is the whole contract
    /// here, so it is the whole assertion.
    /// </remarks>
    [Fact]
    public async Task Returns_exit_code_99_when_the_comparison_cannot_be_represented()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaCsSrc";
        const string tgtDb = "DbDeltaCiTgt";

        await using (SqlConnection c = new(fixture.ConnectionString))
        {
            await c.OpenAsync(ct);
            await using SqlCommand create = new(
                $"IF DB_ID('{srcDb}') IS NULL CREATE DATABASE [{srcDb}] COLLATE Latin1_General_CS_AS;", c);
            await create.ExecuteNonQueryAsync(ct);
        }
        await CreateDb(tgtDb, ct);

        await using (SqlConnection c = new(ConnectionFor(srcDb)))
        {
            await c.OpenAsync(ct);
            // Legal only because the database collation is case-sensitive.
            await using SqlCommand tables = new(
                "IF OBJECT_ID('dbo.T') IS NULL CREATE TABLE dbo.T(Id int);"
                + "IF OBJECT_ID('dbo.t') IS NULL CREATE TABLE dbo.t(Id int);", c);
            await tables.ExecuteNonQueryAsync(ct);
        }

        int exitCode = await RunCli(
            ["compare", "--source", ConnectionFor(srcDb), "--target", ConnectionFor(tgtDb), "--format", "text"],
            ct);

        exitCode.Should().Be(
            ExpectedExitCodes.InternalError,
            "a crash must not be reported as the 'differences found' success code");
    }

    private string ConnectionFor(string db) => CliRunner.ConnectionFor(fixture.ConnectionString, db);
    private Task CreateDb(string db, CancellationToken ct) => CliRunner.CreateDb(fixture.ConnectionString, db, ct);
    private Task CreateCustomerTable(string db, CancellationToken ct) => CliRunner.CreateCustomerTable(fixture.ConnectionString, db, ct);

    [Fact]
    public async Task Returns_exit_code_1_when_target_is_missing_a_primary_key()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaPkSrc";
        const string tgtDb = "DbDeltaPkTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerWithPk(srcDb, ct);
        await CreateCustomerWithoutPk(tgtDb, ct);

        string srcConn = ConnectionFor(srcDb);
        string tgtConn = ConnectionFor(tgtDb);

        int exit = await RunCli(["compare", "--source", srcConn, "--target", tgtConn, "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    private async Task CreateCustomerWithPk(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer (
                    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
                    Name nvarchar(100) NOT NULL
                );
            """, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateCustomerWithoutPk(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer (
                    Id int IDENTITY(1,1) NOT NULL,
                    Name nvarchar(100) NOT NULL
                );
            """, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task<int> RunCli(string[] args, CancellationToken ct) => CliRunner.Run(args, ct);

    /// <summary>
    /// The census reaches the CLI's own text output, asserted across the
    /// process boundary.
    /// </summary>
    /// <remarks>
    /// <c>TextFormatter</c> is internal and the CLI keeps its internals closed
    /// on purpose, so this does not open it with InternalsVisibleTo: it runs the
    /// binary and reads stdout, which is the surface a user actually sees. An
    /// empty diff with no caveat reads as "the two databases match"; it means
    /// "no difference among the thirteen kinds DbDelta compares", and the last
    /// line on screen is the only thing that says so.
    /// </remarks>
    [Fact]
    public async Task The_text_output_declares_what_the_comparison_did_not_examine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaCensusSrc";
        const string tgtDb = "DbDeltaCensusTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateCustomerTable(srcDb, ct);
        await AddExtendedProperty(srcDb, ct);

        (int exit, string stdout) = await CliRunner.RunCapturing(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "text"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
        stdout.Should().Contain("Non esaminati").And.Contain("proprietà estese");
    }

    private async Task AddExtendedProperty(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            """
            IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE name = N'MS_Description')
                EXEC sys.sp_addextendedproperty
                    @name = N'MS_Description', @value = N'a column nobody compares',
                    @level0type = N'SCHEMA', @level0name = N'dbo',
                    @level1type = N'TABLE',  @level1name = N'Customer';
            """, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }


    [Fact]
    public async Task Returns_exit_code_1_when_source_has_an_extra_view()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaViewSrc";
        const string tgtDb = "DbDeltaViewTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateViewSrcOnly(srcDb, ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    [Fact]
    public async Task Returns_exit_code_1_when_a_procedure_body_differs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaProcSrc";
        const string tgtDb = "DbDeltaProcTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateProcWithBody(srcDb, "SELECT 1 AS Id;", ct);
        await CreateProcWithBody(tgtDb, "SELECT 2 AS Id;", ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    private async Task CreateViewSrcOnly(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            "IF OBJECT_ID('dbo.vReport') IS NULL EXEC('CREATE VIEW dbo.vReport AS SELECT 1 AS Id;');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateProcWithBody(string db, string innerSql, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            $"IF OBJECT_ID('dbo.uspGet') IS NULL EXEC('CREATE PROCEDURE dbo.uspGet AS {innerSql}');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Returns_exit_code_1_when_source_has_an_extra_function()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaFnSrc";
        const string tgtDb = "DbDeltaFnTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateScalarFunction(srcDb, ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    [Fact]
    public async Task Returns_exit_code_1_when_a_trigger_body_differs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string srcDb = "DbDeltaTrgSrc";
        const string tgtDb = "DbDeltaTrgTgt";
        await CreateDb(srcDb, ct);
        await CreateDb(tgtDb, ct);
        await CreateTriggerWithBody(srcDb, "SET NOCOUNT ON;", ct);
        await CreateTriggerWithBody(tgtDb, "DECLARE @x int = 1;", ct);

        int exit = await RunCli(["compare",
            "--source", ConnectionFor(srcDb),
            "--target", ConnectionFor(tgtDb),
            "--format", "json"], ct);

        exit.Should().Be(ExpectedExitCodes.SuccessDifferencesFound);
    }

    private async Task CreateScalarFunction(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            "IF OBJECT_ID('dbo.fnSum') IS NULL EXEC('CREATE FUNCTION dbo.fnSum() RETURNS int AS BEGIN RETURN 1; END');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateTriggerWithBody(string db, string innerSql, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand setup = new(
            "IF OBJECT_ID('dbo.Customer') IS NULL CREATE TABLE dbo.Customer(Id int NOT NULL);", c);
        await setup.ExecuteNonQueryAsync(ct);
        await using SqlCommand cmd = new(
            $"IF OBJECT_ID('dbo.trgCustomer') IS NULL EXEC('CREATE TRIGGER dbo.trgCustomer ON dbo.Customer AFTER INSERT AS BEGIN {innerSql} END');", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Mirror of <c>DbDelta.Cli.ExitCodes</c> — the CLI exit codes are internal so we
/// duplicate the relevant constants here rather than expose them via InternalsVisibleTo.
/// </summary>
internal static class ExpectedExitCodes
{
    public const int SuccessNoDifferences = 0;
    public const int SuccessDifferencesFound = 1;
    public const int ScriptGenerationFailure = 30;
    public const int DeploymentFailure = 40;
    public const int ProjectFileError = 60;
    public const int InternalError = 99;
}
