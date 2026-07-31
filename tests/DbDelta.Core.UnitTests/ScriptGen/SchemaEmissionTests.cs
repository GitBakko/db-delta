using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// Schemas were read by the provider and then discarded by the engine, so a
/// source-only schema was never reported and no CREATE SCHEMA was ever emitted.
/// Any object living in a schema the target lacked therefore killed the deploy
/// on its first statement with Msg 2760 ("The specified schema name ... either
/// does not exist or you do not have permission to use it").
/// </summary>
public class SchemaEmissionTests
{
    private static Database Db(IReadOnlyList<Schema> schemas, params Table[] tables) =>
        new("X", schemas, tables);

    private static Table T(string schema, string name) =>
        new(schema, name, [new Column("Id", "int", false, 1)]);

    [Fact]
    public void Source_only_schema_is_reported_as_a_difference()
    {
        Database a = Db([new Schema("dbo"), new Schema("sales")], T("sales", "Order"));
        Database b = Db([new Schema("dbo")]);

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        DifferencePair schemaPair = r.Differences
            .Should().ContainSingle(p => p.Identity.Kind == "Schema").Subject;
        schemaPair.Identity.SchemaName.Should().Be("sales");
        schemaPair.Status.Should().Be(DifferenceStatus.OnlyInA);
    }

    [Fact]
    public void Target_only_schema_is_reported_as_a_difference()
    {
        Database a = Db([new Schema("dbo")]);
        Database b = Db([new Schema("dbo"), new Schema("legacy")]);

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Should().ContainSingle(p => p.Identity.Kind == "Schema")
            .Which.Status.Should().Be(DifferenceStatus.OnlyInB);
    }

    [Fact]
    public void Only_the_schemas_that_differ_produce_a_row()
    {
        // A schema is modelled by its name alone, so a pair that matches by
        // identity can never be anything but equal — the row would carry no
        // information and dbo would show up in every comparison ever run.
        // The suppression is asserted in the SAME arrange as the two schemas
        // that DO differ: on its own, the negative half stayed green with
        // CompareSchemas deleted from the engine, so it could not catch a
        // refactor that removed the feature.
        Database a = Db([new Schema("dbo"), new Schema("sales")]);
        Database b = Db([new Schema("dbo"), new Schema("legacy")]);

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        IReadOnlyList<DifferencePair> schemaRows =
            [.. r.Differences.Where(p => p.Identity.Kind == "Schema")];
        schemaRows.Should().HaveCount(2, "sales and legacy differ, dbo matches");
        schemaRows.Should().ContainSingle(p =>
            p.Identity.SchemaName == "sales" && p.Status == DifferenceStatus.OnlyInA);
        schemaRows.Should().ContainSingle(p =>
            p.Identity.SchemaName == "legacy" && p.Status == DifferenceStatus.OnlyInB);
        schemaRows.Should().NotContain(p => p.Identity.SchemaName == "dbo");
    }

    [Fact]
    public void Create_schema_is_emitted_before_the_objects_that_live_in_it()
    {
        // The whole point: CREATE TABLE [sales].[Order] cannot run first.
        Table order = T("sales", "Order");
        Database a = Db([new Schema("dbo"), new Schema("sales")], order);
        Database b = Db([new Schema("dbo")]);
        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        string sql = new ScriptGenerator().Generate(r);

        int createSchema = sql.IndexOf("CREATE SCHEMA [sales];", StringComparison.Ordinal);
        int createTable = sql.IndexOf("CREATE TABLE [sales].[Order]", StringComparison.Ordinal);
        createSchema.Should().BeGreaterThan(0, "the schema must be created");
        createTable.Should().BeGreaterThan(createSchema, "the schema must exist before its objects");
    }

    [Fact]
    public void Create_schema_is_alone_in_its_batch()
    {
        // CREATE SCHEMA must be the first statement in its batch, so two
        // source-only schemas cannot share one.
        Database a = Db([new Schema("dbo"), new Schema("sales"), new Schema("report")]);
        Database b = Db([new Schema("dbo")]);
        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        string sql = new ScriptGenerator().Generate(r);

        string[] lines = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int firstIdx = Array.FindIndex(lines, l => l.Trim() == "CREATE SCHEMA [report];");
        int secondIdx = Array.FindIndex(lines, l => l.Trim() == "CREATE SCHEMA [sales];");
        firstIdx.Should().BeGreaterThan(0);
        secondIdx.Should().BeGreaterThan(firstIdx);
        // A GO separates them, so each is first in its own batch.
        lines[firstIdx..secondIdx].Should().Contain(l => l.Trim() == "GO");
    }

