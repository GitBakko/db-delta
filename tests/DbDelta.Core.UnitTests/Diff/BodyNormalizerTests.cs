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
}
