using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class FunctionReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_scalar_function()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaFnScalar";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.fnSum') IS NULL
                EXEC('CREATE FUNCTION dbo.fnSum(@a int, @b int) RETURNS int AS BEGIN RETURN @a + @b; END');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Function fn = result.Value!.Functions.Should().ContainSingle(f => f.Name == "fnSum").Subject;
        fn.Schema.Should().Be("dbo");
        fn.FunctionKind.Should().Be(FunctionKind.Scalar);
        fn.IsEncrypted.Should().BeFalse();
        fn.Body.Should().Contain("RETURN @a + @b");
    }

    [Fact]
    public async Task LiveDbSource_loads_inline_TVF_function()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaFnInline";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.fnList') IS NULL
                EXEC('CREATE FUNCTION dbo.fnList() RETURNS TABLE AS RETURN (SELECT 1 AS X)');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Function fn = result.Value!.Functions.Should().ContainSingle(f => f.Name == "fnList").Subject;
        fn.FunctionKind.Should().Be(FunctionKind.InlineTableValued);
    }

    [Fact]
    public async Task LiveDbSource_surfaces_encrypted_function_with_null_body()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaFnEncrypted";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.fnSecret') IS NULL
                EXEC('CREATE FUNCTION dbo.fnSecret() RETURNS int WITH ENCRYPTION AS BEGIN RETURN 1; END');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Function fn = result.Value!.Functions.Should().ContainSingle(f => f.Name == "fnSecret").Subject;
        fn.IsEncrypted.Should().BeTrue();
        fn.Body.Should().BeNull();
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
