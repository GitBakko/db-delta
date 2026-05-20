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

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
