namespace DbDelta.Core.Diff;

/// <summary>
/// Outcome of running <see cref="ComparisonEngine.Compare"/>.
/// </summary>
public sealed record ComparisonResult(IReadOnlyList<DifferencePair> Differences)
{
    /// <summary>
    /// How the target server resolves identifier case, derived from its
    /// collation by <see cref="NameComparison.ForCollation"/>. The script
    /// generator has to pair columns, constraints and indexes by the same rule
    /// the engine paired their tables with, otherwise a table correctly
    /// reported as Different is then altered by dropping and re-adding a column
    /// that only differs in case — which destroys its data.
    /// <para>
    /// Defaults to ordinal so a hand-built result (tests, fixtures) keeps the
    /// literal-minded behaviour its assertions were written against.
    /// </para>
    /// </summary>
    public StringComparer NameComparer { get; init; } = StringComparer.Ordinal;

    /// <summary>
    /// The families of objects neither endpoint was examined for, merged across
    /// the two sides. Empty ⇒ the verdict covers everything both databases hold.
    /// </summary>
    /// <remarks>
    /// Every artefact that reports a verdict — the app's grid banner, the HTML
    /// report, the JSON report, the CLI — reads this so the disclosure travels
    /// with the result instead of being re-derived, or forgotten, in each.
    /// </remarks>
    public ObjectModel.UnexaminedCensus Unexamined { get; init; } = ObjectModel.UnexaminedCensus.Empty;
}
