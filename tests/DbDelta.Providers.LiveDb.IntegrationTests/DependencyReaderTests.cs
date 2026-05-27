using DbDelta.Core.Abstractions;
using DbDelta.Core.Dependency;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class DependencyReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task Loads_view_on_view_and_computed_column_on_function_edges()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection b = new(fixture.ConnectionString))
        {
            await b.OpenAsync(ct);
            await Exec(b, "IF DB_ID('DepEdges') IS NULL CREATE DATABASE DepEdges;", ct);
        }
        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DepEdges",
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await Exec(c, "CREATE OR ALTER FUNCTION dbo.fnTax(@x money) RETURNS money AS BEGIN RETURN @x * 0.2 END", ct);
            await Exec(c, """
                IF OBJECT_ID('dbo.Sale', 'U') IS NULL
                    EXEC('
                        CREATE TABLE dbo.Sale (
                            Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            Net money NOT NULL,
                            Tax AS (dbo.fnTax(Net))
                        );');
                """, ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vBase AS SELECT Id, Net FROM dbo.Sale;", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vTop AS SELECT Id FROM dbo.vBase;", ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        IReadOnlyList<DependencyEdge> edges = result.Value!.Dependencies;

        edges.Should().Contain(e =>
            e.Dependent.ObjectName == "vTop" && e.Referenced.ObjectName == "vBase");
        edges.Should().Contain(e =>
            e.Dependent.ObjectName == "Sale" && e.Referenced.ObjectName == "fnTax");
    }

    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
