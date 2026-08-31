using DbDelta.Core.Abstractions;
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

    /// <summary>
    /// The diff pane has to show the keys too. The grid now calls a table type
    /// Different when only its PRIMARY KEY moved, so a pane that renders the
    /// columns alone would show two bodies a reader cannot tell apart — the
    /// same "the grid describes a row that shows nothing" bug as above, one
    /// level down.
    /// </summary>
    [Fact]
    public async Task The_diff_viewer_shows_the_keys_of_a_table_type()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await FreshDatabaseAsync("DbDeltaTvpKeyBody", ct);

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, FullSurfaceType, ct);
        }

        ObjectBody.LiveDbObjectBodyResolver resolver = new(dbConn, dbConn);
        string? body = await resolver.ResolveSourceBodyAsync("TableType", "dbo", "FullTvp", ct);

        body.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("PRIMARY KEY CLUSTERED ([Id] ASC, [Qty] DESC)")
            .And.Contain("UNIQUE NONCLUSTERED ([Code] ASC)")
            .And.Contain("CHECK ")
            .And.Contain("INDEX [IX_FullTvp_Note] NONCLUSTERED ([Note] ASC) INCLUDE ([Code])")
            .And.Contain("IDENTITY(1,1)")
            .And.Contain("DEFAULT ")
            .And.Contain("[Total] AS ");
    }

    /// <summary>
    /// Everything a table type can declare has to arrive in the model, because
    /// the only edit SQL Server allows is DROP + CREATE and whatever the model
    /// does not carry is dropped by the deploy.
    /// </summary>
    /// <remarks>
    /// <b>This is the load-bearing half, not the round-trip below.</b> If
    /// neither side is read, both come back keyless, the comparison says
    /// Identical and a deploy that dropped the key looks like a success — which
    /// is precisely how the loss stayed invisible until the 2026-08-31 parity
    /// audit. So the assertion is on the READER.
    /// </remarks>
    [Fact]
    public async Task The_keys_of_a_table_type_reach_the_model()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await FreshDatabaseAsync("DbDeltaTvpKeys", ct);

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, FullSurfaceType, ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        TableTypeUdt tt = result.Value!.TableTypeUdts.Single(t => t.Name == "FullTvp");

        tt.PrimaryKey.Should().NotBeNull();
        tt.PrimaryKey!.IsClustered.Should().BeTrue();
        tt.PrimaryKey.IsSystemNamed.Should().BeTrue("a table type refuses a CONSTRAINT clause, so the name is always the server's");
        tt.PrimaryKey.KeyColumns.Select(k => $"{k.Name}:{k.IsDescending}")
          .Should().Equal(["Id:False", "Qty:True"], "the DESC is part of the key, not decoration");

        tt.Keys.Should().ContainSingle(k => k.IsUniqueConstraint)
          .Which.KeyColumns.Select(k => k.Name).Should().Equal("Code");

        TableIndex ix = tt.Keys.Single(k => !k.IsPrimaryKey && !k.IsUniqueConstraint);
        ix.Name.Should().Be("IX_FullTvp_Note", "an inline INDEX name is the user's, unlike the constraints");
        ix.IsSystemNamed.Should().BeFalse();
        ix.KeyColumns.Select(k => k.Name).Should().Equal("Note");
        ix.IncludedColumns.Should().Equal(["Code"], "INCLUDE is legal on a table type's inline index");

        tt.CheckConstraints.Should().ContainSingle()
          .Which.Expression.Should().Contain("Qty");

        Column id = tt.Columns.Single(c => c.Name == "Id");
        id.IsIdentity.Should().BeTrue();
        id.IdentitySeed.Should().Be(1);
        id.IdentityIncrement.Should().Be(1);

        tt.Columns.Single(c => c.Name == "Qty").DefaultExpression.Should().Contain("0");
        tt.Columns.Single(c => c.Name == "Total").ComputedExpression.Should().Contain("[Id]");
    }

    /// <summary>
    /// The convergence invariant applied to a table type that carries keys:
    /// deploy, re-read, and the two sides must be Identical — with the target
    /// catalog checked directly, so Identical cannot be reached by both sides
    /// being equally empty.
    /// </summary>
    [Fact]
    public async Task A_table_type_with_keys_survives_a_deploy_and_a_re_read()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string srcConn = await FreshDatabaseAsync("DbDeltaTvpRtSrc", ct);
        string tgtConn = await FreshDatabaseAsync("DbDeltaTvpRtTgt", ct);

        await using (SqlConnection c = new(srcConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, FullSurfaceType, ct);
        }

        Database source = (await new LiveDbSource(srcConn, "source").LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgtConn, "target").LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            diff,
            selection: null,
            options: ComparisonOptions.Default,
            dependencies: source.Dependencies,
            dropDependencies: target.Dependencies);

        SqlBatchResult applied = await SqlExecutor.ExecuteAsync(tgtConn, script, ct, useOwnTransaction: false);
        applied.Success.Should().BeTrue(applied.ErrorMessage ?? "the table type script did not apply");

        // The catalog, before the comparison: two keyless types also compare
        // Identical, and that is the failure this test exists to exclude.
        await using (SqlConnection c = new(tgtConn))
        {
            await c.OpenAsync(ct);
            (await ScalarAsync(c, """
                SELECT COUNT(*) FROM sys.table_types tt
                JOIN sys.key_constraints kc ON kc.parent_object_id = tt.type_table_object_id
                WHERE tt.name = 'FullTvp';
                """, ct)).Should().Be(2, "the PRIMARY KEY and the UNIQUE both have to be on the target");
            (await ScalarAsync(c, """
                SELECT COUNT(*) FROM sys.table_types tt
                JOIN sys.check_constraints cc ON cc.parent_object_id = tt.type_table_object_id
                WHERE tt.name = 'FullTvp';
                """, ct)).Should().Be(1, "the CHECK has to be on the target");
        }

        Database after = (await new LiveDbSource(tgtConn, "target").LoadAsync(ct)).Value!;
        new ComparisonEngine().Compare(source, after, ComparisonOptions.Default)
            .Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Select(d => $"{d.Identity.Kind} {d.Identity.SchemaName}.{d.Identity.ObjectName} = {d.Status}")
            .Should().BeEmpty("a table type that does not converge is losing part of itself on every deploy");
    }

    /// <summary>
    /// Every shape SQL Server lets a table type declare. No <c>CONSTRAINT</c>
    /// clause anywhere: it is a syntax error inside <c>CREATE TYPE</c>.
    /// </summary>
    private const string FullSurfaceType = """
        IF TYPE_ID('dbo.FullTvp') IS NULL
            CREATE TYPE dbo.FullTvp AS TABLE (
                Id    int IDENTITY(1,1) NOT NULL,
                Code  nvarchar(10) NOT NULL UNIQUE,
                Qty   int NOT NULL DEFAULT (0) CHECK (Qty > 0),
                Note  nvarchar(50) NULL,
                Total AS (Id + Qty),
                PRIMARY KEY CLUSTERED (Id ASC, Qty DESC),
                INDEX IX_FullTvp_Note NONCLUSTERED (Note) INCLUDE (Code)
            );
        """;

    private async Task<string> FreshDatabaseAsync(string name, CancellationToken ct)
    {
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, $"IF DB_ID('{name}') IS NULL CREATE DATABASE [{name}];", ct);
        }
        return new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    private static async Task<int> ScalarAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
