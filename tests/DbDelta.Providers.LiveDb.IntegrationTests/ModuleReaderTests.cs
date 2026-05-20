using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class ModuleReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_views_with_their_bodies()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaModulesView";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.vCustomer') IS NULL EXEC('CREATE VIEW dbo.vCustomer AS SELECT 1 AS Id;');
            """, ct);

        LiveDbSource source = new(conn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Database db = result.Value!;
        View v = db.Views.Should().ContainSingle(x => x.Name == "vCustomer").Subject;
        v.Schema.Should().Be("dbo");
        v.IsEncrypted.Should().BeFalse();
        v.Body.Should().NotBeNullOrWhiteSpace();
        v.Body!.Should().Contain("SELECT 1 AS Id");
    }

    [Fact]
    public async Task LiveDbSource_loads_procedures_with_their_bodies()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaModulesProc";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.uspGet') IS NULL
                EXEC('CREATE PROCEDURE dbo.uspGet AS SELECT 1 AS Id;');
            """, ct);

        LiveDbSource source = new(conn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        StoredProcedure p = result.Value!.Procedures.Should()
            .ContainSingle(x => x.Name == "uspGet").Subject;
        p.IsEncrypted.Should().BeFalse();
        p.Body.Should().NotBeNullOrWhiteSpace();
        p.Body!.Should().Contain("SELECT 1 AS Id");
    }

    [Fact]
    public async Task LiveDbSource_surfaces_encrypted_modules_with_null_body()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaModulesEncrypted";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.uspSecret') IS NULL
                EXEC('CREATE PROCEDURE dbo.uspSecret WITH ENCRYPTION AS SELECT 1;');
            """, ct);

        LiveDbSource source = new(conn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        StoredProcedure p = result.Value!.Procedures.Should()
            .ContainSingle(x => x.Name == "uspSecret").Subject;
        p.IsEncrypted.Should().BeTrue();
        p.Body.Should().BeNull();
    }

    private async Task<string> CreateAndSeedAsync(string dbName, string seedSql, CancellationToken ct)
    {
        await using (SqlConnection master = new(fixture.ConnectionString))
        {
            await master.OpenAsync(ct);
            await ExecAsync(master, $"IF DB_ID('{dbName}') IS NULL CREATE DATABASE [{dbName}];", ct);
        }

        string conn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = dbName
        }.ConnectionString;

        await using (SqlConnection target = new(conn))
        {
            await target.OpenAsync(ct);
            await ExecAsync(target, seedSql, ct);
        }

        return conn;
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
