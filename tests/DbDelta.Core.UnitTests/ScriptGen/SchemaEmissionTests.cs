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
    public void Schemas_present_on_both_sides_produce_no_row()
    {
        // A schema is modelled by its name alone, so a pair that matches by
        // identity can never be anything but equal — the row would carry no
        // information and dbo would show up in every comparison ever run.
        Database a = Db([new Schema("dbo"), new Schema("sales")]);
        Database b = Db([new Schema("dbo"), new Schema("sales")]);

        ComparisonResult r = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        r.Differences.Should().NotContain(p => p.Identity.Kind == "Schema");
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
