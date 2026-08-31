using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using DbDelta.Persistence.Sql;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDelta.Providers.LiveDb.IntegrationTests;

/// <summary>
/// The schema of an ALIAS type reaches the model. <c>TYPE_NAME(user_type_id)</c>
/// returns the bare name whatever schema the type lives in, so a column of type
/// <c>app.CodiceArticolo</c> was emitted as <c>[Codice] [CodiceArticolo]</c> and
/// died on the target with Msg 2715.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verification is on the READER, not on the round-trip.</b> If neither
/// side carries the schema, both sides read the same bare name, the pair
/// compares Identical and a round-trip converges for the wrong reason — the
/// exact way this defect stayed invisible while every alias type in the corpus
/// sat in <c>dbo</c>. One test below does deploy, and it deploys into a target
/// where the unqualified form is proven to fail first.
/// </para>
/// <para>
/// <b>Do not reach for <c>EXECUTE AS</c> to vary the default schema in one
/// batch.</b> Type names bind when the batch COMPILES, before the
/// <c>EXECUTE AS</c> statement has run, so the whole batch resolves as the
/// original principal and a <c>SELECT</c> placed before the DDL never prints.
/// Measured. These tests do not need impersonation: they create the type in a
/// non-<c>dbo</c> schema and connect as a <c>dbo</c>-default principal, which
/// is the failing configuration itself.
/// </para>
/// </remarks>
[Collection(nameof(LiveDbCollection))]
public class AliasTypeSchemaReaderTests(LiveDbFixture fixture)
{
    /// <summary>
    /// The reader carries the schema for all three shapes that can be typed
    /// with an alias type: a table column, a table-type column and a sequence.
    /// </summary>
    [Fact]
    public async Task The_reader_carries_the_schema_of_an_alias_type_outside_dbo()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DbDeltaAliasSchema", ct);

        await using (SqlConnection c = new(conn))
        {
            await c.OpenAsync(ct);
            await SeedAsync(c, ct);
        }

        Database db = (await new LiveDbSource(conn).LoadAsync(ct)).Value!;

        Column tableCol = db.Tables.Single(t => t.Name == "Articolo").Columns.Single(x => x.Name == "Codice");
        tableCol.TypeSchema.Should().Be("app");
        tableCol.IsUserDefinedType.Should().BeTrue();
        // The name stays BARE. A dotted DataType is bracket-quoted as one
        // identifier downstream and yields the type [app.CodiceArticolo].
        tableCol.DataType.Should().Be("CodiceArticolo");

        Column tvpCol = db.TableTypeUdts.Single(t => t.Name == "RigheTvp").Columns.Single(x => x.Name == "Codice");
        tvpCol.TypeSchema.Should().Be("app");
        tvpCol.DataType.Should().Be("CodiceArticolo");

        Sequence seq = db.Sequences.Single(s => s.Name == "SeqArticolo");
        seq.TypeSchema.Should().Be("app");
        seq.DataType.Should().Be("MioIntTipo");

