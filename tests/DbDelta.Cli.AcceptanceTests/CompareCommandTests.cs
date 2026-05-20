using System.Diagnostics;
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

    private string ConnectionFor(string db) =>
        new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = db }.ConnectionString;

    private async Task CreateDb(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(fixture.ConnectionString);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new($"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task CreateCustomerTable(string db, CancellationToken ct)
    {
        await using SqlConnection c = new(ConnectionFor(db));
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            "IF OBJECT_ID('dbo.Customer') IS NULL CREATE TABLE dbo.Customer(Id int);", c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> RunCli(string[] args, CancellationToken ct)
    {
        string exe = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "DbDelta.Cli", "bin", "Debug", "net10.0", "dbdelta.exe"));

        ProcessStartInfo psi = new(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
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
}
