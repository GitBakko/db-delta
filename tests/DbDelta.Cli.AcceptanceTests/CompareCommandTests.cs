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
    public const int InternalError = 99;
}
