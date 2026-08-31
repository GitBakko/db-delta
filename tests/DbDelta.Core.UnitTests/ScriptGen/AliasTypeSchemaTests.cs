using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using DbDelta.Core.ScriptGen;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ScriptGen;

/// <summary>
/// An ALIAS type outside <c>dbo</c> must be written schema-qualified, or the
/// deploy stops with Msg 2715 "Cannot find data type".
/// </summary>
/// <remarks>
/// <para>
/// Measured on <c>mssql/server:2022-latest</c> with real logins whose
/// DEFAULT_SCHEMA differed: SQL Server resolves an unqualified type name for a
/// table or table-type column against the caller's default schema first, then
/// <c>dbo</c>. So a type in <c>dbo</c> resolves for everyone and keeps its bare
/// name — which is why not one existing assertion moves — while a type in any
/// other schema is invisible to a <c>dbo</c>-default caller.
/// </para>
/// <para>
/// The schema travels in <see cref="Column.TypeSchema"/> and NOT inside
/// <c>DataType</c>: <c>SqlTypeFormatter</c> bracket-quotes the whole pre-paren
/// token at once, so a dotted <c>DataType</c> comes out as the single
/// identifier <c>[app.CodiceArticolo]</c> — the shape S11 removed and
/// <c>IdentifierEscapingTests</c> still pins.
/// </para>
/// </remarks>
public class AliasTypeSchemaTests
{
    private static Column Alias(string name, string type, string? schema, int ordinal = 2) =>
        new(name, type, isNullable: false, ordinal: ordinal) { IsUserDefinedType = true, TypeSchema = schema };

    private static Table Tbl(params Column[] cols) => new("vendite", "Articolo", cols);

    private static Database Db(Table? t = null, Sequence? seq = null) =>
        new("X", [new Schema("vendite"), new Schema("app")], t is null ? [] : [t])
        { Sequences = seq is null ? [] : [seq] };

    private static Database DbC(string collation, Table t) =>
        new("X", [new Schema("vendite"), new Schema("app")], [t]) { DefaultCollation = collation };

    private static Database DbTt(TableTypeUdt tt) =>
        new("X", [new Schema("dbo"), new Schema("app")], []) { TableTypeUdts = [tt] };

    private static DifferenceStatus StatusOf(Database a, Database b, string kind) =>
        new ComparisonEngine().Compare(a, b, ComparisonOptions.Default)
            .Differences.Single(d => d.Identity.Kind == kind).Status;

    private static string Create(Table t) =>
        new TableScriptEmitter().Emit(new DifferencePair(t.Identity, DifferenceStatus.OnlyInA, t, null));

    // ── the defect ────────────────────────────────────────────────────────

    [Fact]
    public void A_column_of_an_alias_type_outside_dbo_is_written_schema_qualified()
    {
        Create(Tbl(Alias("Codice", "CodiceArticolo", "app")))
            .Should().Contain("[Codice] [app].[CodiceArticolo] NOT NULL");
    }

    [Fact]
    public void A_table_type_column_of_an_alias_type_outside_dbo_is_written_schema_qualified()
    {
        TableTypeUdt tt = new("dbo", "RigheTvp", [Alias("Codice", "CodiceArticolo", "app", 1)]);

        new TableTypeUdtScriptEmitter().EmitCreate(tt)
            .Should().Contain("[Codice] [app].[CodiceArticolo] NOT NULL");
    }

    [Fact]
    public void A_sequence_over_an_alias_type_outside_dbo_is_written_schema_qualified()
    {
        Sequence s = new("app", "S1", "MioIntTipo", 1, 1, null, null, false, true, null) { TypeSchema = "app" };

        new SequenceScriptEmitter().EmitCreate(s).Should().Contain("AS [app].[MioIntTipo]");
    }

