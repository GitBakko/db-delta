using DbDelta.Core.Diff;
using FluentAssertions;
using Xunit;

namespace DbDelta.Core.UnitTests.Diff;

public class BodyNormalizerTests
{
    [Fact]
    public void Null_input_returns_null() => BodyNormalizer.Normalize(null).Should().BeNull();

    [Fact]
    public void Trims_outer_whitespace() => BodyNormalizer.Normalize("   SELECT 1   ").Should().Be("SELECT 1");

    [Fact]
    public void Collapses_runs_of_whitespace_to_single_space() => BodyNormalizer.Normalize("SELECT     1\t\t  ,\n\n2").Should().Be("SELECT 1 , 2");

    [Fact]
    public void Normalizes_CRLF_to_LF_before_collapsing() => BodyNormalizer.Normalize("a\r\nb\r\nc").Should().Be("a b c");

    [Fact]
    public void Preserves_case_by_default() => BodyNormalizer.Normalize("Select x From dbo.T").Should().Be("Select x From dbo.T");

    [Fact]
    public void Two_bodies_only_differing_in_whitespace_compare_equal_after_normalize()
    {
        string a = "CREATE VIEW dbo.v AS\r\nSELECT 1 AS Id;";
        string b = "CREATE  VIEW   dbo.v  AS  SELECT 1 AS Id;";
        BodyNormalizer.Normalize(a).Should().Be(BodyNormalizer.Normalize(b));
    }

    [Fact]
    public void Strips_trailing_semicolon_so_roundtrip_bodies_compare_equal()
    {
        // SQL Server may store a module body without a trailing ';' while the script
        // generator appends one, or vice versa.  Both forms must normalise identically.
        string withoutSemi = "CREATE FUNCTION dbo.fnTax(@x money) RETURNS money AS BEGIN RETURN @x*0.2 END";
        string withSemi = withoutSemi + ";";
        BodyNormalizer.Normalize(withoutSemi).Should().Be(BodyNormalizer.Normalize(withSemi));
    }

    // ----- ExpressionsEqual -------------------------------------------------

    [Theory]
    [InlineData("([Age]>=0)", "([Age]>=0)\r\n")]
    [InlineData("([Age]>=0\r\nAND [Age]<=120)", "([Age]>=0 AND [Age]<=120)")]
    [InlineData("(getdate())", "  (getdate())  ")]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData("", "   ")]
    public void ExpressionsEqual_ignores_whitespace_and_null_vs_empty(string? a, string? b) =>
        BodyNormalizer.ExpressionsEqual(a, b).Should().BeTrue();

    [Theory]
    [InlineData("([Age]>=0)", "([Age]>=1)")]
    [InlineData("(getdate())", "(GETDATE())")] // case preserved, like Normalize
    [InlineData(null, "(0)")]
    public void ExpressionsEqual_still_detects_real_differences(string? a, string? b) =>
        BodyNormalizer.ExpressionsEqual(a, b).Should().BeFalse();
}