        // The guard: a BUILT-IN base type reports no schema. Without this,
        // `SCHEMA_NAME(ty.schema_id)` unguarded would hand back 'sys'.
        db.Sequences.Single(s => s.Name == "SeqPlain").TypeSchema.Should().BeNull();
        db.Tables.Single(t => t.Name == "Articolo").Columns
          .Single(x => x.Name == "Id").TypeSchema.Should().BeNull();
    }

    /// <summary>
    /// The negative controls: an alias type in <c>dbo</c> reports its schema
    /// like any other, and a BUILT-IN type reports none at all. The second is
    /// the one that matters — it is what keeps <c>SCHEMA_NAME</c> from handing
    /// back <c>sys</c> and every column coming out <c>[sys].[nvarchar]</c>.
    /// </summary>
    [Fact]
    public async Task A_dbo_alias_type_reports_dbo_and_a_built_in_type_reports_nothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DbDeltaAliasSchemaNeg", ct);

        await using (SqlConnection c = new(conn))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, "IF TYPE_ID('dbo.CodiceDbo') IS NULL CREATE TYPE dbo.CodiceDbo FROM nvarchar(20) NOT NULL;", ct);
            await ExecAsync(c, """
                IF OBJECT_ID('dbo.Misto','U') IS NULL
                    CREATE TABLE dbo.Misto (Id int NOT NULL, Codice dbo.CodiceDbo NOT NULL, Nome nvarchar(50) NULL);
                """, ct);
        }

        Database db = (await new LiveDbSource(conn).LoadAsync(ct)).Value!;
        Table t = db.Tables.Single(x => x.Name == "Misto");

        t.Columns.Single(c => c.Name == "Codice").TypeSchema.Should().Be("dbo");
        t.Columns.Single(c => c.Name == "Id").TypeSchema.Should().BeNull();
        t.Columns.Single(c => c.Name == "Nome").TypeSchema.Should().BeNull();

        // A dbo alias type is qualified like any other, and the built-in
        // beside it is not: the two halves of the policy in one script.
        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));
        sql.Should().Contain("[Codice] [dbo].[CodiceDbo] NOT NULL")
           .And.Contain("[Nome] [nvarchar] (50)")
           .And.NotContain("[sys]");
    }

    /// <summary>
    /// The defect itself, and its fix, on a real server: the unqualified form
    /// is refused with Msg 2715, and the script DbDelta now generates is
    /// accepted where it is.
    /// </summary>
    /// <remarks>
    /// The refusal is asserted FIRST and on purpose. Without it this test would
    /// pass just as well against an emitter that never qualified anything, and
    /// would be proving that the target accepts a script rather than that the
    /// script had to be qualified to be accepted.
    /// </remarks>
    [Fact]
    public async Task The_generated_script_deploys_where_the_unqualified_form_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string src = await FreshDbAsync("DbDeltaAliasSchemaSrc", ct);
        string tgt = await FreshDbAsync("DbDeltaAliasSchemaTgt", ct);

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await SeedAsync(c, ct);
        }

        // The target gets the schema and the type, and nothing else: the type
        // exists, it is simply not in the default schema of whoever deploys.
        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);
            await ExecAsync(c, "IF TYPE_ID('app.CodiceArticolo') IS NULL CREATE TYPE app.CodiceArticolo FROM nvarchar(20) NOT NULL;", ct);
            // Both alias types are pre-created so this test measures ONE thing.
            // A sequence over an alias type the target lacks fails with Msg 243
            // regardless of qualification: DependencyResolver.KindRank puts
            // Sequence at 0 and UserDefinedType at 1, so the CREATE SEQUENCE is
            // emitted before the type it is declared over, and no edge exists to
            // reorder them — sys.sql_expression_dependencies records none for a
            // binding to a type. Separate defect, open in docs/BACKLOG.md; it
            // cannot be closed by swapping the two ranks, because the DROP order
            // is that same rank reversed and today it is correct.
            await ExecAsync(c, "IF TYPE_ID('app.MioIntTipo') IS NULL CREATE TYPE app.MioIntTipo FROM bigint NOT NULL;", ct);

            // The mechanism, asserted before the verdict: this connection's
            // default schema is dbo, so the bare name cannot be resolved.
            Func<Task> unqualified = () => ExecAsync(c,
                "CREATE TABLE dbo.SondaNonQualificata (Codice CodiceArticolo NOT NULL);", ct);
            (await unqualified.Should().ThrowAsync<SqlException>())
                .Which.Number.Should().Be(2715, "an unqualified alias type outside dbo is invisible here");
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        ComparisonResult diff = new ComparisonEngine().Compare(source, target, ComparisonOptions.Default);
        string script = new ScriptGenerator().Generate(
            diff, selection: null, options: ComparisonOptions.Default, dependencies: source.Dependencies);

        script.Should().Contain("[app].[CodiceArticolo]");

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "the qualified script failed to apply");

        Database after = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        after.Tables.Single(t => t.Name == "Articolo")
             .Columns.Single(c => c.Name == "Codice").TypeSchema.Should().Be("app");
    }

    /// <summary>
    /// The diff pane reads the catalog through a resolver of its own, whose
    /// column query had no <c>sys.types</c> join at all. Left alone it would
    /// render the bare name on both sides of a row the grid calls Different —
    /// two panes a reader cannot tell apart.
    /// </summary>
    [Fact]
    public async Task The_diff_pane_shows_the_qualified_type_for_a_table_and_for_a_sequence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string conn = await FreshDbAsync("DbDeltaAliasSchemaPane", ct);

        await using (SqlConnection c = new(conn))
        {
            await c.OpenAsync(ct);
            await SeedAsync(c, ct);
        }

        ObjectBody.LiveDbObjectBodyResolver resolver = new(conn, conn);

        string? table = await resolver.ResolveSourceBodyAsync("Table", "dbo", "Articolo", ct);
        table.Should().Contain("[Codice] [app].[CodiceArticolo]");

        string? sequence = await resolver.ResolveSourceBodyAsync("Sequence", "dbo", "SeqArticolo", ct);
        sequence.Should().Contain("AS [app].[MioIntTipo]");

        string? tableType = await resolver.ResolveSourceBodyAsync("TableType", "dbo", "RigheTvp", ct);
        tableType.Should().Contain("[Codice] [app].[CodiceArticolo]");

        // The same guard on the pane's own two queries, which are a second
        // reader and drifted from the first before: a built-in stays bare.
        table.Should().Contain("[Id] [int] NOT NULL").And.NotContain("[sys]");
        string? plain = await resolver.ResolveSourceBodyAsync("Sequence", "dbo", "SeqPlain", ct);
        plain.Should().Contain("AS bigint").And.NotContain("[sys]");
    }

    /// <summary>
    /// Two same-named alias types in different schemas are two different types,
    /// and the comparison has to say so. Without the schema in the model both
    /// sides read <c>DataType = "CodiceArticolo"</c>, the table compares
    /// Identical and nothing is emitted: silence, not a wrong script.
    /// </summary>
    [Fact]
    public async Task Two_same_named_alias_types_in_different_schemas_make_the_table_Different()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string src = await FreshDbAsync("DbDeltaAliasSchemaAmbSrc", ct);
        string tgt = await FreshDbAsync("DbDeltaAliasSchemaAmbTgt", ct);

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);
            await ExecAsync(c, "IF TYPE_ID('app.CodiceArticolo') IS NULL CREATE TYPE app.CodiceArticolo FROM nvarchar(20) NOT NULL;", ct);
            await ExecAsync(c, "IF OBJECT_ID('dbo.Articolo','U') IS NULL CREATE TABLE dbo.Articolo (Id int NOT NULL, Codice app.CodiceArticolo NOT NULL);", ct);
        }

        // Same table, same column name, same base type, same length — the only
        // difference is which schema the alias type lives in.
        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            await ExecAsync(c, "IF TYPE_ID('dbo.CodiceArticolo') IS NULL CREATE TYPE dbo.CodiceArticolo FROM nvarchar(20) NOT NULL;", ct);
            await ExecAsync(c, "IF OBJECT_ID('dbo.Articolo','U') IS NULL CREATE TABLE dbo.Articolo (Id int NOT NULL, Codice dbo.CodiceArticolo NOT NULL);", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;

        // The mechanism first: both sides really do carry the same bare name.
        source.Tables.Single(t => t.Name == "Articolo").Columns.Single(c => c.Name == "Codice")
              .DataType.Should().Be("CodiceArticolo");
        target.Tables.Single(t => t.Name == "Articolo").Columns.Single(c => c.Name == "Codice")
              .DataType.Should().Be("CodiceArticolo");

        new ComparisonEngine().Compare(source, target, ComparisonOptions.Default)
            .Differences.Single(d => d.Identity.Kind == "Table" && d.Identity.ObjectName == "Articolo")
            .Status.Should().Be(DifferenceStatus.Different);
    }

    private static async Task SeedAsync(SqlConnection c, CancellationToken ct)
    {
        await ExecAsync(c, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);
        await ExecAsync(c, "IF TYPE_ID('app.CodiceArticolo') IS NULL CREATE TYPE app.CodiceArticolo FROM nvarchar(20) NOT NULL;", ct);
        await ExecAsync(c, "IF TYPE_ID('app.MioIntTipo') IS NULL CREATE TYPE app.MioIntTipo FROM bigint NOT NULL;", ct);
        await ExecAsync(c, """
            IF OBJECT_ID('dbo.Articolo','U') IS NULL
                CREATE TABLE dbo.Articolo (Id int NOT NULL, Codice app.CodiceArticolo NOT NULL);
            """, ct);
        await ExecAsync(c, """
            IF TYPE_ID('dbo.RigheTvp') IS NULL
                CREATE TYPE dbo.RigheTvp AS TABLE (Id int NOT NULL, Codice app.CodiceArticolo NOT NULL);
            """, ct);
        await ExecAsync(c, """
            IF OBJECT_ID('dbo.SeqArticolo','SO') IS NULL
                CREATE SEQUENCE dbo.SeqArticolo AS app.MioIntTipo START WITH 1 INCREMENT BY 1;
            """, ct);
        // The negative control lives in the fixture: without a built-in beside
        // the alias one, dropping the `CASE WHEN ty.is_user_defined = 1` guard
        // from any reader would make every built-in report schema 'sys' — every
        // sequence `AS [sys].[bigint]` — and nothing would go red.
        await ExecAsync(c, """
            IF OBJECT_ID('dbo.SeqPlain','SO') IS NULL
                CREATE SEQUENCE dbo.SeqPlain AS bigint START WITH 1 INCREMENT BY 1;
            """, ct);
    }

    private async Task<string> FreshDbAsync(string name, CancellationToken ct)
    {
        await using (SqlConnection bootstrap = new(fixture.ConnectionString))
        {
            await bootstrap.OpenAsync(ct);
            await ExecAsync(bootstrap, $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", ct);
            await ExecAsync(bootstrap, $"CREATE DATABASE [{name}];", ct);
        }

        return new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    private static async Task ExecAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using SqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
