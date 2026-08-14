using System.Text.Json;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Shared.Dtos;
using DbDelta.Shared.Reports;
using FluentAssertions;
using Xunit;

namespace DbDelta.Shared.UnitTests.Reports;

public class JsonReportGeneratorTests
{
    private static readonly JsonReportGenerator Sut = new();

    [Fact]
    public void Empty_result_renders_an_empty_differences_array()
    {
        string json = Sut.Generate(new ComparisonResult([]));

        json.Should().Contain("\"differences\"");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("differences").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Differences_serialise_with_camelCase_property_names()
    {
        ComparisonResult result = new(
        [
            new DifferencePair(new ObjectIdentity("dbo", "Orders", "Table"),
                DifferenceStatus.OnlyInB, null, Stub("dbo", "Orders")),
        ]);

        string json = Sut.Generate(result);

        json.Should().Contain("\"differences\"");
        json.Should().Contain("\"kind\"");
        json.Should().Contain("\"schemaName\"");
        json.Should().Contain("\"objectName\"");
        json.Should().Contain("\"status\"");
        json.Should().NotContain("\"Kind\":");
        json.Should().NotContain("\"SchemaName\":");
    }

    [Fact]
    public void Round_trip_through_dto_preserves_all_difference_fields()
    {
        ComparisonResult result = new(
        [
            new DifferencePair(new ObjectIdentity("dbo", "Customer", "Table"),
                DifferenceStatus.Different, Stub("dbo", "Customer"), Stub("dbo", "Customer")),
            new DifferencePair(new ObjectIdentity("dbo", "vReport", "View"),
                DifferenceStatus.OnlyInA, StubView("dbo", "vReport"), null),
        ]);

        string json = Sut.Generate(result);
        ComparisonResultDto? dto = JsonSerializer.Deserialize<ComparisonResultDto>(
            json, JsonReportGenerator.DeserializerOptions);

        dto.Should().NotBeNull();
        dto!.Differences.Should().HaveCount(2);
        dto.Differences[0].Kind.Should().Be("Table");
        dto.Differences[0].SchemaName.Should().Be("dbo");
        dto.Differences[0].ObjectName.Should().Be("Customer");
        dto.Differences[0].Status.Should().Be(DifferenceStatus.Different.ToString());
        dto.Differences[1].Kind.Should().Be("View");
        dto.Differences[1].Status.Should().Be(DifferenceStatus.OnlyInA.ToString());
    }

    [Fact]
    public void Output_is_pretty_printed_so_humans_can_inspect_it()
    {
        ComparisonResult result = new(
        [
            new DifferencePair(new ObjectIdentity("dbo", "Orders", "Table"),
                DifferenceStatus.OnlyInB, null, Stub("dbo", "Orders")),
        ]);

        string json = Sut.Generate(result);

        json.Should().Contain("\n");
        json.Should().Contain("  "); // indentation
    }

    /// <summary>
    /// Structured for a pipeline that wants to gate on coverage, and summarised
    /// for the human reading the same file.
    /// </summary>
    [Fact]
    public void Unexamined_families_travel_in_the_json_report()
    {
        ComparisonResult result = new([])
        {
            Unexamined = new([new("INDEX_NON_ROWSTORE", 3)]),
        };

        string json = Sut.Generate(result);

        using var doc = JsonDocument.Parse(json);
        JsonElement group = doc.RootElement.GetProperty("unexamined").EnumerateArray().Single();
        group.GetProperty("key").GetString().Should().Be("INDEX_NON_ROWSTORE");
        group.GetProperty("count").GetInt32().Should().Be(3);
        group.GetProperty("label").GetString().Should().Contain("columnstore");
        doc.RootElement.GetProperty("unexaminedSummary").GetString().Should().Contain("13 tipologie");
    }

    /// <summary>A fully covered comparison reports an empty array and no sentence.</summary>
    [Fact]
    public void A_fully_covered_comparison_reports_no_unexamined_families()
    {
        using var doc = JsonDocument.Parse(Sut.Generate(new ComparisonResult([])));

        doc.RootElement.GetProperty("unexamined").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("unexaminedSummary").GetString().Should().BeEmpty();
    }

    private static Table Stub(string schema, string name) => new(schema, name, []);

    private static View StubView(string schema, string name) =>
        new(schema, name, "CREATE VIEW [" + schema + "].[" + name + "] AS SELECT 1 AS X;", IsEncrypted: false);
}
