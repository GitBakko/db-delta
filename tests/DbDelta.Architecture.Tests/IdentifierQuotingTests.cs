using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DbDelta.Architecture.Tests;

/// <summary>
/// S11 — every bracketed identifier in emitted T-SQL goes through
/// <c>Sql.Q</c>, which doubles embedded <c>]</c> the way QUOTENAME does.
/// Fixing the emitters once buys nothing on its own: the pattern was copied
/// into sixteen files over the project's life and would come back the next time
/// someone writes a CREATE statement. This test is what stops the third copy.
/// </summary>
public class IdentifierQuotingTests
{
    /// <summary>
    /// The emitters, plus the one file outside them that splices a qualified
    /// name into a live module body.
    /// </summary>
    private static IEnumerable<string> DdlEmittingFiles()
    {
        string root = RepoRoot();
        foreach (string f in Directory.EnumerateFiles(
            Path.Combine(root, "src", "DbDelta.Core", "ScriptGen"), "*.cs"))
        {
            // Sql.cs is where the quoting is defined, so it is the one file
            // allowed to write a bracket by hand.
            if (Path.GetFileName(f) == "Sql.cs") { continue; }
            yield return f;
        }
        yield return Path.Combine(root, "src", "DbDelta.Core", "Diff", "ModuleHeader.cs");
    }

    [Fact]
    public void No_emitter_interpolates_an_identifier_into_brackets_by_hand()
    {
        List<string> offenders = [];
        foreach (string file in DdlEmittingFiles())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) { continue; }
                if (lines[i].Contains("[{", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "an interpolated [{...}] writes the name raw, so a catalog name holding a ']' "
            + "closes its own identifier and the remainder becomes script text — route it through Sql.Q");
    }

    [Fact]
    public void No_emitter_builds_a_bracketed_identifier_with_a_string_builder()
    {
        // The StringBuilder form of the same defect, which a search for "[{"
        // does not see at all: .Append("CREATE TABLE [").Append(schema)…
        Regex appendBracket = new(@"Append\(\s*(""[^""]*\[|'\[')", RegexOptions.None, TimeSpan.FromSeconds(5));
        List<string> offenders = [];
        foreach (string file in DdlEmittingFiles())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) { continue; }
                if (appendBracket.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty("append the result of Sql.Q instead of an opening bracket");
    }

    /// <summary>
    /// A guard that never fails proves nothing — this asserts the two above can
    /// actually see the pattern they are looking for.
    /// </summary>
    [Fact]
    public void The_guards_recognise_the_pattern_they_forbid()
    {
        const string interpolated = """    sb.Append($"CREATE TABLE [{table.Schema}]");""";
        const string appended = """    sb.Append("CREATE TABLE [").Append(table.Schema);""";
        Regex appendBracket = new(@"Append\(\s*(""[^""]*\[|'\[')", RegexOptions.None, TimeSpan.FromSeconds(5));

        interpolated.Contains("[{", StringComparison.Ordinal).Should().BeTrue();
        appendBracket.IsMatch(appended).Should().BeTrue();
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDelta.sln")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("the tests must run from inside the repository");
        return dir!.FullName;
    }
}
