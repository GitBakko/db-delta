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
/// <remarks>
/// The guard started as two facts scanning for two shapes, and four more ways of
/// writing the same defect walked straight past both: <c>AppendLine("… [")</c>,
/// <c>AppendFormat("[{0}]", …)</c>, <c>string.Concat("[", …)</c>, and plain
/// <c>"[" + name + "]"</c>. A lint that only sees the shapes already written is
/// a lint that catches nothing new, so the patterns live in one table here and
/// the walk over the files exists once.
/// </remarks>
public class IdentifierQuotingTests
{
    /// <summary>
    /// One forbidden way of writing a bracket by hand, with a sample that must
    /// match it — a pattern nothing can trigger is a pattern nobody has tested.
    /// </summary>
    private sealed record BracketPattern(string Why, string Regex, string Sample);

    private static readonly BracketPattern[] Patterns =
    [
        new("interpolation",
            @"\[\{",
            """    sb.Append($"CREATE TABLE [{table.Schema}]");"""),
        new("builder append",
            @"Append(Line|Format)?\(\s*(""[^""]*\[|'\[')",
            """    sb.Append("CREATE TABLE [").Append(table.Schema);"""),
        new("builder append-line",
            @"Append(Line|Format)?\(\s*(""[^""]*\[|'\[')",
            """    sb.AppendLine("DROP TABLE [");"""),
        new("format placeholder",
            @"Append(Line|Format)?\(\s*(""[^""]*\[|'\[')",
            """    sb.AppendFormat("[{0}]", table.Name);"""),
        new("string.Concat",
            @"string\.Concat\(\s*""[^""]*\[",
            """    string q = string.Concat("[", table.Name, "]");"""),
        new("operator +",
            @"(""\[""\s*\+|\+\s*""\]"")",
            """    string q = "[" + table.Name + "]";"""),
    ];

    private static readonly TimeSpan RegexCap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The emitters, the one file outside them that splices a qualified name
    /// into a live module body, and the live provider — which reads catalog
    /// names and hands them to those emitters, so it is one refactor away from
    /// building an identifier itself.
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

        foreach (string f in Directory.EnumerateFiles(
            Path.Combine(root, "src", "DbDelta.Providers.LiveDb"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            yield return f;
        }
    }

    [Fact]
    public void No_emitter_builds_a_bracketed_identifier_by_hand()
    {
        List<string> offenders = [];
        foreach (string file in DdlEmittingFiles())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) { continue; }
                foreach (BracketPattern p in Patterns)
                {
                    if (Regex.IsMatch(lines[i], p.Regex, RegexOptions.None, RegexCap))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} ({p.Why}): {lines[i].Trim()}");
                        break;
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "a hand-written bracket writes the name raw, so a catalog name holding a ']' "
            + "closes its own identifier and the remainder becomes script text — route it through Sql.Q");
    }

    /// <summary>
    /// A guard that never fails proves nothing — this asserts every pattern above
    /// can actually see the shape it is looking for.
    /// </summary>
    [Fact]
    public void Every_guard_recognises_the_pattern_it_forbids()
    {
        foreach (BracketPattern p in Patterns)
        {
            Regex.IsMatch(p.Sample, p.Regex, RegexOptions.None, RegexCap)
                .Should().BeTrue($"the '{p.Why}' guard must match its own sample: {p.Sample}");
        }
    }

    /// <summary>
    /// The scan has to reach the files it claims to cover. A typo in a path
    /// silently empties the corpus and every offender list with it.
    /// </summary>
    [Fact]
    public void The_scan_covers_the_emitters_the_module_header_and_the_live_provider()
    {
        List<string> files = [.. DdlEmittingFiles()];

        files.Should().OnlyContain(f => File.Exists(f), "a path that does not exist scans nothing");
        files.Should().Contain(f => Path.GetFileName(f) == "TableScriptEmitter.cs");
        files.Should().Contain(f => Path.GetFileName(f) == "ModuleHeader.cs");
        files.Should().Contain(f => Path.GetFileName(f) == "LiveDbSource.cs");
        files.Should().NotContain(f => Path.GetFileName(f) == "Sql.cs");
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
