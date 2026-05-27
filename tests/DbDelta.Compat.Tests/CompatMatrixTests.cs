using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using DbDelta.Providers.LiveDb;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace DbDelta.Compat.Tests;

/// <summary>
/// Nightly compatibility matrix (spec §4 Quality Bar — "<c>Compat.Matrix</c>:
/// parametrized image versions, ~30 min"). For each supported SQL Server engine
/// the full read → diff → script → apply pipeline must round-trip: seed a known
/// schema on a source DB, deploy it onto an empty target via a generated script,
/// then re-compare and assert the seeded object kinds converged.
/// </summary>
/// <remarks>
/// SQL Server 2016 is a documented minimum-compat target but has <b>no Linux
/// container image</b> — SQL-Server-on-Linux began with 2017, and Testcontainers
/// runs Linux containers. 2016 is therefore exercised only against live Windows
/// instances, not in this matrix.
///
/// Gated off by default: each test self-skips unless <c>DBDELTA_COMPAT=1</c> is
/// set (nightly CI) and a Docker daemon is reachable. Pulling 2017/2019 images
/// makes this suite far too heavy for the per-PR run.
/// </remarks>
[Trait("Category", "Compat")]
public sealed class CompatMatrixTests
{
    private const string SaPassword = "Y0urStrong!Pass";

    /// <summary>Real Linux container engines covered by the nightly matrix.</summary>
    public static TheoryData<string> ServerImages() =>
    [
        "mcr.microsoft.com/mssql/server:2017-latest",
        "mcr.microsoft.com/mssql/server:2019-latest",
        "mcr.microsoft.com/mssql/server:2022-latest",
    ];

    [Theory]
    [MemberData(nameof(ServerImages))]
    public async Task Read_diff_apply_roundtrip_converges(string image)
    {
        await SkipUnlessEnabledAsync();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using MsSqlContainer container = new MsSqlBuilder()
            .WithImage(image)
            .WithPassword(SaPassword)
            .Build();
        await container.StartAsync(ct);

        string baseConn = container.GetConnectionString() + ";TrustServerCertificate=True;";
        await ExecAsync(baseConn, "IF DB_ID('CompatSource') IS NULL CREATE DATABASE CompatSource;", ct);
        await ExecAsync(baseConn, "IF DB_ID('CompatTarget') IS NULL CREATE DATABASE CompatTarget;", ct);

        string srcConn = WithCatalog(baseConn, "CompatSource");
        string tgtConn = WithCatalog(baseConn, "CompatTarget");

        await SeedSourceSchemaAsync(srcConn, ct);

        // Load both sides through the live catalog readers.
        Database source = await LoadAsync(srcConn, ct);
        Database target = await LoadAsync(tgtConn, ct);

        // Source carries objects the empty target lacks → drift must be detected.
        ComparisonEngine engine = new();
        ComparisonResult initial = engine.Compare(source, target, ComparisonOptions.Default);
        SeededDrift(initial).Should().NotBeEmpty(
            "the seeded schema should diff against an empty target on {0}", image);

        // Generate the deploy script (make target match source) and apply it.
        string script = new ScriptGenerator().Generate(initial);
        script.Should().NotBeNullOrWhiteSpace();

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgtConn, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "deploy script failed to apply");
        apply.BatchesExecuted.Should().BeGreaterThan(0);

        // Reload target and re-compare — the seeded object kinds must converge.
        Database targetAfter = await LoadAsync(tgtConn, ct);
        ComparisonResult after = engine.Compare(source, targetAfter, ComparisonOptions.Default);
        SeededDrift(after).Should().BeEmpty(
            "applying the generated script should converge target to source on {0}", image);
    }

    /// <summary>
    /// Differences limited to the object kinds this fixture seeds. Filtering keeps
    /// the convergence assertion immune to incidental users/roles/permissions noise
    /// that a fresh container reports identically on both sides anyway.
    /// </summary>
    private static IReadOnlyList<DifferencePair> SeededDrift(ComparisonResult result) =>
        [.. result.Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Where(d => d.Identity.Kind is "Table" or "View" or "Procedure")];

    private static async Task SeedSourceSchemaAsync(string conn, CancellationToken ct)
    {
        // Table + identity + PK.
        await ExecAsync(conn, """
            CREATE TABLE dbo.Customer (
                Id    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
                Name  nvarchar(100)     NOT NULL,
                Email nvarchar(200)         NULL
            );
            """, ct);

        // Second table + inbound FK.
        await ExecAsync(conn, """
            CREATE TABLE dbo.OrderHeader (
                Id         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderHeader PRIMARY KEY,
                CustomerId int NOT NULL
                    CONSTRAINT FK_OrderHeader_Customer REFERENCES dbo.Customer(Id),
                Total      decimal(18,2) NOT NULL
            );
            """, ct);

        // View (must be the first statement in its batch).
        await ExecAsync(conn, """
            CREATE VIEW dbo.vCustomerOrders AS
                SELECT c.Id AS CustomerId, c.Name, o.Total
                FROM dbo.Customer c
                JOIN dbo.OrderHeader o ON o.CustomerId = c.Id;
            """, ct);

        // Stored procedure (must be the first statement in its batch).
        await ExecAsync(conn, """
            CREATE PROCEDURE dbo.GetCustomer @id int AS
                SELECT Id, Name, Email FROM dbo.Customer WHERE Id = @id;
            """, ct);
    }

    private static async Task<Database> LoadAsync(string conn, CancellationToken ct)
    {
        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        return result.Value!;
    }

    private static async Task ExecAsync(string conn, string sql, CancellationToken ct)
    {
        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string WithCatalog(string conn, string database) =>
        new SqlConnectionStringBuilder(conn) { InitialCatalog = database }.ConnectionString;

    private static async Task SkipUnlessEnabledAsync()
    {
        if (Environment.GetEnvironmentVariable("DBDELTA_COMPAT") != "1")
        {
            Assert.Skip("Compat matrix is nightly-only. Set DBDELTA_COMPAT=1 to run.");
        }

        if (!await IsDockerAvailableAsync())
        {
            Assert.Skip("Docker not available — skipping compat matrix.");
        }
    }

    private static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            using System.Net.Sockets.TcpClient tc = new();
            await tc.ConnectAsync("localhost", 2375).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch
        {
            return File.Exists(@"\\.\pipe\docker_engine");
        }
    }
}
