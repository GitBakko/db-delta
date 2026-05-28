using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class DependencyRoundTripTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task Cross_kind_dependencies_apply_clean_on_empty_target()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "DepSrc", ct);
        await Create(fixture.ConnectionString, "DepTgt", ct);
        string src = Cat(fixture.ConnectionString, "DepSrc");
        string tgt = Cat(fixture.ConnectionString, "DepTgt");

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await Exec(c, "CREATE OR ALTER FUNCTION dbo.fnTax(@x money) RETURNS money AS BEGIN RETURN @x*0.2 END", ct);
            await Exec(c, "IF OBJECT_ID('dbo.Sale','U') IS NULL EXEC('CREATE TABLE dbo.Sale (Id int IDENTITY PRIMARY KEY, Net money NOT NULL, Tax AS (dbo.fnTax(Net)));')", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vBase AS SELECT Id, Net FROM dbo.Sale;", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vTop AS SELECT Id FROM dbo.vBase;", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default,
            dependencies: source.Dependencies);

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "ordered script failed to apply");

        Database after = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        ComparisonResult re = new ComparisonEngine().Compare(source, after, ComparisonOptions.Default);
        re.Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Where(d => d.Identity.Kind is "Table" or "View" or "Function")
            .Should().BeEmpty();
    }

    private static async Task Create(string conn, string db, CancellationToken ct)
    {
        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await Exec(c, $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", ct);
    }
    private static string Cat(string conn, string db) =>
        new SqlConnectionStringBuilder(conn) { InitialCatalog = db }.ConnectionString;
    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
