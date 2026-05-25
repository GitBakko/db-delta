using DbDelta.Core.Diff;
using DbDelta.Core.Reports;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Reports;

public class KindCatalogTests
{
    [Theory]
    [InlineData("Table", 0)]
    [InlineData("View", 1)]
    [InlineData("Procedure", 2)]
    [InlineData("Function", 3)]
    [InlineData("Trigger", 4)]
    [InlineData("Sequence", 5)]
    [InlineData("Synonym", 6)]
    [InlineData("UserDefinedType", 7)]
    [InlineData("User", 8)]
    [InlineData("Role", 9)]
    [InlineData("Permission", 10)]
    public void SortOrder_returns_canonical_index_for_known_kind(string kind, int expectedOrder)
    {
        KindCatalog.SortOrder(kind).Should().Be(expectedOrder);
    }

    [Fact]
    public void SortOrder_returns_max_value_for_unknown_kind_so_it_sorts_last()
    {
        KindCatalog.SortOrder("ZZUnknown").Should().Be(int.MaxValue);
    }

    [Theory]
    [InlineData("Table", "Tabelle")]
    [InlineData("View", "Viste")]
    [InlineData("Procedure", "Procedure")]
    [InlineData("Function", "Funzioni")]
    [InlineData("Trigger", "Trigger")]
    [InlineData("Sequence", "Sequenze")]
    [InlineData("Synonym", "Sinonimi")]
    [InlineData("UserDefinedType", "Tipi utente")]
    [InlineData("User", "Utenti")]
    [InlineData("Role", "Ruoli")]
    [InlineData("Permission", "Permessi")]
    public void DisplayLabel_returns_italian_plural_for_known_kind(string kind, string expectedLabel)
    {
        KindCatalog.DisplayLabel(kind).Should().Be(expectedLabel);
    }

    [Fact]
    public void DisplayLabel_falls_back_to_raw_kind_string_for_unknown_kind()
    {
        KindCatalog.DisplayLabel("Whatever").Should().Be("Whatever");
    }

    [Theory]
    [InlineData(DifferenceStatus.Different, 0)]
    [InlineData(DifferenceStatus.OnlyInB, 1)]
    [InlineData(DifferenceStatus.OnlyInA, 2)]
    [InlineData(DifferenceStatus.Identical, 3)]
    public void StatusOrder_classifies_by_visual_importance(DifferenceStatus status, int expected)
    {
        KindCatalog.StatusOrder(status).Should().Be(expected);
    }

    [Fact]
    public void KnownKinds_lists_all_eleven_in_canonical_order()
    {
        KindCatalog.KnownKinds.Should().Equal(
            "Table", "View", "Procedure", "Function", "Trigger",
            "Sequence", "Synonym", "UserDefinedType",
            "User", "Role", "Permission");
    }
}
