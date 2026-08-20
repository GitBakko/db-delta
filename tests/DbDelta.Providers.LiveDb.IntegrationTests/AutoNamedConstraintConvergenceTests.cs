using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// Roadmap item 12, proved on the one shape that produces it: two databases
/// built by running the SAME DDL twice.
/// </summary>
/// <remarks>
/// <para>
/// This is why it could not be checked on the real instances. A RESTORE
/// preserves <c>object_id</c>, so every database descending from a backup of
/// another carries the same hashes — measured on 2026-08-18 across all three
/// combinations of the two live servers: 24 auto-named PKs out of 24 with
/// IDENTICAL names, 45 DEFAULTs out of 45 likewise. Pairing by name, the thing
/// item 12 fixed, would have worked there. The divergence needs two databases
/// that were CREATED separately, which is what these two throwaway ones are.
/// </para>
/// <para>
/// Two separately CREATED databases are not enough either, which is the sharper
/// finding these two throwaway ones produced. Build both from the same DDL in
/// the same order on a fresh server and SQL Server hands out the same
/// <c>object_id</c>s, so a CHECK, a DEFAULT and a FOREIGN KEY — whose suffix
/// comes from the parent column or table — come back with the IDENTICAL hash.
/// Only PK and UNIQUE diverged, their suffix carrying the index's own id. What
/// the divergence really needs is a different allocation history, which is what
/// the scratch objects below force: exactly what a database that has lived
/// through a few migrations has and a fresh one does not.
/// </para>
/// <para>
/// The first assertion is the mechanism, not the fix: unless the two servers
/// really did mint different names, everything below proves nothing.
/// </para>
/// </remarks>
[Collection(nameof(LiveDbCollection))]
public class AutoNamedConstraintConvergenceTests(LiveDbFixture fixture)
{
    /// <summary>
    /// Every constraint here is UNNAMED in the DDL, so SQL Server derives each
    /// name from the constraint's own object_id — a different one per database.
    /// </summary>
    private const string Ddl = """
        IF OBJECT_ID('dbo.Testa','U') IS NULL
            CREATE TABLE dbo.Testa (
                Id     int          NOT NULL PRIMARY KEY,
                Codice nvarchar(20) NOT NULL UNIQUE
            );
        IF OBJECT_ID('dbo.Righe','U') IS NULL
            CREATE TABLE dbo.Righe (
                Id      int NOT NULL PRIMARY KEY,
                TestaId int NOT NULL REFERENCES dbo.Testa (Id),
                Stato   int NOT NULL DEFAULT ((0)),
                Qta     int NOT NULL CHECK (Qta > 0)
            );
        """;

    [Fact]
    public async Task Two_databases_built_from_the_same_script_compare_identical()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string a = await BuildAsync("DbDeltaHashA", ct, idShift: 0);
        string b = await BuildAsync("DbDeltaHashB", ct, idShift: 3);

        // ── The mechanism. Without this the test passes on two databases that
        //    happen to agree, and says nothing about pairing.
        IReadOnlyList<string> namesA = await AutoNamedAsync(a, ct);
        IReadOnlyList<string> namesB = await AutoNamedAsync(b, ct);
        namesA.Should().HaveCount(5, "one PK and one UNIQUE on Testa, one PK, one DEFAULT and one CHECK on Righe");
        namesA.Should().NotIntersectWith(namesB,
            "a name derived from object_id cannot repeat across two separately created databases");

        Database source = (await new LiveDbSource(a, "source").LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(b, "target").LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);

        diff.Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Select(d => $"{d.Identity.Kind} {d.Identity.SchemaName}.{d.Identity.ObjectName} = {d.Status}")
            .Should().BeEmpty("the two databases hold the same schema, minted names apart");

        // And the script that follows: nothing to do, so nothing said. Before
        // item 12 this dropped the target's hash to add the source's — on
        // production primary keys.
        string script = new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default, dependencies: source.Dependencies);
        script.Should().NotContain("DROP CONSTRAINT").And.NotContain("ADD CONSTRAINT");
    }

    [Fact]
    public async Task A_foreign_key_the_server_named_is_paired_too()
    {
        // The FK half, which item 12 deliberately left out and 2026-08-20
        // closed. An inline REFERENCES leaves the naming to the server exactly
        // as an inline DEFAULT does.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string a = await BuildAsync("DbDeltaHashFkA", ct, idShift: 0);
        string b = await BuildAsync("DbDeltaHashFkB", ct, idShift: 5);

        string fkA = await SingleForeignKeyNameAsync(a, ct);
        string fkB = await SingleForeignKeyNameAsync(b, ct);
        fkA.Should().StartWith("FK__Righe__").And.NotBe(fkB);

        Database source = (await new LiveDbSource(a, "source").LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(b, "target").LoadAsync(ct)).Value!;

        source.Tables.Single(t => t.Name == "Righe").Constraints
            .OfType<ForeignKey>().Single().IsSystemNamed
            .Should().BeTrue("the reader has to say so, or the pairing never learns of it");

        new ComparisonEngine().Compare(source, target, ComparisonOptions.Default)
            .Differences.Single(d => d.Identity.ObjectName == "Righe")
            .Status.Should().Be(DifferenceStatus.Identical);
    }

    /// <summary>
    /// A database holding the schema, with <paramref name="idShift"/> objects
    /// created and dropped before it.
    /// </summary>
    /// <remarks>
    /// The shift is the whole point. Two fresh databases built by the same
    /// statements in the same order get the same <c>object_id</c>s, and a hash
    /// derived from one of those is then the same on both sides — which is how
    /// pairing by name looked like it worked. A database that has lived through
    /// a few migrations has a different allocation history; these scratch
    /// objects are the shortest way to give one to a database created a second
    /// ago.
    /// </remarks>
    private async Task<string> BuildAsync(string db, CancellationToken ct, int idShift)
    {
        await using (SqlConnection master = new(fixture.ConnectionString))
        {
            await master.OpenAsync(ct);
            await Exec(master, $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", ct);
        }

        string conn = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = db }.ConnectionString;
        await using (SqlConnection c = new(conn))
        {
            await c.OpenAsync(ct);
            for (int i = 0; i < idShift; i++)
            {
                await Exec(c, $"CREATE TABLE dbo._Scratch{i} (Id int NOT NULL); DROP TABLE dbo._Scratch{i};", ct);
            }
            await Exec(c, Ddl, ct);
        }
        return conn;
    }

    private static async Task<IReadOnlyList<string>> AutoNamedAsync(string conn, CancellationToken ct)
    {
        const string sql = """
            SELECT name FROM sys.key_constraints    WHERE is_system_named = 1
            UNION ALL SELECT name FROM sys.check_constraints   WHERE is_system_named = 1
            UNION ALL SELECT name FROM sys.default_constraints WHERE is_system_named = 1
            ORDER BY name;
            """;
        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(sql, c);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        List<string> names = [];
        while (await r.ReadAsync(ct).ConfigureAwait(false)) { names.Add(r.GetString(0)); }
        return names;
    }

    private static async Task<string> SingleForeignKeyNameAsync(string conn, CancellationToken ct)
    {
        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await using SqlCommand cmd = new(
            "SELECT name FROM sys.foreign_keys WHERE is_system_named = 1;", c);
        object? scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar as string ?? string.Empty;
    }

    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
