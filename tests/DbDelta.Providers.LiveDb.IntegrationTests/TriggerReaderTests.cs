using DbDelta.Core.Abstractions;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

[Collection(nameof(LiveDbCollection))]
public class TriggerReaderTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task LiveDbSource_loads_DML_trigger_with_parent_table()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaTriggerInsert";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer(Id int NOT NULL);
            IF OBJECT_ID('dbo.trgCustomerAudit') IS NULL
                EXEC('CREATE TRIGGER dbo.trgCustomerAudit ON dbo.Customer AFTER INSERT AS BEGIN SET NOCOUNT ON; END');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Trigger trg = result.Value!.Triggers.Should().ContainSingle(t => t.Name == "trgCustomerAudit").Subject;
        trg.ParentSchema.Should().Be("dbo");
        trg.ParentTable.Should().Be("Customer");
        trg.IsDisabled.Should().BeFalse();
        trg.IsEncrypted.Should().BeFalse();
        trg.Body.Should().Contain("AFTER INSERT");
    }

    [Fact]
    public async Task LiveDbSource_surfaces_disabled_trigger()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaTriggerDisabled";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer(Id int NOT NULL);
            IF OBJECT_ID('dbo.trgCustomerAudit') IS NULL
                EXEC('CREATE TRIGGER dbo.trgCustomerAudit ON dbo.Customer AFTER UPDATE AS BEGIN SET NOCOUNT ON; END');
            DISABLE TRIGGER dbo.trgCustomerAudit ON dbo.Customer;
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Trigger trg = result.Value!.Triggers.Should().ContainSingle(t => t.Name == "trgCustomerAudit").Subject;
        trg.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task LiveDbSource_loads_INSTEAD_OF_trigger_on_a_view()
    {
        // TriggerQuery used to INNER JOIN sys.tables on tr.parent_id, which
        // silently discarded every trigger whose parent is a VIEW — i.e. every
        // INSTEAD OF trigger, the standard way to make a multi-table view
        // updatable. Both sides read zero triggers for it, so the pair compared
        // Identical and no DDL was ever generated: after a "successful" deploy
        // every INSERT against the target view failed.
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string dbName = "DbDeltaTriggerInsteadOfView";
        string conn = await CreateAndSeedAsync(dbName, """
            IF OBJECT_ID('dbo.Customer') IS NULL
                CREATE TABLE dbo.Customer(Id int NOT NULL, Name nvarchar(50) NULL);
            IF OBJECT_ID('dbo.vCustomer') IS NULL
                EXEC('CREATE VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer');
            IF OBJECT_ID('dbo.trgVCustomerInsert') IS NULL
                EXEC('CREATE TRIGGER dbo.trgVCustomerInsert ON dbo.vCustomer INSTEAD OF INSERT AS BEGIN SET NOCOUNT ON; END');
            """, ct);

        Result<Database> result = await new LiveDbSource(conn).LoadAsync(ct);
        result.IsSuccess.Should().BeTrue(result.Error?.Message);

        Trigger trg = result.Value!.Triggers
            .Should().ContainSingle(t => t.Name == "trgVCustomerInsert").Subject;
        trg.ParentSchema.Should().Be("dbo");
        trg.ParentTable.Should().Be("vCustomer");
        trg.Body.Should().Contain("INSTEAD OF INSERT");
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
