using DbDelta.Core.Diff;
using DbDelta.Core.ObjectModel;
using DbDelta.Core.Options;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class UnexaminedCensusTests
{
    [Fact]
    public void Empty_census_says_nothing()
    {
        UnexaminedCensus.Empty.IsEmpty.Should().BeTrue();
        UnexaminedCensus.Empty.Summary.Should().BeEmpty();
    }

    /// <summary>
    /// The two sides are NOT added. The same columnstore index normally exists
    /// on both endpoints; summing would report two where there is one, and the
    /// number is the whole point of showing the caveat at all.
    /// </summary>
    [Fact]
    public void Merge_takes_the_larger_side_rather_than_the_sum()
    {
        UnexaminedCensus source = new([new("INDEX_NON_ROWSTORE", 4), new("ASSEMBLY", 1)]);
        UnexaminedCensus target = new([new("INDEX_NON_ROWSTORE", 6), new("EXTENDED_PROPERTY", 118)]);

        var merged = UnexaminedCensus.Merge(source, target);

        merged.Groups.Should().HaveCount(3);
        merged.Groups.Single(g => g.Key == "INDEX_NON_ROWSTORE").Count.Should().Be(6);
        merged.Groups.Single(g => g.Key == "ASSEMBLY").Count.Should().Be(1);
        merged.Groups.Single(g => g.Key == "EXTENDED_PROPERTY").Count.Should().Be(118);
    }

    [Fact]
    public void Merge_of_two_empty_sides_is_empty()
    {
        UnexaminedCensus.Merge(UnexaminedCensus.Empty, UnexaminedCensus.Empty).IsEmpty.Should().BeTrue();
        UnexaminedCensus.Merge(null, null).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Merge_orders_the_largest_family_first()
    {
        var merged = UnexaminedCensus.Merge(
            new([new("ASSEMBLY", 4)]),
            new([new("EXTENDED_PROPERTY", 118), new("PARTITION_SCHEME", 2)]));

        merged.Groups.Select(g => g.Key).Should()
            .ContainInOrder("EXTENDED_PROPERTY", "ASSEMBLY", "PARTITION_SCHEME");
    }

    [Fact]
    public void Summary_states_the_scope_and_names_every_family()
    {
        UnexaminedCensus census = new([new("ASSEMBLY", 4), new("PARTITION_SCHEME", 2)]);

        census.Summary.Should()
            .Contain("13 tipologie").And
            .Contain("4 assembly CLR").And
            .Contain("2 schemi di partizione");
    }

    /// <summary>
    /// A SQL Server version that adds an object type must show up as something
    /// readable rather than vanish, which is why the reader excludes the modelled
    /// types instead of listing the unmodelled ones.
    /// </summary>
    [Fact]
    public void An_unknown_catalog_key_still_gets_a_readable_label()
    {
        UnexaminedCensus.LabelFor("SOME_FUTURE_THING").Should().Be("some future thing");
        UnexaminedCensus.LabelFor("ASSEMBLY").Should().Be("assembly CLR");
    }

    /// <summary>
    /// The caveat has to reach every artefact that reports a verdict, so the
    /// engine carries it on the result rather than each consumer re-deriving it.
    /// </summary>
    [Fact]
    public void The_engine_carries_the_merged_census_onto_the_result()
    {
        Database a = new("A", [], []) { Unexamined = new([new("INDEX_NON_ROWSTORE", 3)]) };
        Database b = new("B", [], []) { Unexamined = new([new("INDEX_NON_ROWSTORE", 5)]) };

        ComparisonResult result = new ComparisonEngine().Compare(a, b, ComparisonOptions.Default);

        result.Unexamined.Groups.Single().Count.Should().Be(5);
    }

    [Fact]
    public void A_result_from_two_fully_modelled_databases_carries_no_caveat()
    {
        ComparisonResult result = new ComparisonEngine()
            .Compare(new Database("A", [], []), new Database("B", [], []), ComparisonOptions.Default);

        result.Unexamined.IsEmpty.Should().BeTrue();
    }
}
