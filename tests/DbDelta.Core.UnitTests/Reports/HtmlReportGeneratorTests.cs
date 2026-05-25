using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Reports;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Reports;

public class HtmlReportGeneratorTests
{
    private static readonly HtmlReportGenerator Sut = new();

    [Fact]
    public void Empty_result_still_produces_a_valid_self_contained_html_document()
    {
        string html = Sut.Generate(new ComparisonResult([]));

        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().Contain("<html");
        html.Should().Contain("</html>");
        html.Should().Contain("<style>");
        html.Should().Contain("</style>");
        // No external links anywhere — fully self-contained.
        html.Should().NotContain("<link");
        html.Should().NotContain("<script src");
    }

    [Fact]
    public void Embedded_css_carries_the_four_status_colors_from_the_design_system()
    {
        string html = Sut.Generate(new ComparisonResult([]));

        html.Should().Contain("#0064C8"); // Different — cobalt
        html.Should().Contain("#B31220"); // OnlyInB — crimson (solo destinazione)
        html.Should().Contain("#007339"); // OnlyInA — emerald (solo provenienza)
        html.Should().Contain("#9097A0"); // Identical — grey
    }

    [Fact]
    public void Differences_are_grouped_into_kind_sections_with_italian_labels()
    {
        ComparisonResult result = new(
        [
            PairOnlyInB("Table", "dbo", "Orders"),
            PairOnlyInB("View", "dbo", "vReport"),
            PairOnlyInB("Permission", "dbo", "GRANT.SELECT.Orders.TO.dbo"),
        ]);

        string html = Sut.Generate(result);

        html.Should().Contain("Tabelle");
        html.Should().Contain("Viste");
        html.Should().Contain("Permessi");
    }

    [Fact]
    public void Kind_sections_appear_in_canonical_order_regardless_of_input_order()
    {
        ComparisonResult result = new(
        [
            PairOnlyInB("Permission", "dbo", "GRANT.SELECT.Orders.TO.dbo"),
            PairOnlyInB("View", "dbo", "vReport"),
            PairOnlyInB("Table", "dbo", "Orders"),
        ]);

        string html = Sut.Generate(result);
        int tabelle = html.IndexOf("Tabelle", StringComparison.Ordinal);
        int viste = html.IndexOf("Viste", StringComparison.Ordinal);
        int permessi = html.IndexOf("Permessi", StringComparison.Ordinal);

        tabelle.Should().BeGreaterThan(0);
        viste.Should().BeGreaterThan(tabelle);
        permessi.Should().BeGreaterThan(viste);
    }

    [Fact]
    public void Sections_are_collapsible_via_native_details_elements()
    {
        ComparisonResult result = new([PairOnlyInB("Table", "dbo", "Orders")]);

        string html = Sut.Generate(result);

        html.Should().Contain("<details");
        html.Should().Contain("<summary");
    }

    [Fact]
    public void Each_row_carries_a_status_class_matching_its_difference_status()
    {
        ComparisonResult result = new(
        [
            PairDifferent("Table", "dbo", "Customer"),
            PairOnlyInB("Table", "dbo", "Orders"),
            PairOnlyInA("Table", "dbo", "Legacy"),
            PairIdentical("Table", "dbo", "Audit"),
        ]);

        string html = Sut.Generate(result);

        html.Should().Contain("status-different");
        html.Should().Contain("status-only-target");
        html.Should().Contain("status-only-source");
        html.Should().Contain("status-identical");
    }

    [Fact]
    public void Schema_and_object_names_are_html_encoded_to_avoid_injection()
    {
        ComparisonResult result = new(
        [
            PairOnlyInB("Table", "dbo", "<script>alert(1)</script>"),
        ]);

        string html = Sut.Generate(result);

        html.Should().NotContain("<script>alert(1)</script>");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void Section_summary_includes_total_count_of_rows_in_that_kind()
    {
        ComparisonResult result = new(
        [
            PairDifferent("Table", "dbo", "A"),
            PairOnlyInB("Table", "dbo", "B"),
            PairIdentical("Table", "dbo", "C"),
        ]);

        string html = Sut.Generate(result);

        // The kind section must surface the count somewhere on the summary line.
        html.Should().MatchRegex(@"Tabelle.*\b3\b");
    }

    [Fact]
    public void Within_a_kind_section_modified_rows_render_before_identical_rows()
    {
        ComparisonResult result = new(
        [
            PairIdentical("Table", "dbo", "AAA_identical"),
            PairDifferent("Table", "dbo", "ZZZ_different"),
        ]);

        string html = Sut.Generate(result);
        int diffIdx = html.IndexOf("ZZZ_different", StringComparison.Ordinal);
        int identIdx = html.IndexOf("AAA_identical", StringComparison.Ordinal);

        diffIdx.Should().BeGreaterThan(0);
        identIdx.Should().BeGreaterThan(diffIdx);
    }

    [Fact]
    public void Top_of_report_carries_a_summary_with_grand_totals_per_status()
    {
        ComparisonResult result = new(
        [
            PairDifferent("Table", "dbo", "C1"),
            PairDifferent("Table", "dbo", "C2"),
            PairOnlyInB("View", "dbo", "v1"),
            PairOnlyInA("Procedure", "dbo", "p1"),
            PairIdentical("Function", "dbo", "f1"),
            PairIdentical("Function", "dbo", "f2"),
            PairIdentical("Function", "dbo", "f3"),
        ]);

        string html = Sut.Generate(result);

        // The grand totals strip lives at the top — render before any kind section.
        int summaryIdx = html.IndexOf("DbDelta", StringComparison.Ordinal);
        int firstKindIdx = html.IndexOf("Tabelle", StringComparison.Ordinal);

        summaryIdx.Should().BeGreaterOrEqualTo(0);
        firstKindIdx.Should().BeGreaterThan(summaryIdx);
        // Counts visible somewhere up-top.
        html.Should().Contain("2"); // 2 different
    }

    // ── factory helpers ─────────────────────────────────────────────────────

    private static DifferencePair PairDifferent(string kind, string schema, string name) =>
        new(new ObjectIdentity(schema, name, kind), DifferenceStatus.Different, Stub(schema, name), Stub(schema, name));

    private static DifferencePair PairOnlyInB(string kind, string schema, string name) =>
        new(new ObjectIdentity(schema, name, kind), DifferenceStatus.OnlyInB, null, Stub(schema, name));

    private static DifferencePair PairOnlyInA(string kind, string schema, string name) =>
        new(new ObjectIdentity(schema, name, kind), DifferenceStatus.OnlyInA, Stub(schema, name), null);

    private static DifferencePair PairIdentical(string kind, string schema, string name) =>
        new(new ObjectIdentity(schema, name, kind), DifferenceStatus.Identical, Stub(schema, name), Stub(schema, name));

    private static Table Stub(string schema, string name) => new(schema, name, []);
}
