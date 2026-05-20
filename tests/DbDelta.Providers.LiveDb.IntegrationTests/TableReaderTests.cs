using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class TableReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_a_table_with_columns_and_identity()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Arrange — create a fresh DB and a known schema
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaTest') IS NULL CREATE DATABASE DbDeltaTest;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaTest"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Customer') IS NULL
                    CREATE TABLE dbo.Customer (
                        Id    int IDENTITY(1,1) NOT NULL,
                        Name  nvarchar(100)     NOT NULL,
                        Email nvarchar(200)         NULL
                    );
                """, ct);
        }

        // Act
        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);

        // Assert
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        Table customer = result.Value!.Tables.Single(t => t.Name == "Customer");
        customer.Schema.Should().Be("dbo");
        customer.Columns.Should().HaveCount(3);
        customer.Columns[0].Name.Should().Be("Id");
        customer.Columns[0].IsIdentity.Should().BeTrue();
        customer.Columns[2].IsNullable.Should().BeTrue();
    }

    [Fact]
    public async Task LiveDbSource_loads_identity_seed_increment_and_computed_columns()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaTestM2') IS NULL CREATE DATABASE DbDeltaTestM2;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaTestM2"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Person') IS NULL
                    CREATE TABLE dbo.Person (
                        Id        int IDENTITY(1000, 5) NOT NULL,
                        FirstName nvarchar(50)          NOT NULL,
                        LastName  nvarchar(50)          NOT NULL,
                        FullName  AS ([FirstName] + N' ' + [LastName]) PERSISTED
                    );
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table person = result.Value!.Tables.Single(t => t.Name == "Person");
        Column id = person.Columns.Single(c => c.Name == "Id");
        id.IsIdentity.Should().BeTrue();
        id.IdentitySeed.Should().Be(1000);
        id.IdentityIncrement.Should().Be(5);

        Column fullName = person.Columns.Single(c => c.Name == "FullName");
        fullName.ComputedExpression.Should().NotBeNull();
        fullName.IsPersistedComputed.Should().BeTrue();
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
