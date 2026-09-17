using System.Text.RegularExpressions;

namespace DbDelta.Core.Diff;

/// <summary>
/// Drops from a <see cref="ComparisonResult"/> the pairs an operator named
/// with <c>schema.name</c> patterns — the GUI's per-object selection, for the
/// CLI's <c>--exclude</c>.
/// </summary>
/// <remarks>
/// A pattern is matched against <c>schema.name</c>, case-insensitively like
/// the identifiers it names, with <c>*</c> for any run and <c>?</c> for one
/// character. A pattern with no dot is matched against the name alone: a user
/// or a role has no schema, and a permission carries its grantee inside its
/// name, so <c>*pcrm_ro*</c> takes the principal that blocked a whole verb on
/// 2026-09-02 together with everything granted to it. The census of what was
/// not examined is left as it is — excluding an object narrows the verdict,
/// not what was looked at — and the patterns that matched nothing come back
/// to the caller, because an <c>--exclude</c> that quietly excludes nothing is
/// a typo nobody would see.
/// </remarks>
public static class ObjectExclusion
{
    public static ComparisonResult Apply(
        ComparisonResult result,
        IReadOnlyList<string> patterns,
        out IReadOnlyList<string> unmatched)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0)
        {
            unmatched = [];
            return result;
        }

        List<(string Pattern, Regex Regex, bool NameOnly)> compiled = [.. patterns
            .Select(p => (p, ToRegex(p), !p.Contains('.')))];
        HashSet<string> hit = [];
        List<DifferencePair> kept = [];
        foreach (DifferencePair pair in result.Differences)
        {
            bool excluded = false;
            foreach ((string pattern, Regex regex, bool nameOnly) in compiled)
            {
                string subject = nameOnly
                    ? pair.Identity.ObjectName
                    : $"{pair.Identity.SchemaName}.{pair.Identity.ObjectName}";
                if (regex.IsMatch(subject))
                {
                    hit.Add(pattern);
                    excluded = true;
                }
            }

            if (!excluded) { kept.Add(pair); }
        }

        unmatched = [.. patterns.Where(p => !hit.Contains(p))];
        return result with { Differences = kept };
    }

    private static Regex ToRegex(string glob) => new(
        "^" + Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