    [Fact]
    public void A_principal_is_dropped_after_the_schema_and_the_objects_it_could_own()
    {
        // DROP USER used to sit in the prologue while DROP SCHEMA sat at the end
        // of the script, so a user owning a target-only schema died on Msg 15138
        // ("The database principal owns a schema in the database, and cannot be
        // dropped"). Same for a user owning any object the DROP pass removes.
        // The CREATE half must stay in the prologue — asserted in the same
        // arrange, since moving both halves would be just as wrong.
        Table legacyTable = T("legacy", "OldOrder");
        DatabaseUser goingAway = new("legacy_owner", "S", "legacy_login", "legacy");
        DatabaseUser arriving = new("app_reader", "S", "app_login", "dbo");
        Table newTable = T("dbo", "Nuova");

        ComparisonResult r = new(
        [
            new DifferencePair(newTable.Identity, DifferenceStatus.OnlyInA, newTable, null),
            new DifferencePair(legacyTable.Identity, DifferenceStatus.OnlyInB, null, legacyTable),
            new DifferencePair(new Schema("legacy").Identity, DifferenceStatus.OnlyInB, null, new Schema("legacy")),
            new DifferencePair(goingAway.Identity, DifferenceStatus.OnlyInB, null, goingAway),
            new DifferencePair(arriving.Identity, DifferenceStatus.OnlyInA, arriving, null),
        ]);

        string sql = new ScriptGenerator().Generate(r);

        int dropTable = sql.IndexOf("DROP TABLE [legacy].[OldOrder];", StringComparison.Ordinal);
        int dropSchema = sql.IndexOf("DROP SCHEMA [legacy];", StringComparison.Ordinal);
        int dropUser = sql.IndexOf("DROP USER [legacy_owner];", StringComparison.Ordinal);
        int createUser = sql.IndexOf("CREATE USER [app_reader]", StringComparison.Ordinal);
        int createTable = sql.IndexOf("CREATE TABLE [dbo].[Nuova]", StringComparison.Ordinal);

        dropTable.Should().BeGreaterThan(0);
        dropSchema.Should().BeGreaterThan(dropTable);
        dropUser.Should().BeGreaterThan(dropSchema,
            "a principal owning the schema cannot be dropped before it");
        createUser.Should().BeGreaterThan(0).And.BeLessThan(createTable,
            "the CREATE half stays in the prologue — a new object may reference the new user");
    }

    [Fact]
    public void A_schema_the_selection_needs_is_created_even_when_its_row_is_not_ticked()
    {
        // The GUI shape. The user ticks the one table row; the Schema row for
        // `vendite` is a separate row they did not tick, so it never reaches the
        // generator's working set and the script opened with
        // CREATE TABLE [vendite].[Ordine] against a target with no `vendite`
        // schema — Msg 2760, the exact failure the Schema kind exists to
        // prevent. Same arrange asserts the promotion does not overreach: the
        // unrelated source-only `report` schema stays out.
        Table order = T("vendite", "Order");
        Database a = Db([new Schema("dbo"), new Schema("vendite"), new Schema("report")], order);
        Database b = Db([new Schema("dbo")]);
        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        DifferencePair tableOnly = r.Differences.Single(p => p.Identity.Kind == "Table");
        string script = DeployScriptBuilder.Build(
            r, [tableOnly], "src", "tgt", DateTime.UtcNow, [], []);

        int createSchema = script.IndexOf("CREATE SCHEMA [vendite];", StringComparison.Ordinal);
        int createTable = script.IndexOf("CREATE TABLE [vendite].[Order]", StringComparison.Ordinal);
        createSchema.Should().BeGreaterThan(0, "the selected table lives in it");
        createTable.Should().BeGreaterThan(createSchema);
        script.Should().NotContain("CREATE SCHEMA [report];", "nothing selected lives there");
    }

    [Fact]
    public void Drop_schema_is_emitted_after_the_objects_it_held()
    {
        Table legacyTable = T("legacy", "OldOrder");
        Database a = Db([new Schema("dbo")]);
        Database b = Db([new Schema("dbo"), new Schema("legacy")], legacyTable);
        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        string sql = new ScriptGenerator().Generate(r);

        int dropTable = sql.IndexOf("DROP TABLE [legacy].[OldOrder];", StringComparison.Ordinal);
        int dropSchema = sql.IndexOf("DROP SCHEMA [legacy];", StringComparison.Ordinal);
        dropTable.Should().BeGreaterThan(0);
        dropSchema.Should().BeGreaterThan(dropTable, "a schema cannot be dropped while it still holds objects");
    }
}
