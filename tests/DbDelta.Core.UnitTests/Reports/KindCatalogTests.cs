using DbDelta.Core.Diff;
using DbDelta.Core.Reports;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Reports;

public class KindCatalogTests
{
    [Theory]
    [InlineData("Schema", 0)]
    [InlineData("Table", 1)]
    [InlineData("View", 2)]
    [InlineData("Procedure", 3)]
    [InlineData("Function", 4)]
    [InlineData("Trigger", 5)]
    [InlineData("Sequence", 6)]
    [InlineData("Synonym", 7)]
    [InlineData("UserDefinedType", 8)]
    [InlineData("TableType", 9)]
    [InlineData("User", 10)]
    [InlineData("Role", 11)]
    [InlineData("Permission", 12)]
    public void SortOrder_returns_canonical_index_for_known_kind(string kind, int expectedOrder) => KindCatalog.SortOrder(kind).Should().Be(expectedOrder);

    [Fact]
    public void SortOrder_returns_max_value_for_unknown_kind_so_it_sorts_last() => KindCatalog.SortOrder("ZZUnknown").Should().Be(int.MaxValue);

    [Theory]
    [InlineData("Schema", "Schemi")]
    [InlineData("Table", "Tabelle")]
    [InlineData("View", "Viste")]
    [InlineData("Procedure", "Procedure")]
    [InlineData("Function", "Funzioni")]
    [InlineData("Trigger", "Trigger")]
    [InlineData("Sequence", "Sequenze")]
    [InlineData("Synonym", "Sinonimi")]
    [InlineData("UserDefinedType", "Tipi utente")]
    [InlineData("TableType", "Tipi tabella")]
    [InlineData("User", "Utenti")]
    [InlineData("Role", "Ruoli")]
    [InlineData("Permission", "Permessi")]
    public void DisplayLabel_returns_italian_plural_for_known_kind(string kind, string expectedLabel) => KindCatalog.DisplayLabel(kind).Should().Be(expectedLabel);

    [Fact]
    public void DisplayLabel_falls_back_to_raw_kind_string_for_unknown_kind() => KindCatalog.DisplayLabel("Whatever").Should().Be("Whatever");

    [Theory]
    [InlineData(DifferenceStatus.Different, 0)]
    [InlineData(DifferenceStatus.OnlyInB, 1)]
    [InlineData(DifferenceStatus.OnlyInA, 2)]
    [InlineData(DifferenceStatus.Identical, 3)]
    public void StatusOrder_classifies_by_visual_importance(DifferenceStatus status, int expected) => KindCatalog.StatusOrder(status).Should().Be(expected);

    [Fact]
    public void KnownKinds_lists_all_thirteen_in_canonical_order()
    {
        KindCatalog.KnownKinds.Should().Equal(
            "Schema", "Table", "View", "Procedure", "Function", "Trigger",
            "Sequence", "Synonym", "UserDefinedType", "TableType",
            "User", "Role", "Permission");
    }
}
