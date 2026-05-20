using Bunit;
using DbDelta.App.Components;
using DbDelta.Shared.Dtos;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.ComponentTests;

public class ResultsTreeTests : Bunit.TestContext
{
    [Fact]
    public void Renders_no_comparison_message_when_Result_null()
    {
        IRenderedComponent<ResultsTree> cut = RenderComponent<ResultsTree>(
            p => p.Add(r => r.Result, null));

        cut.Markup.Should().Contain("No comparison run yet");
    }

    [Fact]
    public void Renders_dgrid_rows_with_design_system_classes()
    {
        ComparisonResultDto dto = new(
        [
            new DifferenceDto("Table", "dbo", "Customer",   "OnlyInA"),
            new DifferenceDto("Table", "dbo", "Order",      "Different"),
            new DifferenceDto("Table", "dbo", "Legacy",     "OnlyInB"),
            new DifferenceDto("Table", "dbo", "Identical1", "Identical"),
        ]);

        IRenderedComponent<ResultsTree> cut = RenderComponent<ResultsTree>(
            p => p.Add(r => r.Result, dto));

        // Object names rendered
        cut.Markup.Should().Contain("Customer");
        cut.Markup.Should().Contain("Order");
        cut.Markup.Should().Contain("Legacy");
        cut.Markup.Should().Contain("Identical1");

        // Design System classes wired correctly
        cut.Markup.Should().Contain("class=\"dgrid\"");
        cut.Markup.Should().Contain("data-diff=\"only-source\"");
        cut.Markup.Should().Contain("data-diff=\"modified\"");
        cut.Markup.Should().Contain("data-diff=\"only-target\"");
        cut.Markup.Should().Contain("data-diff=\"identical\"");

        // Badge variants
        cut.Markup.Should().Contain("badge--info");
        cut.Markup.Should().Contain("badge--warning");
    }
}
