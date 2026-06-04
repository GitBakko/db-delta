using FluentAssertions;
using Xunit;

// NOTE: no `using DbDelta.App;` — the namespace below already resolves it via
// parent-namespace traversal, and a redundant using trips IDE0005 in the
// format gate. `using Xunit;` IS required here: xunit.v3 (3.2.2) does not
// register `Xunit` as a project-wide implicit using (no `<Using Include="Xunit"/>`
// in its build props), so `[Fact]` does not resolve without it — every other
// test file in this repo imports it explicitly for the same reason.

namespace DbDelta.App.HeadlessTests;

public class AppVersionInfoTests
{
    private const string PageUrl = "https://gitbakko.github.io/db-delta/articles/version-history.html";

    [Fact]
    public void Null_raw_version_falls_back_to_plain_dev_and_unanchored_url()
    {
        (string display, string url) = AppVersionInfo.FromRaw(null);
        display.Should().Be("dev");
        url.Should().Be(PageUrl);
    }

    [Fact]
    public void Whitespace_raw_version_falls_back_to_plain_dev()
    {
        (string display, string url) = AppVersionInfo.FromRaw("   ");
        display.Should().Be("dev");
        url.Should().Be(PageUrl);
    }

    [Fact]
    public void Build_metadata_suffix_is_stripped()
    {
        // The SDK appends "+<commit-sha>" when building inside a git repo.
        (string display, string url) = AppVersionInfo.FromRaw("1.0.0-rc1+abc1234");
        display.Should().Be("v1.0.0-rc1");
        url.Should().Be($"{PageUrl}#v1.0.0-rc1");
    }

    [Fact]
    public void Plain_semver_maps_to_prefixed_display_and_anchored_url()
    {
        (string display, string url) = AppVersionInfo.FromRaw("0.0.0-dev");
        display.Should().Be("v0.0.0-dev");
        url.Should().Be($"{PageUrl}#v0.0.0-dev");
    }

    [Fact]
    public void Static_properties_are_populated_and_consistent()
    {
        AppVersionInfo.Display.Should().NotBeNullOrWhiteSpace();
        AppVersionInfo.HistoryUrl.Should().StartWith(PageUrl);
    }
}
