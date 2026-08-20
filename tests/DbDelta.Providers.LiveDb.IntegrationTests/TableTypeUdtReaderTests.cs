using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class TableTypeUdtReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_table_type_udts_with_their_columns()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaTvpTest') IS NULL CREATE DATABASE DbDeltaTvpTest;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaTvpTest"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF TYPE_ID('dbo.OrderItemTvp') IS NULL
                    CREATE TYPE dbo.OrderItemTvp AS TABLE (
                        ProductId int NOT NULL,
                        Quantity  int NOT NULL,
                        Notes     nvarchar(100) NULL
                    );
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        TableTypeUdt tt = result.Value!.TableTypeUdts.Single(t => t.Name == "OrderItemTvp");
        tt.Schema.Should().Be("dbo");
        tt.Columns.Should().HaveCount(3);
        tt.Columns.Select(c => c.Name).Should().Equal("ProductId", "Quantity", "Notes");
        tt.Columns[0].DataType.Should().Be("int");
        tt.Columns[2].IsNullable.Should().BeTrue();
    }

    /// <summary>
    /// The diff viewer's pane for a table type and for a schema. Its switch had
    /// no case for either kind, so selecting one opened an empty pane — the
    /// round-16 bug seen from the other end, where a row the grid describes
    /// shows nothing to read.
    /// </summary>
    [Fact]
    public async Task The_diff_viewer_has_a_body_for_a_table_type_and_for_a_schema()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaTvpBody') IS NULL CREATE DATABASE DbDeltaTvpBody;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaTvpBody"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);
            await ExecAsync(c, "IF TYPE_ID('app.RigheTvp') IS NULL CREATE TYPE app.RigheTvp AS TABLE (Id int NOT NULL, Nota nvarchar(50) NULL);", ct);
        }

        ObjectBody.LiveDbObjectBodyResolver resolver = new(dbConn, dbConn);

        string? tableType = await resolver.ResolveSourceBodyAsync("TableType", "app", "RigheTvp", ct);
        tableType.Should().NotBeNullOrWhiteSpace();
        tableType.Should().Contain("CREATE TYPE [app].[RigheTvp] AS TABLE")
                 .And.Contain("[Id] [int] NOT NULL")
                 .And.Contain("[Nota] [nvarchar] (50)");

        string? schema = await resolver.ResolveSourceBodyAsync("Schema", "app", string.Empty, ct);
        schema.Should().Contain("CREATE SCHEMA [app]");
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
