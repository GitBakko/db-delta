using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using DbDelta.Providers.LiveDb.ObjectBody;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// The diff pane is the only surface that exists for giving informed consent
/// before an irreversible operation, and it does NOT read the catalog through
/// the readers the comparison uses: <see cref="LiveDbObjectBodyResolver"/>
/// carries its own hand-written queries. Every field those queries fail to
/// select turns a real difference into silence of the worst kind — the grid
/// says Different and the pane shows two byte-identical bodies.
/// </summary>
/// <remarks>
/// That drift had already been paid for three times (alias type schema, key
/// column direction, and then this) before anyone measured the whole surface
/// at once. It found six holes in two queries, not the one the backlog knew
/// about: column COLLATE, table DATA_COMPRESSION, index DATA_COMPRESSION,
/// index TypeDesc, the <c>i.type IN (1, 2)</c> filter IndexReader had already
/// removed as silent destruction, and the INCLUDE ordering tiebreak.
/// The equality assertion below is deliberately whole-body rather than
/// field-by-field: a seventh hole nobody has thought of yet fails it too.
/// </remarks>
[Collection(nameof(LiveDbCollection))]
public class ObjectBodyPaneParityTests(LiveDbFixture fixture)
{
    private const string DbName = "DbDeltaPaneParity";

    [Fact]
    public async Task Pane_body_is_byte_identical_to_the_body_emitted_from_the_compared_model()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await SeedAsync(ct);

        Database db = (await new LiveDbSource(dbConn).LoadAsync(ct)).Value!;
        Table compared = db.Tables.Single(t => t.Name == "PaneParity");
        string fromComparedModel = TableScriptEmitter.GenerateFullTableBody(compared);

        LiveDbObjectBodyResolver resolver = new(dbConn, dbConn);
        string? fromPane = await resolver.ResolveSourceBodyAsync("Table", "dbo", "PaneParity", ct);

        // One assertion, six holes. The pane reader and the compared reader are
        // two blind halves of the same sentence: if either stops reading a field
        // the emitter writes, these two strings stop matching.
        fromPane.Should().Be(fromComparedModel);
    }

    [Fact]
    public async Task Pane_body_carries_every_clause_the_deployed_script_would_carry()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await SeedAsync(ct);

        LiveDbObjectBodyResolver resolver = new(dbConn, dbConn);
        string body = (await resolver.ResolveSourceBodyAsync("Table", "dbo", "PaneParity", ct))!;

        // COLLATE on every string column — the rule DbDelta shares with Redgate.
        // Explicit on the column that merely inherits the database default too —
        // that is the Redgate-parity rule, not "emit it when it diverges".
        body.Should().Contain("[NomeDefault] [nvarchar] (50) COLLATE ");
        body.Should().Contain("[NomeAltro] [nvarchar] (50) COLLATE Latin1_General_BIN2");

        // The table's own rows, and the nonclustered index's, each carry their own
        // setting; ComparisonEngine compares both.
        body.Should().Contain(") WITH (DATA_COMPRESSION = PAGE);");
        body.Should().Contain("[IX_Pane]").And.Contain("INCLUDE ([Extra2], [NomeDefault])");

        // The columnstore was excluded by a type filter this query still carried
        // after IndexReader dropped it. Present now, and refused rather than
        // rendered as a plain CREATE INDEX — which is what a null TypeDesc would
        // have produced the moment the filter went.
        body.Should().Contain("IX_PaneCs").And.Contain("read, not scriptable by DbDelta");
        body.Should().NotContain("CREATE NONCLUSTERED INDEX [IX_PaneCs]");
    }

    [Fact]
    public async Task Pane_body_omits_COLLATE_on_an_alias_type_column()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dbConn = await SeedAsync(ct);

        LiveDbObjectBodyResolver resolver = new(dbConn, dbConn);
        string body = (await resolver.ResolveSourceBodyAsync("Table", "dbo", "PaneParity", ct))!;

        // The negative control, and it is not cosmetic: sys.columns reports a
        // collation for an alias-type column exactly as it does for an nvarchar
        // one, but SQL Server refuses "COLLATE clause cannot be used on
        // user-defined data types" and the deploy stops there. Adding
        // collation_name to this query without IsUserDefinedType — which it
        // already carried — would have put that statement in the pane.
        string codiceLine = body.Split('\n').Single(l => l.Contains("[Codice]", StringComparison.Ordinal));
        codiceLine.Should().NotContain("COLLATE");
    }

    private async Task<string> SeedAsync(CancellationToken ct)
    {
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, $"IF DB_ID('{DbName}') IS NULL CREATE DATABASE {DbName};", ct);
        }

        string dbConn = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = DbName
        }.ConnectionString;

        await using SqlConnection c = new(dbConn);
        await c.OpenAsync(ct);

        await ExecAsync(c, """
            IF TYPE_ID('dbo.PaneCode') IS NULL
                CREATE TYPE dbo.PaneCode FROM nvarchar(10) NOT NULL;
            """, ct);

        await ExecAsync(c, """
            IF OBJECT_ID('dbo.PaneParity','U') IS NULL
                CREATE TABLE dbo.PaneParity (
                    Id          int NOT NULL,
                    NomeDefault nvarchar(50) NOT NULL,
                    NomeAltro   nvarchar(50) COLLATE Latin1_General_BIN2 NOT NULL,
                    Codice      dbo.PaneCode NOT NULL,
                    Extra1      int NOT NULL,
                    Extra2      int NOT NULL,
                    CONSTRAINT PK_PaneParity PRIMARY KEY CLUSTERED (Id ASC)
                ) WITH (DATA_COMPRESSION = PAGE);
            """, ct);

        await ExecAsync(c, """
            IF INDEXPROPERTY(OBJECT_ID('dbo.PaneParity'), 'IX_Pane', 'IndexID') IS NULL
                CREATE NONCLUSTERED INDEX IX_Pane ON dbo.PaneParity (Extra1 ASC)
                    INCLUDE (Extra2, NomeDefault)
                    WITH (DATA_COMPRESSION = PAGE);
            """, ct);

        await ExecAsync(c, """
            IF INDEXPROPERTY(OBJECT_ID('dbo.PaneParity'), 'IX_PaneCs', 'IndexID') IS NULL
                CREATE NONCLUSTERED COLUMNSTORE INDEX IX_PaneCs ON dbo.PaneParity (Extra1, Extra2);
            """, ct);

        return dbConn;
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
