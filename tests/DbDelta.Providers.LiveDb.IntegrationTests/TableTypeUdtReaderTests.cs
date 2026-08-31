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
    /// The flag reaches the model, and the disk-based type beside it does not
    /// pick it up. Asserted on the READER for the usual reason: a flag nobody
    /// reads is false on both sides, the comparison says Identical, and the
    /// deploy replaces a memory-optimized type with a disk-based one under a
    /// success banner.
    /// </summary>
    /// <remarks>
    /// This is the test the 2026-08-31 audit could not write: it needs a
    /// MEMORY_OPTIMIZED_DATA filegroup, which the image does not ship with but
    /// which <see cref="FreshMemoryOptimizedDatabaseAsync"/> creates in four
    /// lines. Without the filegroup the CREATE fails with Msg 41337, not with
    /// something subtle.
    /// </remarks>
    [Fact]
    public async Task The_memory_optimized_flag_of_a_table_type_reaches_the_model()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await FreshMemoryOptimizedDatabaseAsync("DbDeltaTvpMemOpt", ct);

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, MemoryOptimizedTypes, ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        IReadOnlyList<TableTypeUdt> types = result.Value!.TableTypeUdts;

        types.Single(t => t.Name == "MemOptHashTvp").IsMemoryOptimized.Should().BeTrue();
        types.Single(t => t.Name == "MemOptRangeTvp").IsMemoryOptimized.Should().BeTrue();
        types.Single(t => t.Name == "DiskTvp").IsMemoryOptimized
             .Should().BeFalse("the control in the negative: the flag must separate the two engines");
    }

    /// <summary>
    /// Why the flag and not the index shape — measured rather than argued.
    /// </summary>
    /// <remarks>
    /// A memory-optimized type is free to key itself on a plain range index,
    /// and <c>sys.indexes</c> then reports exactly what a disk-based type
    /// reports. So every term the comparison already had — key columns, their
    /// direction, uniqueness, clustering — is equal across the two engines, and
    /// no amount of reading the index harder would have told them apart. If
    /// this test ever fails, the design note on
    /// <see cref="TableTypeUdt.IsMemoryOptimized"/> is wrong and the cheaper
    /// fix it rejects becomes available again.
    /// </remarks>
    [Fact]
    public async Task A_range_keyed_memory_optimized_type_is_indistinguishable_from_a_disk_based_one_by_its_keys()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await FreshMemoryOptimizedDatabaseAsync("DbDeltaTvpMemOptKeys", ct);

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, MemoryOptimizedTypes, ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        TableIndex memOpt = result.Value!.TableTypeUdts.Single(t => t.Name == "MemOptRangeTvp").PrimaryKey!;
        TableIndex disk = result.Value!.TableTypeUdts.Single(t => t.Name == "DiskTvp").PrimaryKey!;

        memOpt.IsClustered.Should().Be(disk.IsClustered);
        memOpt.IsUnique.Should().Be(disk.IsUnique);
        memOpt.IsSystemNamed.Should().Be(disk.IsSystemNamed);
        memOpt.KeyColumns.Select(k => $"{k.Name}:{k.IsDescending}")
              .Should().Equal(disk.KeyColumns.Select(k => $"{k.Name}:{k.IsDescending}"));
    }

    /// <summary>
    /// End to end on a live server: the difference is REPORTED, and only then
    /// refused at generation. Reporting first is the half that matters — a
    /// refusal on a row the grid never showed would be unexplainable.
    /// </summary>
    [Fact]
    public async Task A_memory_optimized_table_type_is_refused_rather_than_deployed_as_a_disk_based_one()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string srcConn = await FreshMemoryOptimizedDatabaseAsync("DbDeltaTvpMoSrc", ct);
        string tgtConn = await FreshDatabaseAsync("DbDeltaTvpMoTgt", ct);

        await using (SqlConnection c = new(srcConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, MemoryOptimizedTypes, ct);
        }

        Result<Database> src = await new LiveDbSource(srcConn).LoadAsync(ct);
        Result<Database> tgt = await new LiveDbSource(tgtConn).LoadAsync(ct);
        src.IsSuccess.Should().BeTrue(src.Error?.Message);
        tgt.IsSuccess.Should().BeTrue(tgt.Error?.Message);

        ComparisonResult diff = new ComparisonEngine().Compare(src.Value!, tgt.Value!, ComparisonOptions.Default);

        diff.Differences.Single(d => d.Identity.ObjectName == "MemOptHashTvp")
            .Status.Should().Be(DifferenceStatus.OnlyInA, "the row is reported before anything refuses it");

        Action generate = () => new ScriptGenerator().Generate(diff);

        generate.Should().Throw<UnscriptableTableTypeException>()
                .Which.Name.Should().BeOneOf("MemOptHashTvp", "MemOptRangeTvp");
    }

    /// <summary>
    /// The census counts the shape none of its other branches can see — which
    /// was measured on a probe database holding twelve memory-optimized table
    /// types: the sys.tables branch, the dynamic branch and INDEX_NON_ROWSTORE
    /// all returned 0.
    /// </summary>
    [Fact]
    public async Task The_census_counts_a_memory_optimized_table_type()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await FreshMemoryOptimizedDatabaseAsync("DbDeltaTvpMoCensus", ct);

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, MemoryOptimizedTypes, ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.Unexamined.Groups
              .Should().ContainSingle(g => g.Key == "MEMORY_OPTIMIZED_TABLE_TYPE")
              .Which.Count.Should().Be(2, "two of the three seeded types are memory-optimized");
    }

    /// <summary>
    /// The diff pane renders what the type IS instead of letting the refusal
    /// escape. Found by a mutation probe: deleting the branch that does this
    /// left all 804 tests green, so the pane had no coverage at all.
    /// </summary>
    /// <remarks>
    /// The caller — <c>AppStateViewModel.LoadDiffAsync</c> — turns any throw
    /// into "Impossibile leggere il corpo di …", so without the branch a user
    /// clicking a row the grid calls OnlyInA is told the body could not be
    /// read, which is both wrong and unactionable. The deploy path is where the
    /// refusal belongs; this one is the same choice
    /// <c>TableScriptEmitter</c> makes for a non-rowstore index.
    /// </remarks>
    [Fact]
    public async Task The_diff_pane_describes_a_memory_optimized_table_type_instead_of_refusing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await FreshMemoryOptimizedDatabaseAsync("DbDeltaTvpMoBody", ct);

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, MemoryOptimizedTypes, ct);
        }

        ObjectBody.LiveDbObjectBodyResolver resolver = new(dbConn, dbConn);

        string? memOpt = await resolver.ResolveSourceBodyAsync("TableType", "dbo", "MemOptHashTvp", ct);
        string? disk = await resolver.ResolveSourceBodyAsync("TableType", "dbo", "DiskTvp", ct);

        memOpt.Should().NotBeNull()
              .And.Contain("MEMORY_OPTIMIZED")
              .And.Contain("not scriptable by DbDelta");

        // The caveat is the FIRST line and the body follows it. Asserting the
        // body away — which the first cut of this test did — is what let the
        // pane collapse two Different types into one identical text; see
        // The_diff_pane_tells_two_memory_optimized_table_types_apart.
        memOpt!.Split(Environment.NewLine)[0].Should().StartWith("--");
        memOpt.Should().Contain("CREATE TYPE [dbo].[MemOptHashTvp] AS TABLE",
            "with the deploy refusing, this pane is the only place left that can say what to build by hand");

        // The control in the negative: the pane still emits a real body for a
        // disk-based type, so the comment above is a branch and not the only
        // thing this method can return.
        disk.Should().NotBeNull().And.Contain("CREATE TYPE [dbo].[DiskTvp] AS TABLE");
    }

    /// <summary>
    /// The caveat is a header, not a replacement: two memory-optimized types
    /// that differ must still render two different bodies.
    /// </summary>
    /// <remarks>
    /// The first cut of the fix returned the comment ALONE, and a comment built
    /// from schema and name is identical on both sides — they are the pairing
    /// key. So a row the grid calls Different showed two byte-identical panes
    /// and no highlighted change: the exact drift the remarks on
    /// <c>ResolveTableTypeBodyAsync</c> say it exists to prevent, reintroduced
    /// one case over. The test above could not see it because it resolves the
    /// SOURCE side of two different types; this one resolves the two SIDES of
    /// the same type, which is what the diff viewer actually does.
    /// </remarks>
    [Fact]
    public async Task The_diff_pane_tells_two_memory_optimized_table_types_apart()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string srcConn = await FreshMemoryOptimizedDatabaseAsync("DbDeltaTvpMoPaneSrc", ct);
        string tgtConn = await FreshMemoryOptimizedDatabaseAsync("DbDeltaTvpMoPaneTgt", ct);

        await using (SqlConnection c = new(srcConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, MemoryOptimizedTypes, ct);
        }

        await using (SqlConnection c = new(tgtConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF TYPE_ID('dbo.MemOptHashTvp') IS NULL
                    CREATE TYPE dbo.MemOptHashTvp AS TABLE (
                        Id    int NOT NULL,
                        Code  nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        Extra int NOT NULL,
                        PRIMARY KEY NONCLUSTERED HASH (Id) WITH (BUCKET_COUNT = 8)
                    ) WITH (MEMORY_OPTIMIZED = ON);
                """, ct);
        }

        ObjectBody.LiveDbObjectBodyResolver resolver = new(srcConn, tgtConn);

        string? source = await resolver.ResolveSourceBodyAsync("TableType", "dbo", "MemOptHashTvp", ct);
        string? target = await resolver.ResolveTargetBodyAsync("TableType", "dbo", "MemOptHashTvp", ct);

        source.Should().NotBe(target, "a row the grid calls Different must not show two identical panes");
        target.Should().Contain("[Extra]", "the pane has to name the column that differs");
        source.Should().NotContain("[Extra]");

        // The caveat survives on both sides: it is a header, and losing it
        // would let the pane read as if the type were deployable.
        source.Should().Contain("MEMORY_OPTIMIZED").And.Contain("not scriptable by DbDelta");
        target.Should().Contain("MEMORY_OPTIMIZED").And.Contain("not scriptable by DbDelta");
    }

    /// <summary>
    /// Three types that differ only in the two ways that matter: the storage
    /// engine, and — between the two memory-optimized ones — whether the key is
    /// a HASH or a range index.
    /// </summary>
    private const string MemoryOptimizedTypes = """
        IF TYPE_ID('dbo.MemOptHashTvp') IS NULL
            CREATE TYPE dbo.MemOptHashTvp AS TABLE (
                Id   int NOT NULL,
                Code nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PRIMARY KEY NONCLUSTERED HASH (Id) WITH (BUCKET_COUNT = 8)
            ) WITH (MEMORY_OPTIMIZED = ON);

        IF TYPE_ID('dbo.MemOptRangeTvp') IS NULL
            CREATE TYPE dbo.MemOptRangeTvp AS TABLE (
                Id   int NOT NULL,
                Code nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PRIMARY KEY NONCLUSTERED (Id)
            ) WITH (MEMORY_OPTIMIZED = ON);

        IF TYPE_ID('dbo.DiskTvp') IS NULL
            CREATE TYPE dbo.DiskTvp AS TABLE (
                Id   int NOT NULL,
                Code nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PRIMARY KEY NONCLUSTERED (Id)
            );
        """;

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

    /// <summary>
    /// The same, plus the MEMORY_OPTIMIZED_DATA filegroup a memory-optimized
    /// table type needs. The stock image ships no such filegroup, which is why
    /// the 2026-08-31 parity audit recorded the shape as "not reproduced in a
    /// container" — it is four lines, not a blocker.
    /// </summary>
    /// <remarks>
    /// Run against the new database rather than master, because
    /// <c>sys.filegroups</c> is per-database — asking master whether the
    /// filegroup exists always answers no, and the second run then fails on a
    /// duplicate. <c>type = 'FX'</c> is the memory-optimized filegroup. The
    /// container path is the Linux one: <c>mcr.microsoft.com/mssql/server</c>
    /// is a Linux image.
    /// </remarks>
    private async Task<string> FreshMemoryOptimizedDatabaseAsync(string name, CancellationToken ct)
    {
        string conn = await FreshDatabaseAsync(name, ct);
        await using (SqlConnection c = new(conn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, $"""
                IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE type = 'FX')
                BEGIN
                    EXEC sp_executesql N'
                        ALTER DATABASE CURRENT ADD FILEGROUP [MemOptFg] CONTAINS MEMORY_OPTIMIZED_DATA;';
                    EXEC sp_executesql N'
                        ALTER DATABASE CURRENT ADD FILE (NAME = N''{name}_mod'',
                            FILENAME = N''/var/opt/mssql/data/{name}_mod'') TO FILEGROUP [MemOptFg];';
                END
                """, ct);
        }
        return conn;
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
