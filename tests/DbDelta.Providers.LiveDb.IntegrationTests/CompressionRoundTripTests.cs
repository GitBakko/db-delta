using DbDelta.Core.Abstractions;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// The compression emission, run against a real server and read back. Written
/// because one step of it was an assumption: the generator emits the setting on
/// <c>CREATE TABLE</c> and the clustered PK as a separate
/// <c>ALTER TABLE ADD CONSTRAINT</c>, which builds a clustered index over the
/// heap — and whether that index keeps the heap's compression is documented
/// inheritance, not something the emitter states. If it ever stops being true
/// the table deploys uncompressed and the next comparison reports a difference
/// the script cannot fix.
/// </summary>
[Collection(nameof(LiveDbCollection))]
public class CompressionRoundTripTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task A_compressed_table_with_a_clustered_pk_round_trips()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DbDeltaCompRT", ct);

        Table source = new(
            "dbo",
            "Packed",
            [new Column("Id", "int", false, 1), new Column("Payload", "nvarchar(400)", false, 2)],
            [new PrimaryKey("PK_Packed", ["Id"], IsClustered: true)],
            [new TableIndex(
                "IX_Packed_Payload", false, false, null,
                [new IndexColumn("Payload", false)], [], "ROW")],
            ModifyDate: null,
            DataCompression: "PAGE");

        string script = new ScriptGenerator().Generate(new ComparisonResult(
            [new DifferencePair(source.Identity, DifferenceStatus.OnlyInA, source, null)]));

        SqlBatchResult run = await SqlExecutor.ExecuteAsync(conn, script, ct, useOwnTransaction: false);
        run.Success.Should().BeTrue(run.ErrorMessage);

        Result<Database> read = await new LiveDbSource(conn).LoadAsync(ct);
        read.IsSuccess.Should().BeTrue(read.Error?.Message);

        Table back = read.Value!.Tables.Single(t => t.Name == "Packed");
        back.DataCompression.Should().Be("PAGE",
            "the clustered PK built over the compressed heap must keep its compression");
        back.Indexes.Single(i => i.Name == "IX_Packed_Payload").DataCompression.Should().Be("ROW");

        // Compared with the read-back COLUMNS substituted in: a hand-built
        // source names no collation and the server stamps its default on every
        // nvarchar, which is a difference about collation and not about this
        // test. Everything compression touches — the table's own rows and the
        // index's — is still the source's.
        Table expected = source with { Columns = back.Columns };

        // The point of a round trip: what comes back has to compare identical to
        // what went out, or the deploy leaves a difference behind it.
        new ComparisonEngine()
            .Compare(Db(expected), Db(back), Core.Options.ComparisonOptions.Default)
            .Differences.Single(p => p.Identity.Kind == "Table")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    private static Database Db(Table t) =>
        new("Db", Schemas: [new Schema("dbo")], Tables: [t], Views: [], Procedures: []);

    private async Task<string> FreshDbAsync(string db, CancellationToken ct)
    {
        await using SqlConnection bootstrap = new(fixture.ConnectionString);
        await bootstrap.OpenAsync(ct);
        await using (SqlCommand cmd = new($"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", bootstrap))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = db }.ConnectionString;
    }
}
