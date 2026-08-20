using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

// Lives under Diff/ rather than Options/ for one reason: a
// DbDelta.Core.UnitTests.Options namespace shadows DbDelta.Core.Options for
// every other test file in the assembly, and eleven of them stop compiling.
namespace DbDelta.Core.UnitTests.Diff;

/// <summary>
/// The six toggles something actually reads, and the bit each one sits on.
/// </summary>
/// <remarks>
/// Fourteen more were declared and never read; they were deleted rather than
/// implemented, because an option nobody reads is worse than one that does not
/// exist — the caller who passes it believes they changed something. The
/// positions are pinned here because a legacy <c>.dbd</c> stores this value as
/// an INTEGER: renumbering the survivors to close the gaps would silently
/// re-interpret every project file written before today.
/// </remarks>
public class ComparisonOptionsTests
{
    [Theory]
    [InlineData(ComparisonOptions.IgnorePermissions, 1 << 5)]
    [InlineData(ComparisonOptions.IgnoreIndexes, 1 << 8)]
    [InlineData(ComparisonOptions.IgnoreKeys, 1 << 9)]
    [InlineData(ComparisonOptions.NoTransactions, 1 << 16)]
    [InlineData(ComparisonOptions.ForceColumnOrder, 1 << 17)]
    [InlineData(ComparisonOptions.DoNotOutputCommentHeader, 1 << 19)]
    public void Each_surviving_flag_keeps_the_bit_a_saved_project_stored_it_on(
        ComparisonOptions flag, int expected) => ((int)flag).Should().Be(expected);

    [Fact]
    public void The_default_leaves_permissions_out_and_compares_everything_else() => ComparisonOptions.Default.Should().Be(ComparisonOptions.IgnorePermissions);

    [Fact]
    public void Nothing_else_is_declared()
    {
        // The negative control on the deletion: adding a flag back without
        // wiring a reader for it fails here, which is the whole point.
        Enum.GetNames<ComparisonOptions>().Should().BeEquivalentTo(
            "None", "IgnorePermissions", "IgnoreIndexes", "IgnoreKeys",
            "NoTransactions", "ForceColumnOrder", "DoNotOutputCommentHeader", "Default");
    }
}
