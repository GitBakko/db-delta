using DbDelta.Persistence.Json;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.UnitTests.Json;

/// <summary>
/// The project name is typed by the user, so <c>ResolvePath</c> is a trust
/// boundary: everything it returns is handed straight to <c>Path.Combine</c>
/// and then to a file write.
/// </summary>
/// <remarks>
/// Two failures lived here and neither was catchable downstream. The buffer was
/// a <c>stackalloc</c> sized from the input, so a long pasted name raised
/// <see cref="StackOverflowException"/> — which terminates the process past any
/// handler. And the filter covered only <c>Path.GetInvalidFileNameChars</c>,
/// which does not include the Windows device names: a project called
/// <c>NUL</c> was written to the null device and read back empty, silently.
/// </remarks>
public class ProjectsFolderTests
{
    [Fact]
    public void A_very_long_name_is_capped_instead_of_blowing_the_stack()
    {
        // 200_000 characters is a paste, not an attack — and it used to be a
        // 400 KB stack request. Anything that returns at all passes; the test
        // exists so that "returns at all" stays true.
        string path = ProjectsFolder.ResolvePath(new string('a', 200_000));

        Path.GetFileNameWithoutExtension(path).Length.Should().BeLessThanOrEqualTo(100);
        path.Should().EndWith(".dbd");
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("CON")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("aux")]
    public void A_reserved_device_name_never_reaches_the_path(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(ProjectsFolder.ResolvePath(name));

        stem.Should().NotBe(name, "writing to that stem writes to a device, not to a file");
        stem.Should().Be("_" + name);
    }

    [Fact]
    public void A_reserved_name_carrying_its_own_extension_is_caught_too()
    {
        // Windows matches the device on the part before the dot, so "NUL.v2"
        // is the null device just as "NUL" is.
        string stem = Path.GetFileNameWithoutExtension(ProjectsFolder.ResolvePath("NUL.v2"));

        stem.Should().Be("_NUL.v2");
    }

    [Theory]
    [InlineData("a/b", "a_b")]
    [InlineData("a:b", "a_b")]
    [InlineData("report.", "report")]
    [InlineData("  spaced  ", "spaced")]
    public void Separators_and_trailing_dots_are_neutralised(string name, string expected) => Path.GetFileNameWithoutExtension(ProjectsFolder.ResolvePath(name)).Should().Be(expected);

    [Fact]
    public void An_ordinary_name_is_left_alone()
    {
        // The negative control: the guards must not mangle the common case.
        Path.GetFileNameWithoutExtension(ProjectsFolder.ResolvePath("Contabilità 2026"))
            .Should().Be("Contabilità 2026");
    }

    [Fact]
    public void A_name_of_nothing_but_separators_falls_back_to_a_usable_stem() => Path.GetFileNameWithoutExtension(ProjectsFolder.ResolvePath("///")).Should().Be("___");
}
