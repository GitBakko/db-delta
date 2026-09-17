using System.CommandLine;
using DbDelta.Core.Diff;

namespace DbDelta.Cli.Commands;

/// <summary>
/// <c>--exclude</c>, shared by <c>compare</c>, <c>report</c> and <c>script</c>:
/// the GUI's per-object selection, as patterns. Not on <c>apply</c>, which runs
/// a file and compares nothing.
/// </summary>
internal static class ExcludeOption
{
    public static Option<string[]> Create() => new("--exclude")
    {
        Description =
            "Leave an object out of the verdict and of the script: `schema.name`, with `*` and `?` "
            + "as wildcards, case-insensitive. A pattern with no dot matches the name alone — a user "
            + "or a role has no schema, and `*pcrm_ro*` also takes what was granted to it. Repeatable. "
            + "A pattern that matches nothing is reported on stderr and changes nothing.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = false,
    };

    /// <summary>The result without the excluded pairs; each pattern that took nothing is named on stderr.</summary>
    public static ComparisonResult Apply(ComparisonResult comparison, string[]? patterns)
    {
        ComparisonResult kept = ObjectExclusion.Apply(comparison, patterns ?? [], out IReadOnlyList<string> unmatched);
        foreach (string pattern in unmatched)
        {
            Console.Error.WriteLine($"--exclude '{pattern}' non corrisponde a nessun oggetto.");
        }

        return kept;
    }
}
