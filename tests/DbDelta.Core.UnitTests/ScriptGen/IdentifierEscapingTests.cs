using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// S11 — catalog names are untrusted input. A name holding a closing bracket
/// used to terminate its own identifier, so everything after it became script
/// text executing with the deploy's privileges. The project already understood
/// this for the HTML report (see HtmlReportGeneratorTests); the SQL sink was
/// the one left raw.
/// </summary>
public class IdentifierEscapingTests
{
    private const string Nasty = "Ev]il";
    private const string Quoted = "[Ev]]il]";

    [Fact]
    public void Q_doubles_the_closing_bracket_like_QUOTENAME()
    {
        Sql.Q(Nasty).Should().Be(Quoted);
        Sql.Q("dbo", Nasty).Should().Be("[dbo].[Ev]]il]");
        Sql.Q("plain").Should().Be("[plain]", "a name with nothing to escape is untouched");
    }

    [Fact]
    public void A_table_name_holding_a_bracket_is_escaped_in_create_and_drop()
    {
        Table t = new("dbo", Nasty, [new Column("Id", "int", false, 1)]);

        string create = new TableScriptEmitter().Emit(
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));
        string drop = new TableScriptEmitter().Emit(
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInB, null, t));

        create.Should().Contain($"CREATE TABLE [dbo].{Quoted}");
        drop.Should().Be($"DROP TABLE [dbo].{Quoted};");
    }

    [Fact]
    public void Column_constraint_and_index_names_are_escaped_too()
    {
        Table t = new(
            Schema: "dbo",
            Name: "T",
            Columns: [new Column(Nasty, "int", false, 1)],
            Constraints: [new PrimaryKey(Nasty, [Nasty], IsClustered: true)],
            Indexes:
            [
                new TableIndex(Nasty, false, false, null, [new IndexColumn(Nasty, false)], [])
            ]);

        string create = new TableScriptEmitter().Emit(
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));
        string index = new IndexScriptEmitter().EmitCreate("dbo", "T", t.Indexes[0]);

        create.Should().Contain($"{Quoted} [int]");
        create.Should().Contain($"ADD CONSTRAINT {Quoted} PRIMARY KEY");
        index.Should().Contain($"INDEX {Quoted} ON [dbo].[T]");
        index.Should().Contain($"({Quoted} ASC)");
    }

    /// <summary>
    /// The type token was the one identifier left with an escape hatch: a name
    /// starting with <c>[</c> or holding a <c>.</c> was passed through unquoted,
    /// on the theory it was already qualified. Every producer in the repo hands
    /// over a bare <c>sys.types.name</c>, so the branch only ever fired for the
    /// input it must not: a type name carrying the punctuation.
    /// </summary>
    [Theory]
    [InlineData("Ev]il", "[Ev]]il]")]
    [InlineData("dbo.Money", "[dbo.Money]")]
    [InlineData("[preformatted]", "[[preformatted]]]")]
    public void A_column_type_name_is_quoted_whatever_punctuation_it_holds(string dataType, string expected)
    {
        Table t = new("dbo", "T", [new Column("C", dataType, true, 1)]);

        string create = new TableScriptEmitter().Emit(
            new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));

        create.Should().Contain($"[C] {expected}");
    }

    /// <summary>
    /// A CHECK constraint over a column whose name holds a <c>]</c>: the catalog
    /// writes it doubled, so a reader that stops at the first <c>]</c> saw a
    /// different column and left the constraint in place — Msg 5074 on the
    /// ALTER COLUMN it was supposed to unblock.
    /// </summary>
    [Fact]
    public void A_check_over_a_bracketed_column_is_dropped_before_that_column_is_retyped()
    {
        CheckConstraint ck = new("CK_T", $"({Quoted}>(0))", false, false);
        Table src = new("dbo", "T", [new Column(Nasty, "bigint", false, 1)], [ck], []);
        Table tgt = new("dbo", "T", [new Column(Nasty, "int", false, 1)], [ck], []);

        string sql = new TableScriptEmitter().Emit(
            new DifferencePair(src.Identity, DifferenceStatus.Different, src, tgt));

        sql.Should().Contain($"DROP CONSTRAINT [CK_T]");
        sql.Should().Contain($"ALTER COLUMN {Quoted} [bigint]");
        sql.Should().Contain($"ADD CONSTRAINT [CK_T] CHECK");
    }

    [Fact]
    public void The_module_header_round_trips_a_bracketed_name()
    {
        // AlignNameToCatalog rewrites a stale embedded name; Unquote reads the
        // embedded one back. Before the fix the reader did not collapse the
        // doubled bracket, so the two disagreed and the rewrite fired on a name
        // that was already correct.
        string body = $"CREATE VIEW {Sql.Q("dbo", Nasty)} AS SELECT 1 AS X;";

        string aligned = ModuleHeader.AlignNameToCatalog(body, "dbo", Nasty);

        aligned.Should().Be(body, "the embedded name already matches the catalog");
    }
}
