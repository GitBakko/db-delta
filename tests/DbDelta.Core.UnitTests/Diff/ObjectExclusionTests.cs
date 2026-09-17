using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

/// <summary>
/// <c>--exclude</c> for the CLI: the object selection the GUI has had all
/// along, as patterns on the command line.
/// </summary>
/// <remarks>
/// From the live smoke of 2026-09-02: one object the operator cannot fix on
/// the server — an orphaned user, a view over a database that is not there —
/// blocked the whole verb, and the documented recovery («re-compare and
/// re-generate, never re-run») could not advance. Chosen by the owner on
/// 2026-09-17 over <c>--continue-on-error</c>, which would leave the target
/// halfway by choice.
/// </remarks>
public class ObjectExclusionTests
{
    private static DifferencePair Pair(ObjectIdentity id) =>
        new(id, DifferenceStatus.OnlyInA, SideA: null, SideB: null);

    private static readonly ObjectIdentity s_view = new("dbo", "VwMigrazioneOdsNexi", "View");
    private static readonly ObjectIdentity s_otherView = new("dbo", "VwAppuntamenti", "View");
    private static readonly ObjectIdentity s_table = new("sales", "Orders", "Table");
    private static readonly ObjectIdentity s_user = new DatabaseUser("pcrm_ro", "S", null, "dbo").Identity;
    private static readonly ObjectIdentity s_grant =
        new Permission("pcrm_ro", "CONNECT", PermissionState.Grant, "DATABASE", null, null, null).Identity;

    private static ComparisonResult All() => new([Pair(s_view), Pair(s_otherView), Pair(s_table), Pair(s_user), Pair(s_grant)]);

    [Fact]
    public void An_exact_schema_dot_name_excludes_that_object_and_nothing_else()
    {
        ComparisonResult kept = ObjectExclusion.Apply(All(), ["dbo.VwMigrazioneOdsNexi"], out _);

        kept.Differences.Select(d => d.Identity).Should().Equal(s_otherView, s_table, s_user, s_grant);
    }

    [Fact]
    public void A_glob_matches_the_schema_dot_name_form()
    {
        ComparisonResult kept = ObjectExclusion.Apply(All(), ["dbo.Vw*"], out _);

        kept.Differences.Select(d => d.Identity).Should().Equal(s_table, s_user, s_grant);
    }

    [Fact]
    public void A_pattern_without_a_dot_matches_the_name_alone_which_is_how_a_user_and_its_grants_go()
    {
        // A user has no schema, and its GRANTs carry the grantee inside their
        // name: one pattern takes the principal that blocked the whole verb on
        // 2026-09-02 together with what was granted to it.
        ComparisonResult kept = ObjectExclusion.Apply(All(), ["*pcrm_ro*"], out _);

        kept.Differences.Select(d => d.Identity).Should().Equal(s_view, s_otherView, s_table);
    }

    [Fact]
    public void Matching_ignores_case_like_the_identifiers_it_names()
    {
        ComparisonResult kept = ObjectExclusion.Apply(All(), ["SALES.orders"], out _);

        kept.Differences.Select(d => d.Identity).Should().NotContain(s_table);
    }

    [Fact]
    public void A_pattern_that_matches_nothing_is_reported_not_swallowed()
    {
        // A typo in --exclude that quietly excludes nothing is the silent
        // failure this project refuses elsewhere; the caller gets the list
        // and says so on stderr.
        ObjectExclusion.Apply(All(), ["dbo.VwMigrazione*", "dbo.Typo"], out IReadOnlyList<string> unmatched);

        unmatched.Should().Equal("dbo.Typo");
    }

    [Fact]
    public void Control_no_pattern_changes_nothing_including_the_census()
    {
        ComparisonResult all = All() with { Unexamined = new UnexaminedCensus([new UnexaminedGroup("EXTENDED_PROPERTY", 3)]) };

        ComparisonResult kept = ObjectExclusion.Apply(all, [], out IReadOnlyList<string> unmatched);

        kept.Differences.Should().HaveCount(5);
        kept.Unexamined.Should().Be(all.Unexamined, "excluding objects does not narrow what was examined");
        unmatched.Should().BeEmpty();
    }
}
