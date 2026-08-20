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

    /// <summary>
    /// Compression, read back from a real catalog for both the table's own rows
    /// and each index separately — they are independent settings and routinely
    /// differ. Nothing read it at all before: a PAGE-compressed table deployed
    /// as an uncompressed one and the next comparison said the two matched.
    /// </summary>
    [Fact]
    public async Task LiveDbSource_reads_table_and_index_compression_separately()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaIxComp') IS NULL CREATE DATABASE DbDeltaIxComp;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaIxComp"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Packed') IS NULL
                BEGIN
                    CREATE TABLE dbo.Packed (
                        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Packed PRIMARY KEY,
                        Payload nvarchar(400) NOT NULL
                    ) WITH (DATA_COMPRESSION = PAGE);
                    CREATE NONCLUSTERED INDEX IX_Packed_Row ON dbo.Packed (Payload)
                        WITH (DATA_COMPRESSION = ROW);
                    CREATE NONCLUSTERED INDEX IX_Packed_Plain ON dbo.Packed (Id, Payload);
                END
                IF OBJECT_ID('dbo.Plain') IS NULL
                    CREATE TABLE dbo.Plain (Id int NOT NULL);
                """, ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        Database db = result.Value!;

        Table packed = db.Tables.Single(t => t.Name == "Packed");
        packed.DataCompression.Should().Be("PAGE", "the clustered index carries the table's rows");
        packed.Indexes.Single(i => i.Name == "IX_Packed_Row").DataCompression.Should().Be("ROW");
        packed.Indexes.Single(i => i.Name == "IX_Packed_Plain").DataCompression.Should().Be("NONE");

        db.Tables.Single(t => t.Name == "Plain").DataCompression.Should().Be("NONE",
            "a heap reports its own row too");
    }

    /// <summary>
    /// The types the reader used to filter away with <c>AND i.type IN (1, 2)</c>.
    /// Read against a real catalog, because the shape of <c>sys.index_columns</c>
    /// for a columnstore is nothing like a rowstore index's — every column comes
    /// back with <c>key_ordinal = 0</c> — and a query that compiles can still
    /// return the wrong rows.
    /// </summary>
    /// <summary>
    /// An index on a VIEW reaches the model. It used to reach nothing: the
    /// reader joined sys.tables, so two databases differing only by an indexed
    /// view compared Identical.
    /// </summary>
    /// <remarks>
    /// This has to be asserted on the READER. A round-trip cannot catch it: if
    /// neither side is read, both sides have no indexes and the comparison is
    /// Identical for the wrong reason — which is precisely how the blind spot
    /// survived being declared in the census.
    /// </remarks>
    [Fact]
    public async Task An_index_on_a_view_reaches_the_model()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaIxView') IS NULL CREATE DATABASE DbDeltaIxView;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaIxView"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, "IF OBJECT_ID('dbo.Ordine','U') IS NULL CREATE TABLE dbo.Ordine (Id int NOT NULL, Stato int NOT NULL);", ct);
            await ExecAsync(c, "CREATE OR ALTER VIEW dbo.vOrdini AS SELECT Id FROM dbo.Ordine;", ct);
            await ExecAsync(c, "CREATE OR ALTER VIEW dbo.vOrdiniPerStato WITH SCHEMABINDING AS SELECT Stato, COUNT_BIG(*) AS N FROM dbo.Ordine GROUP BY Stato;", ct);
            await ExecAsync(c, "IF INDEXPROPERTY(OBJECT_ID('dbo.vOrdiniPerStato'), 'IX_vOrdiniPerStato', 'IndexID') IS NULL CREATE UNIQUE CLUSTERED INDEX IX_vOrdiniPerStato ON dbo.vOrdiniPerStato (Stato);", ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        View view = result.Value!.Views.Single(v => v.Name == "vOrdiniPerStato");
        TableIndex index = view.Indexes.Should().ContainSingle().Subject;
        index.Name.Should().Be("IX_vOrdiniPerStato");
        index.IsUnique.Should().BeTrue();
        index.IsClustered.Should().BeTrue();
        index.KeyColumns.Select(k => k.Name).Should().Equal("Stato");

        // The negative control: an ordinary view has none, and gaining the
        // ability to see them must not invent any.
        result.Value!.Views.Where(v => v.Name != "vOrdiniPerStato")
            .Should().OnlyContain(v => v.Indexes.Count == 0);
    }

    [Fact]
    public async Task Non_rowstore_indexes_are_read_and_carry_their_type()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaIxKinds') IS NULL CREATE DATABASE DbDeltaIxKinds;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaIxKinds"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Fatti') IS NULL
                BEGIN
                    CREATE TABLE dbo.Fatti (
                        Id      int NOT NULL CONSTRAINT PK_Fatti PRIMARY KEY,
                        Importo decimal(18,2) NOT NULL,
                        Note    xml NULL
                    );
                    CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Fatti ON dbo.Fatti (Importo);
                    CREATE PRIMARY XML INDEX PXML_Fatti ON dbo.Fatti (Note);
                    CREATE NONCLUSTERED INDEX IX_Fatti_Importo ON dbo.Fatti (Importo);
                END
                """, ct);
        }

        Result<Database> result = await new LiveDbSource(dbConn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table fatti = result.Value!.Tables.Single(t => t.Name == "Fatti");

        TableIndex ncci = fatti.Indexes.Single(i => i.Name == "NCCI_Fatti");
        ncci.TypeDesc.Should().Be("NONCLUSTERED COLUMNSTORE");
        ncci.IsRowstore.Should().BeFalse();

        fatti.Indexes.Single(i => i.Name == "PXML_Fatti").IsRowstore.Should().BeFalse();

        TableIndex plain = fatti.Indexes.Single(i => i.Name == "IX_Fatti_Importo");
        plain.TypeDesc.Should().Be("NONCLUSTERED");
        plain.IsRowstore.Should().BeTrue("the rowstore index next to them must be untouched by the widening");
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
