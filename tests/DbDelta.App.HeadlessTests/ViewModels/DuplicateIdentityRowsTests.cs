using DbDelta.App.ViewModels;
using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.App.HeadlessTests.ViewModels;

/// <summary>
/// Two comparison rows may carry the same <see cref="ObjectIdentity"/>, and the
/// grid must survive it. <see cref="Permission.Identity"/> leaves
/// <c>ClassDesc</c> out while <see cref="Permission.DiffKey"/> keeps it, so a
/// database that grants the same action to the same principal at two different
/// classes produces two genuinely different rows that hash to one identity.
/// </summary>
/// <remarks>
/// The rebuild used to re-join the DTO list to the raw list through a
/// <c>ToDictionary</c> on that identity. The duplicate key threw
/// <see cref="ArgumentException"/> out of the <c>PropertyChanged</c> handler
/// that calls the rebuild, which has no <c>try</c> around it: the process died
/// halfway through a comparison. The two lists are positional projections of
/// one another, so the join was never needed at all.
/// </remarks>
public class DuplicateIdentityRowsTests
{
    private static Permission Grant(string classDesc) => new(
        GranteeName: "app_user",
        Action: "SELECT",
        State: PermissionState.Grant,
        ClassDesc: classDesc,
        ObjectSchema: "dbo",
        ObjectName: "Ordini",
        ColumnName: null);

    private static DifferencePair PairFor(Permission p) => new(
        Identity: p.Identity,
        Status: DifferenceStatus.OnlyInA,
        SideA: p,
        SideB: null);

    [Fact]
    public void Two_permissions_that_differ_only_by_class_still_collide_on_identity() =>
        // The premise of the test. If this ever stops holding — because
        // Permission.Identity grew a ClassDesc — the rebuild no longer faces a
        // duplicate key here, and this file should be re-pointed at whatever
        // kind still can, not deleted: the rebuild must not assume uniqueness.
        Grant("OBJECT_OR_COLUMN").Identity.Should().Be(Grant("SCHEMA").Identity);

    [Fact]
    public void A_comparison_with_two_colliding_identities_builds_both_rows()
    {
        AppStateViewModel appState = new();
        MainWindowViewModel vm = new(appState);

        ComparisonResult result = new(
        [
            PairFor(Grant("OBJECT_OR_COLUMN")),
            PairFor(Grant("SCHEMA")),
        ]);

        // PublishComparison is the real route: it sets the raw result and its
        // DTO projection, and the second assignment is what fires the rebuild.
        Action act = () => appState.PublishComparison(result, "src", "tgt");

        act.Should().NotThrow<ArgumentException>(
            "a duplicate identity is data, not a bug in the caller");
        vm.Rows.Should().HaveCount(2,
            "neither row may be dropped — one of them would be a permission "
            + "the user never sees and never deploys");
        vm.Rows.Should().OnlyContain(r => r.Pair.SideA is Permission);
    }
}
