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
public class DependencyRoundTripTests(LiveDbFixture fixture)
{
    [Fact]
    public async Task Cross_kind_dependencies_apply_clean_on_empty_target()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "DepSrc", ct);
        await Create(fixture.ConnectionString, "DepTgt", ct);
        string src = Cat(fixture.ConnectionString, "DepSrc");
        string tgt = Cat(fixture.ConnectionString, "DepTgt");

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await Exec(c, "CREATE OR ALTER FUNCTION dbo.fnTax(@x money) RETURNS money AS BEGIN RETURN @x*0.2 END", ct);
            await Exec(c, "IF OBJECT_ID('dbo.Sale','U') IS NULL EXEC('CREATE TABLE dbo.Sale (Id int IDENTITY PRIMARY KEY, Net money NOT NULL, Tax AS (dbo.fnTax(Net)));')", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vBase AS SELECT Id, Net FROM dbo.Sale;", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vTop AS SELECT Id FROM dbo.vBase;", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default,
            dependencies: source.Dependencies);

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "ordered script failed to apply");

        Database after = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        ComparisonResult re = new ComparisonEngine().Compare(source, after, ComparisonOptions.Default);
        re.Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Where(d => d.Identity.Kind is "Table" or "View" or "Function")
            .Should().BeEmpty();
    }

    /// <summary>
    /// The convergence invariant on a real server, over every kind DbDelta
    /// models: seed one of each in the source, deploy into an empty target,
    /// read the target back, and it has to compare Identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test above covers Table, View and Function. Six readers had never
    /// been through a real apply — an emitter that writes DDL the server
    /// accepts, and a reader that reads it back into a DIFFERENT model, both
    /// look fine in isolation and disagree only here.
    /// </para>
    /// <para>
    /// The assertion is on every non-Identical pair, with no kind filter. A
    /// filter is what let the last six through, and anything the fixture does
    /// not create is Identical by absence anyway.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_modelled_kind_survives_a_deploy_and_a_re_read()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "KindsSrc", ct);
        await Create(fixture.ConnectionString, "KindsTgt", ct);
        string src = Cat(fixture.ConnectionString, "KindsSrc");
        string tgt = Cat(fixture.ConnectionString, "KindsTgt");

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            // CREATE SCHEMA has to be the first statement of its batch.
            await Exec(c, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);
            await Exec(c, "IF TYPE_ID('dbo.CodiceArticolo') IS NULL CREATE TYPE dbo.CodiceArticolo FROM nvarchar(20) NOT NULL;", ct);
            await Exec(c, "IF TYPE_ID('dbo.TvpRighe') IS NULL CREATE TYPE dbo.TvpRighe AS TABLE (Id int NOT NULL, Qta int NULL);", ct);
            await Exec(c, """
                IF OBJECT_ID('app.Articolo','U') IS NULL
                    CREATE TABLE app.Articolo (
                        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Articolo PRIMARY KEY,
                        Codice dbo.CodiceArticolo NOT NULL,
                        Qta int NOT NULL CONSTRAINT DF_Articolo_Qta DEFAULT ((0)),
                        CONSTRAINT CK_Articolo_Qta CHECK (Qta >= 0)
                    );
                """, ct);
            await Exec(c, "IF OBJECT_ID('IX_Articolo_Codice') IS NULL CREATE UNIQUE INDEX IX_Articolo_Codice ON app.Articolo (Codice);", ct);
            await Exec(c, "CREATE OR ALTER VIEW app.vArticolo AS SELECT Id, Codice FROM app.Articolo;", ct);
            await Exec(c, "CREATE OR ALTER FUNCTION app.fnQta(@id int) RETURNS int AS BEGIN RETURN (SELECT Qta FROM app.Articolo WHERE Id = @id) END", ct);
            await Exec(c, "CREATE OR ALTER PROCEDURE app.spArticolo AS BEGIN SET NOCOUNT ON; SELECT Id FROM app.Articolo; END", ct);
            await Exec(c, "CREATE OR ALTER TRIGGER app.trgArticolo ON app.Articolo AFTER INSERT AS BEGIN SET NOCOUNT ON; END", ct);
            await Exec(c, "IF OBJECT_ID('dbo.SeqArticolo','SO') IS NULL CREATE SEQUENCE dbo.SeqArticolo AS int START WITH 10 INCREMENT BY 5;", ct);
            await Exec(c, "IF OBJECT_ID('dbo.ArticoloAlias','SN') IS NULL CREATE SYNONYM dbo.ArticoloAlias FOR app.Articolo;", ct);
            await Exec(c, "IF DATABASE_PRINCIPAL_ID('app_reader') IS NULL CREATE ROLE app_reader;", ct);
            await Exec(c, "IF DATABASE_PRINCIPAL_ID('app_user') IS NULL CREATE USER app_user WITHOUT LOGIN;", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;

        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default,
            dependencies: source.Dependencies);

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "the script the tool generated did not apply");

        Database after = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        ComparisonResult re = new ComparisonEngine().Compare(source, after, ComparisonOptions.Default);

        re.Differences
            .Where(d => d.Status != DifferenceStatus.Identical)
            .Select(d => $"{d.Identity.Kind} {d.Identity.SchemaName}.{d.Identity.ObjectName} = {d.Status}")
            .Should().BeEmpty("a difference that survives its own script is one no operator can remove");
    }

    private static async Task Create(string conn, string db, CancellationToken ct)
    {
        await using SqlConnection c = new(conn);
        await c.OpenAsync(ct);
        await Exec(c, $"IF DB_ID('{db}') IS NULL CREATE DATABASE [{db}];", ct);
    }
    private static string Cat(string conn, string db) =>
        new SqlConnectionStringBuilder(conn) { InitialCatalog = db }.ConnectionString;
    private static async Task Exec(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
