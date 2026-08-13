using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Shared.Dtos;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// The results grid marks which side carries the more recent
/// <c>sys.objects.modify_date</c>, so the user can tell at a glance which way
/// they probably want to align.
/// </summary>
public class LastModifiedHighlightTests
{
    private static readonly DateTime s_older = new(2026, 1, 5, 8, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime s_newer = new(2026, 8, 12, 14, 22, 0, DateTimeKind.Unspecified);

    private static DifferenceRowViewModel Row(
        DateTime? source,
        DateTime? target,
        string status = "Different")
    {
        DifferenceDto dto = new(
            Kind: "Table",
            SchemaName: "dbo",
            ObjectName: "Orders",
            Status: status,
            LastModifiedSource: source,
            LastModifiedTarget: target);
        DifferencePair pair = new(
            Identity: new ObjectIdentity("dbo", "Orders", "Table"),
            Status: DifferenceStatus.Different,
            SideA: null,
            SideB: null);
        return new DifferenceRowViewModel(pair, dto, "#0054BD");
    }

    [Fact]
    public void Source_is_marked_when_its_date_is_the_more_recent_one()
    {
        DifferenceRowViewModel row = Row(source: s_newer, target: s_older);

        row.IsSourceNewer.Should().BeTrue();
        row.IsTargetNewer.Should().BeFalse();
    }

    [Fact]
    public void Target_is_marked_when_its_date_is_the_more_recent_one()
    {
        DifferenceRowViewModel row = Row(source: s_older, target: s_newer);

        row.IsTargetNewer.Should().BeTrue();
        row.IsSourceNewer.Should().BeFalse();
    }

    // Same instant on both sides is not a choice — marking one would invent a
    // winner the data does not name.
    [Fact]
    public void Neither_side_is_marked_when_the_dates_are_equal()
    {
        DifferenceRowViewModel row = Row(source: s_newer, target: s_newer);

        row.IsSourceNewer.Should().BeFalse();
        row.IsTargetNewer.Should().BeFalse();
    }

    // Sequence / Synonym / UserDefinedType reach the grid without a modify_date
    // (Mapper.ExtractModifyDate yields null), and an object present on one side
    // only has nothing to compare against.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Neither_side_is_marked_when_a_date_is_missing(bool hasSource, bool hasTarget)
    {
        DifferenceRowViewModel row = Row(
            source: hasSource ? s_newer : null,
            target: hasTarget ? s_older : null);

        row.IsSourceNewer.Should().BeFalse();
        row.IsTargetNewer.Should().BeFalse();
    }

    // An identical object can still carry different modify_dates — a harmless
    // ALTER that changed nothing. There is nothing to align, so pointing at a
    // "winner" would suggest a decision the user does not have to make.
    [Fact]
    public void Identical_rows_are_never_marked()
    {
        DifferenceRowViewModel row = Row(source: s_newer, target: s_older, status: "Identical");

        row.IsSourceNewer.Should().BeFalse();
        row.IsTargetNewer.Should().BeFalse();
    }

    [Theory]
    [InlineData("OnlyInA")]
    [InlineData("OnlyInB")]
    public void One_sided_rows_are_never_marked_even_if_both_dates_somehow_arrive(string status)
    {
        DifferenceRowViewModel row = Row(source: s_newer, target: s_older, status: status);

        row.IsSourceNewer.Should().BeFalse();
        row.IsTargetNewer.Should().BeFalse();
    }

    // The two dates come from two different servers, each with its own clock.
    // The tooltip is what keeps the arrow from being read as absolute truth.
    [Fact]
    public void Marked_rows_explain_that_the_clock_is_the_servers_own()
    {
        DifferenceRowViewModel row = Row(source: s_newer, target: s_older);

        row.NewerTooltip.Should().Contain("server");
    }
}
