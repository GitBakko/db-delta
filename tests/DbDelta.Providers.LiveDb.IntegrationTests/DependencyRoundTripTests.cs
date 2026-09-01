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
            // An INDEXED view: the index is what makes it a stored result set,
            // and it was invisible to the comparison until 2026-08-20.
            await Exec(c, "CREATE OR ALTER VIEW app.vTotali WITH SCHEMABINDING AS SELECT Codice, COUNT_BIG(*) AS N FROM app.Articolo GROUP BY Codice;", ct);
            await Exec(c, "IF INDEXPROPERTY(OBJECT_ID('app.vTotali'), 'IX_vTotali', 'IndexID') IS NULL CREATE UNIQUE CLUSTERED INDEX IX_vTotali ON app.vTotali (Codice);", ct);
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

    /// <summary>
    /// A sequence declared over an alias type, both ways: created after its
    /// type and dropped before it. Both orders were wrong at once, and the
    /// second one had been asserted correct from reasoning rather than
    /// measurement.
    /// </summary>
    /// <remarks>
    /// The refusals are asserted FIRST, against the same server, because
    /// without them this test passes just as well against a generator that got
    /// lucky: it would be proving the target accepts a script rather than that
    /// the order is what makes it acceptable. CREATE is Msg 243 and DROP is
    /// Msg 3732 — two different numbers for one missing ordering, which is part
    /// of why they were never recognised as the same defect.
    /// </remarks>
    [Fact]
    public async Task A_sequence_over_an_alias_type_is_ordered_against_it_in_both_directions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "SeqTypeSrc", ct);
        await Create(fixture.ConnectionString, "SeqTypeTgt", ct);
        string src = Cat(fixture.ConnectionString, "SeqTypeSrc");
        string tgt = Cat(fixture.ConnectionString, "SeqTypeTgt");

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await Exec(c, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);
            await Exec(c, "IF TYPE_ID('app.AliasInt') IS NULL CREATE TYPE app.AliasInt FROM bigint NOT NULL;", ct);
            await Exec(c, "IF OBJECT_ID('app.SeqC','SO') IS NULL CREATE SEQUENCE app.SeqC AS app.AliasInt START WITH 1 INCREMENT BY 1;", ct);
        }

        // ── the mechanism, before the verdict ────────────────────────────
        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            await Exec(c, "IF SCHEMA_ID('app') IS NULL EXEC('CREATE SCHEMA app');", ct);

            Func<Task> seqBeforeType = () => Exec(c,
                "CREATE SEQUENCE app.SondaOrdine AS app.NonEsiste START WITH 1 INCREMENT BY 1;", ct);
            (await seqBeforeType.Should().ThrowAsync<SqlException>())
                .Which.Number.Should().Be(243, "CREATE SEQUENCE before its alias type is Msg 243, not the 2715 every other binding gives");

            await Exec(c, "IF TYPE_ID('app.Sonda') IS NULL CREATE TYPE app.Sonda FROM bigint NOT NULL;", ct);
            await Exec(c, "IF OBJECT_ID('app.SondaSeq','SO') IS NULL CREATE SEQUENCE app.SondaSeq AS app.Sonda START WITH 1 INCREMENT BY 1;", ct);
            Func<Task> typeBeforeSeq = () => Exec(c, "DROP TYPE app.Sonda;", ct);
            (await typeBeforeSeq.Should().ThrowAsync<SqlException>())
                .Which.Number.Should().Be(3732, "DROP TYPE while a sequence still binds it is Msg 3732");
            await Exec(c, "DROP SEQUENCE app.SondaSeq; DROP TYPE app.Sonda;", ct);
        }

        // ── CREATE direction ─────────────────────────────────────────────
        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        string createScript = new ScriptGenerator().Generate(
            new ComparisonEngine().Compare(source, target, ComparisonOptions.Default),
            selection: null, options: ComparisonOptions.Default, dependencies: source.Dependencies);

        SqlBatchResult up = await SqlExecutor.ExecuteAsync(tgt, createScript, ct, useOwnTransaction: false);
        up.Success.Should().BeTrue(up.ErrorMessage ?? "the sequence was created before the type it is declared over");

        // ── DROP direction, on the database the CREATE just built ────────
        Database now = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        Database empty = (await new LiveDbSource(src).LoadAsync(ct)).Value! with
        {
            Sequences = [],
            UserDefinedTypes = [],
        };
        string dropScript = new ScriptGenerator().Generate(
            new ComparisonEngine().Compare(empty, now, ComparisonOptions.Default),
            selection: null, options: ComparisonOptions.Default,
            dependencies: empty.Dependencies, dropDependencies: now.Dependencies);

        SqlBatchResult down = await SqlExecutor.ExecuteAsync(tgt, dropScript, ct, useOwnTransaction: false);
        down.Success.Should().BeTrue(down.ErrorMessage ?? "the type was dropped while the sequence still bound it");

        Database after = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        after.Sequences.Should().NotContain(x => x.Name == "SeqC");
        after.UserDefinedTypes.Should().NotContain(x => x.Name == "AliasInt");
    }

    /// <summary>
    /// A rebuilt table leaves every plain view that reads it holding the column
    /// list it cached at CREATE time, and the view keeps answering SELECTs, so
    /// nothing looks wrong. The generated script now refreshes them.
    /// </summary>
    /// <remarks>
    /// The mechanism is asserted first, in a scratch database, by running the
    /// rebuild dance by hand: without a refresh the view really does report the
    /// old type while the base reports the new one. Without that half the test
    /// would pass against a generator that emitted nothing, since a freshly
    /// deployed view is fresh by construction.
    /// </remarks>
    [Fact]
    public async Task A_plain_view_over_a_rebuilt_table_does_not_keep_the_old_column_types()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "RefreshMech", ct);
        await Create(fixture.ConnectionString, "RefreshSrc", ct);
        await Create(fixture.ConnectionString, "RefreshTgt", ct);
        string mech = Cat(fixture.ConnectionString, "RefreshMech");
        string src = Cat(fixture.ConnectionString, "RefreshSrc");
        string tgt = Cat(fixture.ConnectionString, "RefreshTgt");

        // ── the mechanism: the dance by hand, no refresh ──────────────────
        await using (SqlConnection c = new(mech))
        {
            await c.OpenAsync(ct);
            await Exec(c, "IF OBJECT_ID('dbo.M','U') IS NULL CREATE TABLE dbo.M (Id int NOT NULL, Nota nvarchar(50) NULL);", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vM AS SELECT Id, Nota FROM dbo.M;", ct);
            await Exec(c, """
                CREATE TABLE dbo.M_tmp (Id bigint IDENTITY(1,1) NOT NULL, Nota nvarchar(50) NULL);
                SET IDENTITY_INSERT dbo.M_tmp ON;
                INSERT INTO dbo.M_tmp (Id, Nota) SELECT Id, Nota FROM dbo.M;
                SET IDENTITY_INSERT dbo.M_tmp OFF;
                DROP TABLE dbo.M;
                EXEC sp_rename 'dbo.M_tmp', 'M';
                """, ct);

            (await ColumnTypeAsync(c, "dbo.M", "Id", ct)).Should().Be("bigint", "the base table really was rebuilt");
            (await ColumnTypeAsync(c, "dbo.vM", "Id", ct)).Should().Be(
                "int", "this is the silence: the view still reports the OLD type and still SELECTs cleanly");
        }

        // ── the verdict: DbDelta's own script ─────────────────────────────
        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await Exec(c, "IF OBJECT_ID('dbo.Ordine','U') IS NULL CREATE TABLE dbo.Ordine (Id bigint IDENTITY(1,1) NOT NULL, Nota nvarchar(50) NULL);", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vOrdine AS SELECT Id, Nota FROM dbo.Ordine;", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vOrdineEsterna AS SELECT Id, Nota FROM dbo.vOrdine;", ct);
        }
        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            // Same shape, but Id is a plain int: the identity flip forces the
            // rebuild and the widening is what the views then get wrong.
            await Exec(c, "IF OBJECT_ID('dbo.Ordine','U') IS NULL CREATE TABLE dbo.Ordine (Id int NOT NULL, Nota nvarchar(50) NULL);", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vOrdine AS SELECT Id, Nota FROM dbo.Ordine;", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vOrdineEsterna AS SELECT Id, Nota FROM dbo.vOrdine;", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;
        string script = new ScriptGenerator().Generate(
            new ComparisonEngine().Compare(source, target, ComparisonOptions.Default),
            selection: null, options: ComparisonOptions.Default, dependencies: source.Dependencies);

        script.Should().Contain("sp_refreshsqlmodule", "the rebuild has to carry the refresh with it");

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "the rebuild script failed to apply");

        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            (await ColumnTypeAsync(c, "dbo.Ordine", "Id", ct)).Should().Be("bigint");
            (await ColumnTypeAsync(c, "dbo.vOrdine", "Id", ct)).Should().Be("bigint", "the direct view was refreshed");
            (await ColumnTypeAsync(c, "dbo.vOrdineEsterna", "Id", ct)).Should().Be(
                "bigint", "and the view over that view too — refreshing the inner one alone does not reach it");
        }
    }

    private static async Task<string> ColumnTypeAsync(SqlConnection c, string obj, string col, CancellationToken ct)
    {
        await using SqlCommand cmd = new(
            "SELECT ty.name FROM sys.columns AS cl"
            + " INNER JOIN sys.types AS ty ON ty.user_type_id = cl.user_type_id"
            + " WHERE cl.object_id = OBJECT_ID(@obj) AND cl.name = @col;", c);
        cmd.Parameters.AddWithValue("@obj", obj);
        cmd.Parameters.AddWithValue("@col", col);
        return (string)await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// A SCHEMABINDING module over a table that needs an identity rebuild: the
    /// server refuses the DROP TABLE with Msg 3729, XACT_ABORT rolls the whole
    /// deploy back, and the target is left divergent. DbDelta now answers first
    /// and names the module.
    /// </summary>
    /// <remarks>
    /// This is the shape NO test in the tree had, and the parity fixture cannot
    /// grow it by accident: scenarios 03 and 12 rebuild tables no module
    /// references, and 16/17 put SCHEMABINDING on tables that are never rebuilt.
    /// The second half is the negative control that matters most, because it
    /// runs on the REAL reader output rather than a hand-built edge: an ordinary
    /// CHECK constraint makes sys.sql_expression_dependencies report
    /// is_schema_bound_reference = 1 against the table ITSELF, and that table
    /// rebuilds perfectly well.
    /// </remarks>
    [Fact]
    public async Task A_schemabound_module_over_a_rebuilt_table_is_refused_by_name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "SbSrc", ct);
        await Create(fixture.ConnectionString, "SbTgt", ct);
        string src = Cat(fixture.ConnectionString, "SbSrc");
        string tgt = Cat(fixture.ConnectionString, "SbTgt");

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await Exec(c, "IF OBJECT_ID('dbo.Ordini','U') IS NULL CREATE TABLE dbo.Ordini (Id bigint IDENTITY(1,1) NOT NULL, Amt decimal(9,2) NOT NULL);", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vOrdiniSb WITH SCHEMABINDING AS SELECT Id, Amt FROM dbo.Ordini;", ct);
        }
        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            // Plain int, not IDENTITY: the flip is what forces the rebuild.
            await Exec(c, "IF OBJECT_ID('dbo.Ordini','U') IS NULL CREATE TABLE dbo.Ordini (Id bigint NOT NULL, Amt decimal(9,2) NOT NULL);", ct);
            await Exec(c, "CREATE OR ALTER VIEW dbo.vOrdiniSb WITH SCHEMABINDING AS SELECT Id, Amt FROM dbo.Ordini;", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;

        // The reader has to actually carry the flag, or the guard is decoration.
        target.Dependencies.Should().Contain(
            e => e.IsSchemaBound && e.Referenced.ObjectName == "Ordini" && e.Dependent.ObjectName == "vOrdiniSb");

        SchemaboundRebuildException ex = Assert.Throws<SchemaboundRebuildException>(() =>
            new ScriptGenerator().Generate(
                new ComparisonEngine().Compare(source, target, ComparisonOptions.Default),
                selection: null,
                options: ComparisonOptions.Default,
                dependencies: source.Dependencies,
                dropDependencies: target.Dependencies));

        ex.Table.ObjectName.Should().Be("Ordini");
        ex.Binder.ObjectName.Should().Be("vOrdiniSb");

        // And the refusal is not pessimism: the server really does refuse.
        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            Func<Task> drop = async () => await Exec(c, "DROP TABLE dbo.Ordini;", ct);
            (await drop.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(3729);
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL on real catalog data: a table whose own CHECK constraint
    /// produces a self-referencing schemabound row still rebuilds.
    /// </summary>
    [Fact]
    public async Task A_tables_own_CHECK_does_not_block_its_rebuild()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Create(fixture.ConnectionString, "SbCkSrc", ct);
        await Create(fixture.ConnectionString, "SbCkTgt", ct);
        string src = Cat(fixture.ConnectionString, "SbCkSrc");
        string tgt = Cat(fixture.ConnectionString, "SbCkTgt");

        await using (SqlConnection c = new(src))
        {
            await c.OpenAsync(ct);
            await Exec(c, "IF OBJECT_ID('dbo.Righe','U') IS NULL CREATE TABLE dbo.Righe (Id bigint IDENTITY(1,1) NOT NULL, Amt decimal(9,2) NOT NULL CONSTRAINT CK_Righe CHECK (Amt > 0));", ct);
        }
        await using (SqlConnection c = new(tgt))
        {
            await c.OpenAsync(ct);
            await Exec(c, "IF OBJECT_ID('dbo.Righe','U') IS NULL CREATE TABLE dbo.Righe (Id bigint NOT NULL, Amt decimal(9,2) NOT NULL CONSTRAINT CK_Righe CHECK (Amt > 0));", ct);
        }

        Database source = (await new LiveDbSource(src).LoadAsync(ct)).Value!;
        Database target = (await new LiveDbSource(tgt).LoadAsync(ct)).Value!;

        // The trap is real and it comes from the catalog, not from a fixture:
        // the CHECK's row is schemabound and points the table at itself.
        target.Dependencies.Should().Contain(
            e => e.IsSchemaBound
                 && e.Dependent.ObjectName == "Righe"
                 && e.Referenced.ObjectName == "Righe");

        string script = new ScriptGenerator().Generate(
            new ComparisonEngine().Compare(source, target, ComparisonOptions.Default),
            selection: null,
            options: ComparisonOptions.Default,
            dependencies: source.Dependencies,
            dropDependencies: target.Dependencies);

        SqlBatchResult apply = await SqlExecutor.ExecuteAsync(tgt, script, ct, useOwnTransaction: false);
        apply.Success.Should().BeTrue(apply.ErrorMessage ?? "an ordinary CHECK must not stop a rebuild");
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
