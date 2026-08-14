using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// The census, measured against a real server rather than believed. The reader
/// is one long UNION ALL over catalog views, which is exactly the kind of code
/// that compiles, runs, returns a plausible number and is wrong.
/// </summary>
[Collection(nameof(LiveDbCollection))]
public class UnexaminedCensusTests(LiveDbFixture fixture)
{
    /// <summary>
    /// The failure the census exists to disclose, demonstrated end to end: two
    /// databases whose ONLY difference is a columnstore index compare Identical,
    /// because IndexReader takes type IN (1,2) and neither side ever reports it.
    /// The verdict is not wrong so much as narrower than it sounds — and the
    /// census is what says so.
    /// </summary>
    [Fact]
    public async Task A_columnstore_only_difference_compares_Identical_and_the_census_says_why()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string withIndex = await SeedAsync("DbDeltaCensusA", columnstore: true, ct);
        string withoutIndex = await SeedAsync("DbDeltaCensusB", columnstore: false, ct);

        Database a = await LoadAsync(withIndex, ct);
        Database b = await LoadAsync(withoutIndex, ct);

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        DifferencePair fatti = result.Differences
            .Single(d => d.Identity.Kind == "Table" && d.Identity.ObjectName == "Fatti");
        fatti.Status.Should().Be(DifferenceStatus.Identical,
            "the columnstore index is invisible to both sides — this is the blind spot, not a bug in this test");

        result.Unexamined.IsEmpty.Should().BeFalse("the verdict must disclose that it skipped the index");
        result.Unexamined.Groups.Should().Contain(g => g.Key == "INDEX_NON_ROWSTORE" && g.Count >= 1);
        result.Unexamined.Summary.Should().Contain("columnstore");
    }

    /// <summary>
    /// The other families the reader claims to count. Each is seeded, so a
    /// mis-written UNION branch fails here rather than silently reporting zero.
    /// </summary>
    [Fact]
    public async Task Counts_extended_properties_and_database_scoped_ddl_triggers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await SeedAsync("DbDeltaCensusA", columnstore: true, ct);

        Database db = await LoadAsync(conn, ct);

        db.Unexamined.Groups.Should().Contain(g => g.Key == "EXTENDED_PROPERTY" && g.Count >= 1);
        db.Unexamined.Groups.Should().Contain(g => g.Key == "DDL_TRIGGER_DATABASE" && g.Count >= 1);
        db.Unexamined.Summary.Should()
            .Contain("proprietà estese").And
            .Contain("trigger DDL di database");
    }

    /// <summary>
    /// A database holding nothing outside the thirteen modelled kinds must
    /// produce NO caveat: a banner that is always on is a banner nobody reads.
    /// </summary>
    [Fact]
    public async Task An_ordinary_database_produces_no_caveat_at_all()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await SeedAsync("DbDeltaCensusB", columnstore: false, ct);

        Database db = await LoadAsync(conn, ct);

        db.Unexamined.IsEmpty.Should().BeTrue(
            "seeded with one plain table and nothing else: " + db.Unexamined.Summary);
        db.Unexamined.Summary.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Database A carries the unmodelled objects; database B is the same table
    /// and nothing else. The DDL trigger is created last so it does not fire on
    /// the CREATE TABLE above it.
    /// </summary>
    private async Task<string> SeedAsync(string database, bool columnstore, CancellationToken ct)
    {
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, $"IF DB_ID('{database}') IS NULL CREATE DATABASE {database};", ct);
        }

        string conn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = database,
        }.ConnectionString;

        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await ExecAsync(c, """
            IF OBJECT_ID('dbo.Fatti') IS NULL
                CREATE TABLE dbo.Fatti (
                    Id      int NOT NULL CONSTRAINT PK_Fatti PRIMARY KEY,
                    Importo decimal(18,2) NOT NULL
                );
            """, ct);

        if (columnstore)
        {
            await ExecAsync(c, """
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'NCCI_Fatti')
                    CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Fatti ON dbo.Fatti (Importo);
                """, ct);
            await ExecAsync(c, """
                IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE name = 'MS_Description')
                    EXEC sp_addextendedproperty
                        @name = N'MS_Description', @value = N'Tabella dei fatti',
                        @level0type = N'SCHEMA', @level0name = N'dbo',
                        @level1type = N'TABLE',  @level1name = N'Fatti';
                """, ct);
            await ExecAsync(c, """
                IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = 'trg_ddl_audit')
                    EXEC sp_executesql N'CREATE TRIGGER trg_ddl_audit ON DATABASE FOR DROP_TABLE AS PRINT ''audit'';';
                """, ct);
        }

        return conn;
    }

    private static async Task<Database> LoadAsync(string conn, CancellationToken ct)
    {
        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        return result.Value!;
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
