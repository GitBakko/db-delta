namespace DbDelta.Core.Abstractions;

/// <summary>
/// Fine-grained comparison toggles stored inside a <c>.dbd</c> project file.
/// These mirror the most common Red Gate SQL Compare options.
/// </summary>
public sealed record ProjectOptions(
    bool IgnoreFillFactor,
    bool IgnoreCollation,
    bool IgnoreWhitespace,
    bool IgnoreCommentBlocks,
    bool TreatExtendedPropertiesAsObjects)
{
    /// <summary>
    /// Sensible defaults matching the Red Gate SQL Compare defaults:
    /// ignore whitespace only; everything else at its conservative setting.
    /// </summary>
    public static readonly ProjectOptions Default = new(
        IgnoreFillFactor: false,
        IgnoreCollation: false,
        IgnoreWhitespace: true,
        IgnoreCommentBlocks: false,
        TreatExtendedPropertiesAsObjects: false);
}