    [Fact]
    public void The_in_place_retype_path_qualifies_too()
    {
        // ALTER TABLE … ALTER COLUMN is a second sink, separate from the column
        // list of CREATE TABLE: it calls the formatter on its own.
        Table before = Tbl(Alias("Codice", "CodiceArticolo", "app"));
        Table after = Tbl(Alias("Codice", "CodiceArticoloLungo", "app"));

        new TableScriptEmitter()
            .Emit(new DifferencePair(before.Identity, DifferenceStatus.Different, after, before))
            .Should().Contain("ALTER COLUMN [Codice] [app].[CodiceArticoloLungo]");
    }

    // ── the silence guard: the field must reach the COMPARISON ────────────
    //
    // Without these, source app.CodiceArticolo and target dbo.CodiceArticolo
    // both carry DataType "CodiceArticolo", compare Identical, produce no
    // difference row and emit nothing. Silence instead of a wrong script.

    [Fact]
    public void Two_tables_whose_column_differs_only_by_the_types_schema_are_Different()
    {
        Table a = Tbl(Alias("Codice", "CodiceArticolo", "app"));
        Table b = Tbl(Alias("Codice", "CodiceArticolo", "dbo"));

        StatusOf(Db(a), Db(b), "Table").Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Two_table_types_whose_column_differs_only_by_the_types_schema_are_Different()
    {
        TableTypeUdt a = new("dbo", "RigheTvp", [Alias("Codice", "CodiceArticolo", "app", 1)]);
        TableTypeUdt b = new("dbo", "RigheTvp", [Alias("Codice", "CodiceArticolo", "dbo", 1)]);

        // Through the engine: TableTypeComparison is internal, and the engine is
        // the seam a real comparison actually crosses.
        StatusOf(DbTt(a), DbTt(b), "TableType").Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void Two_sequences_that_differ_only_by_the_types_schema_are_Different()
    {
        Sequence a = new("app", "S1", "MioIntTipo", 1, 1, null, null, false, true, null) { TypeSchema = "app" };
        Sequence b = a with { TypeSchema = "dbo" };

        StatusOf(Db(seq: a), Db(seq: b), "Sequence").Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void A_sequence_whose_type_schema_changed_cannot_be_ALTERed()
    {
        // SQL Server cannot ALTER a sequence's base type; the emitter signals
        // that with null so the caller falls back to DROP + CREATE. A schema
        // change is a type change.
        Sequence source = new("app", "S1", "MioIntTipo", 1, 1, null, null, false, true, null) { TypeSchema = "app" };
        Sequence target = source with { TypeSchema = "dbo" };

        new SequenceScriptEmitter().EmitAlter(source, target).Should().BeNull();
    }

    // ── negative controls ────────────────────────────────────────────────

    [Fact]
    public void An_alias_type_in_dbo_keeps_its_bare_name()
    {
        // The reason no golden file and no existing assertion moves: an
        // unqualified name resolves against dbo for every caller, measured.
        Create(Tbl(Alias("Codice", "CodiceArticolo", "dbo")))
            .Should().Contain("[Codice] [CodiceArticolo] NOT NULL")
            .And.NotContain("[dbo].[CodiceArticolo]");
    }

    [Fact]
    public void A_column_with_no_type_schema_at_all_keeps_its_bare_name()
    {
        // Every Column built by hand rather than read from a catalog — which is
        // every column in the golden corpus.
        Create(Tbl(new Column("Codice", "CodiceArticolo", isNullable: false, ordinal: 2) { IsUserDefinedType = true }))
            .Should().Contain("[Codice] [CodiceArticolo] NOT NULL");
    }

    [Fact]
    public void A_built_in_type_is_never_qualified()
    {
        Create(Tbl(new Column("Nome", "nvarchar(100)", isNullable: false, ordinal: 2)))
            .Should().Contain("[Nome] [nvarchar] (100)").And.NotContain("].[nvarchar]");
    }

    [Fact]
    public void A_sequence_over_a_built_in_type_is_still_unbracketed()
    {
        // CREATE SEQUENCE has always said "AS bigint", never "AS [bigint]".
        // Bracketing it would be churn on every sequence ever emitted.
        Sequence s = new("dbo", "S1", "bigint", 1, 1, null, null, false, true, null);

        new SequenceScriptEmitter().EmitCreate(s).Should().Contain("AS bigint").And.NotContain("[bigint]");
    }

    [Fact]
    public void Two_columns_of_the_same_alias_type_in_the_same_schema_stay_Identical()
    {
        Table a = Tbl(Alias("Codice", "CodiceArticolo", "app"));
        Table b = Tbl(Alias("Codice", "CodiceArticolo", "app"));

        StatusOf(Db(a), Db(b), "Table").Should().Be(DifferenceStatus.Identical);
    }

    [Fact]
    public void A_column_that_changed_only_its_types_schema_still_gets_an_ALTER_COLUMN()
    {
        // ColumnRequiresAlterColumn and ColumnShapeEqual are two comparisons of
        // their own, and neither is reached by the engine's verdict: revert the
        // schema half of either and the pair is still reported Different while
        // the script comes out EMPTY. A Different row with nothing to deploy is
        // the same silence one step later.
        Table before = Tbl(Alias("Codice", "CodiceArticolo", "dbo"));
        Table after = Tbl(Alias("Codice", "CodiceArticolo", "app"));

        new TableScriptEmitter()
            .Emit(new DifferencePair(before.Identity, DifferenceStatus.Different, after, before))
            .Should().Contain("ALTER COLUMN [Codice] [app].[CodiceArticolo]");
    }

    [Fact]
    public void A_case_only_difference_in_the_type_is_Different_on_a_case_sensitive_target()
    {
        // Both halves are server identifiers, and a CS/BIN database holds
        // app.Codice and app.codice as two distinct types — user_type_id 257
        // and 258, measured. The engine pairs the objects around this call with
        // the target's collation, so the type comparison has to fold case the
        // same way or it disagrees with the table it sits inside — silently.
        Column a = Alias("K", "Codice", "app");
        Column b = Alias("K", "codice", "app");

        a.TypeMatches(b, StringComparer.Ordinal).Should().BeFalse();
        a.TypeMatches(b, StringComparer.OrdinalIgnoreCase).Should().BeTrue();
    }

    [Fact]
    public void On_a_case_sensitive_target_two_types_spelled_alike_stay_Different_through_the_engine()
    {
        // The seam above proves Column.TypeMatches honours the comparer it is
        // handed. This proves ComparisonEngine actually HANDS it the target's
        // one: pass OrdinalIgnoreCase at that call site instead and the two
        // types below fold together, the table reads Identical and the row
        // disappears — the failure is silence, so nothing else would show it.
        Table a = Tbl(Alias("K", "Codice", "app"));
        Table b = Tbl(Alias("K", "codice", "app"));

        new ComparisonEngine()
            .Compare(DbC("SQL_Latin1_General_CP1_CS_AS", a), DbC("SQL_Latin1_General_CP1_CS_AS", b),
                     ComparisonOptions.Default)
            .Differences.Single(d => d.Identity.Kind == "Table").Status
            .Should().Be(DifferenceStatus.Different);
    }

    [Fact]
    public void A_sequence_over_a_dbo_alias_type_is_still_bracket_quoted()
    {
        // The name is an arbitrary user identifier, not a keyword: unquoted it
        // does not even parse, and a name holding ']' is the S11 injection
        // shape. Only BUILT-IN base types stay bare.
        Sequence s = new("dbo", "S1", "Ordine Riga", 1, 1, null, null, false, true, null) { TypeSchema = "dbo" };

        new SequenceScriptEmitter().EmitCreate(s).Should().Contain("AS [Ordine Riga]");
    }

    [Fact]
    public void A_qualified_alias_column_still_gets_no_collation()
    {
        // The two branches meet in FormatColumn: the type is qualified, and the
        // COLLATE the catalog reports for it is still suppressed. A real alias
        // column always carries both — sys.columns reports a collation for
        // app.CodiceArticolo exactly as for an nvarchar.
        Create(Tbl(new Column("Codice", "CodiceArticolo", isNullable: false, ordinal: 2,
            collation: "Latin1_General_CI_AS")
        { IsUserDefinedType = true, TypeSchema = "app" }))
            .Should().Contain("[Codice] [app].[CodiceArticolo] NOT NULL").And.NotContain("COLLATE");
    }
}
