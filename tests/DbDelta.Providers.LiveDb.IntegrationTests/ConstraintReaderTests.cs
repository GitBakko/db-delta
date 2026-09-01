using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class ConstraintReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_PK_UQ_CHECK_DEFAULT_for_a_table()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaCons') IS NULL CREATE DATABASE DbDeltaCons;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaCons"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Customer') IS NULL
                BEGIN
                    CREATE TABLE dbo.Customer (
                        Id        int           NOT NULL,
                        Email     nvarchar(200) NOT NULL,
                        Age       int           NOT NULL,
                        CreatedAt datetime2(7)  NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT (sysutcdatetime()),
                        CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED (Id),
                        CONSTRAINT UQ_Customer_Email UNIQUE NONCLUSTERED (Email),
                        CONSTRAINT CK_Customer_Age CHECK ([Age] >= 0)
                    );
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table customer = result.Value!.Tables.Single(t => t.Name == "Customer");
        customer.Constraints.Should().HaveCount(4);

        PrimaryKey pk = customer.Constraints.OfType<PrimaryKey>().Single();
        pk.Name.Should().Be("PK_Customer");
        pk.Columns.Should().Equal("Id");
        pk.IsClustered.Should().BeTrue();

        UniqueConstraint uq = customer.Constraints.OfType<UniqueConstraint>().Single();
        uq.Name.Should().Be("UQ_Customer_Email");
        uq.Columns.Should().Equal("Email");

        CheckConstraint ck = customer.Constraints.OfType<CheckConstraint>().Single();
        ck.Name.Should().Be("CK_Customer_Age");
        ck.Expression.Should().Contain("Age");

        DefaultConstraint df = customer.Constraints.OfType<DefaultConstraint>().Single();
        df.Name.Should().Be("DF_Customer_CreatedAt");
        df.ColumnName.Should().Be("CreatedAt");
        df.Expression.Should().Contain("sysutcdatetime");

        // The negative half of the auto-named test below: every name here came
        // from the DDL, so a reader that hardcoded the flag would fail here.
        customer.Constraints.Should().OnlyContain(c => !c.IsSystemNamed);
    }

    [Fact]
    public async Task LiveDbSource_flags_the_constraints_SQL_Server_named_itself()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaAutoNamed') IS NULL CREATE DATABASE DbDeltaAutoNamed;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaAutoNamed"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Ordini') IS NULL
                BEGIN
                    CREATE TABLE dbo.Ordini (
                        Id    int          NOT NULL PRIMARY KEY,
                        Stato int          NOT NULL DEFAULT ((0)),
                        Qta   int          NOT NULL CHECK ([Qta] > (0)),
                        Codice nvarchar(10) NOT NULL UNIQUE
                    );
                END
                IF OBJECT_ID('dbo.Righe') IS NULL
                BEGIN
                    -- An inline REFERENCES: SQL Server names this one too, from
                    -- its own object_id, exactly like the four above.
                    CREATE TABLE dbo.Righe (
                        Id       int NOT NULL PRIMARY KEY,
                        OrdineId int NOT NULL REFERENCES dbo.Ordini (Id)
                    );
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table ordini = result.Value!.Tables.Single(t => t.Name == "Ordini");
        ordini.Constraints.Should().HaveCount(4);
        ordini.Constraints.Should().OnlyContain(c => c.IsSystemNamed);

        // The shape the whole feature exists for: a suffix derived from the
        // constraint's own object_id, which the other server cannot reproduce.
        ordini.Constraints.OfType<PrimaryKey>().Single().Name.Should().StartWith("PK__Ordini__");
        ordini.Constraints.OfType<UniqueConstraint>().Single().Name.Should().StartWith("UQ__Ordini__");
        ordini.Constraints.OfType<CheckConstraint>().Single().Name.Should().StartWith("CK__Ordini__");
        ordini.Constraints.OfType<DefaultConstraint>().Single().Name.Should().StartWith("DF__Ordini__");

        Table righe = result.Value!.Tables.Single(t => t.Name == "Righe");
        ForeignKey fk = righe.Constraints.OfType<ForeignKey>().Single();
        fk.IsSystemNamed.Should().BeTrue("an inline REFERENCES leaves the naming to the server");
        fk.Name.Should().StartWith("FK__Righe__");
    }

    [Fact]
    public async Task LiveDbSource_loads_FK_with_cascade_delete()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaFk') IS NULL CREATE DATABASE DbDeltaFk;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaFk"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.OrderItem') IS NULL
                BEGIN
                    CREATE TABLE dbo.Customer (
                        Id int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY
                    );
                    CREATE TABLE dbo.OrderItem (
                        Id int NOT NULL CONSTRAINT PK_OrderItem PRIMARY KEY,
                        CustomerId int NOT NULL,
                        CONSTRAINT FK_OrderItem_Customer FOREIGN KEY (CustomerId)
                            REFERENCES dbo.Customer (Id) ON DELETE CASCADE
                    );
                END
                """, ct);
        }

        LiveDbSource source = new(dbConn);
        Result<Database> result = await source.LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Table orderItem = result.Value!.Tables.Single(t => t.Name == "OrderItem");
        ForeignKey fk = orderItem.Constraints.OfType<ForeignKey>().Single();

        fk.Name.Should().Be("FK_OrderItem_Customer");
        fk.Columns.Should().Equal("CustomerId");
        fk.ReferencedSchema.Should().Be("dbo");
        fk.ReferencedTable.Should().Be("Customer");
        fk.ReferencedColumns.Should().Equal("Id");
        fk.OnDelete.Should().Be(ReferentialAction.Cascade);
        fk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    /// <summary>
    /// A key column's direction reaches the model, and the diff pane renders it.
    /// </summary>
    /// <remarks>
    /// <b>The verification is on the READER.</b> Two sides both blind to the
    /// direction converge on all-ascending and a round-trip is green for the
    /// wrong reason — which is exactly how this survived: every key in the whole
    /// corpus was ascending, so nothing ever disagreed.
    /// </remarks>
    [Fact]
    public async Task A_key_columns_direction_reaches_the_model_and_the_diff_pane()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, "IF DB_ID('DbDeltaKeyDir') IS NULL CREATE DATABASE DbDeltaKeyDir;", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = "DbDeltaKeyDir"
        }.ConnectionString;

        await using (SqlConnection c = new(dbConn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Misto','U') IS NULL
                    CREATE TABLE dbo.Misto (
                        A int NOT NULL, B int NOT NULL, C int NOT NULL,
                        CONSTRAINT PK_Misto PRIMARY KEY CLUSTERED (A ASC, B DESC),
                        CONSTRAINT UQ_Misto UNIQUE NONCLUSTERED (C DESC, A ASC));
                """, ct);
        }

        Database db = (await new LiveDbSource(dbConn).LoadAsync(ct)).Value!;
        Table t = db.Tables.Single(x => x.Name == "Misto");

        PrimaryKey pk = t.Constraints.OfType<PrimaryKey>().Single();
        pk.Columns.Select(k => (k.Name, k.IsDescending))
          .Should().Equal(("A", false), ("B", true));

        UniqueConstraint uq = t.Constraints.OfType<UniqueConstraint>().Single();
        uq.Columns.Select(k => (k.Name, k.IsDescending))
          .Should().Equal(("C", true), ("A", false));

        // The pane reads the catalog through a query of its own and had drifted
        // from the compared reader before; here it must say the same thing.
        ObjectBody.LiveDbObjectBodyResolver resolver = new(dbConn, dbConn);
        string? body = await resolver.ResolveSourceBodyAsync("Table", "dbo", "Misto", ct);
        body.Should().Contain("[B] DESC").And.Contain("[C] DESC");
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
