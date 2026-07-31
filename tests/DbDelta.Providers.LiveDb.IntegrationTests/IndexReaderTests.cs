using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class IndexReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_nonclustered_unique_included_and_filtered_indexes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaIx') IS NULL CREATE DATABASE DbDeltaIx;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaIx"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Doc') IS NULL
                BEGIN
                    CREATE TABLE dbo.Doc (
                        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Doc PRIMARY KEY,
                        Title nvarchar(200) NOT NULL,
                        Author nvarchar(100) NOT NULL,
                        IsDeleted bit NOT NULL CONSTRAINT DF_Doc_IsDeleted DEFAULT (0),
                        Tags nvarchar(200) NULL
                    );
                    CREATE NONCLUSTERED INDEX IX_Doc_Title ON dbo.Doc (Title ASC) INCLUDE (Author);
                    CREATE UNIQUE NONCLUSTERED INDEX UX_Doc_Author ON dbo.Doc (Author DESC);
                    CREATE NONCLUSTERED INDEX IX_Doc_Active ON dbo.Doc (Id) WHERE IsDeleted = 0;
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table doc = result.Value!.Tables.Single(t => t.Name == "Doc");
        doc.Indexes.Should().HaveCount(3);

        TableIndex ixTitle = doc.Indexes.Single(i => i.Name == "IX_Doc_Title");
        ixTitle.IsUnique.Should().BeFalse();
        ixTitle.IsClustered.Should().BeFalse();
        ixTitle.KeyColumns.Should().HaveCount(1);
        ixTitle.KeyColumns[0].Name.Should().Be("Title");
        ixTitle.KeyColumns[0].IsDescending.Should().BeFalse();
        ixTitle.IncludedColumns.Should().Equal("Author");

        TableIndex uxAuthor = doc.Indexes.Single(i => i.Name == "UX_Doc_Author");
        uxAuthor.IsUnique.Should().BeTrue();
        uxAuthor.KeyColumns[0].IsDescending.Should().BeTrue();

        TableIndex ixActive = doc.Indexes.Single(i => i.Name == "IX_Doc_Active");
        ixActive.FilterExpression.Should().Contain("IsDeleted");
    }

    /// <summary>
    /// The INCLUDE list must come back in the index's own column order.
    /// </summary>
    /// <remarks>
    /// <c>key_ordinal</c> is 1..n for key columns but ZERO for every included
    /// one, so ordering by it alone left the INCLUDE list unordered — the
    /// engine could return it however it liked. <c>IndexesEqual</c> compares
    /// that list as a SEQUENCE, so two reads of one unchanged index could
    /// disagree and report a rebuild of an index nobody touched. The INCLUDE
    /// below is declared in the REVERSE of the table's column order, so a read
    /// that falls back to column order gets it visibly wrong.
    /// </remarks>
    [Fact]
    public async Task Included_columns_come_back_in_the_indexes_own_order()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaIxOrder') IS NULL CREATE DATABASE DbDeltaIxOrder;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaIxOrder"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Wide') IS NULL
                BEGIN
                    CREATE TABLE dbo.Wide (
                        Id int NOT NULL CONSTRAINT PK_Wide PRIMARY KEY,
                        Alpha int NULL,
                        Bravo int NULL,
                        Charlie int NULL,
                        Delta int NULL
                    );
                    CREATE NONCLUSTERED INDEX IX_Wide ON dbo.Wide (Id)
                        INCLUDE (Delta, Charlie, Bravo, Alpha);
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        TableIndex ix = result.Value!.Tables.Single(t => t.Name == "Wide")
            .Indexes.Single(i => i.Name == "IX_Wide");

        ix.IncludedColumns.Should().Equal(
            ["Delta", "Charlie", "Bravo", "Alpha"],
            "the INCLUDE list is ordered by the index, not by the table's columns");
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
