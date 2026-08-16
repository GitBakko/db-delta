using DbDelta.Core.ObjectModel;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.ObjectModel;

public class TableIndexTests
{
    /// <summary>
    /// The one question every emission path asks before writing SQL. A wrong
    /// TRUE here writes a rowstore CREATE INDEX for a columnstore — valid SQL
    /// for a different index — and a wrong FALSE refuses a script that was
    /// always fine, so both directions are pinned.
    /// </summary>
    [Theory]
    [InlineData(null, true)]                          // hand-built model: rowstore
    [InlineData("CLUSTERED", true)]
    [InlineData("NONCLUSTERED", true)]
    [InlineData("nonclustered", true)]                // catalogs are upper-case; be forgiving anyway
    [InlineData("CLUSTERED COLUMNSTORE", false)]
    [InlineData("NONCLUSTERED COLUMNSTORE", false)]
    [InlineData("XML", false)]
    [InlineData("SPATIAL", false)]
    [InlineData("NONCLUSTERED HASH", false)]
    public void IsRowstore_admits_only_the_two_shapes_the_emitter_can_write(
        string? typeDesc, bool expected)
    {
        TableIndex ix = new(
            Name: "IX_Whatever",
            IsUnique: false,
            IsClustered: false,
            FilterExpression: null,
            KeyColumns: [new IndexColumn("Col", false)],
            IncludedColumns: [],
            TypeDesc: typeDesc);

        ix.IsRowstore.Should().Be(expected);
    }
}
