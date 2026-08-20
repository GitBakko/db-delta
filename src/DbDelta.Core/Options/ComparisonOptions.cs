namespace DbDelta.Core.Options;

/// <summary>
/// The comparison toggles the engine and the generator actually read.
/// </summary>
/// <remarks>
/// <para>
/// There were twenty. Fourteen were declared and never read by anything, which
/// is worse than an option that does not exist: passing one made a caller
/// believe they had changed something. They were deleted on 2026-08-20 — the
/// surviving six keep their original bit positions, so a legacy <c>.dbd</c>
/// that stored the value as an integer still means what it meant.
/// </para>
/// <para>
/// Two of the deleted ones described behaviour that IS the behaviour, with or
/// without a flag: whitespace is always collapsed before two bodies are
/// compared, and fill factor and statistics are not read from the catalog at
/// all, so they cannot differ. One described behaviour the tool does NOT have
/// and never had: comments are part of a module body and two bodies differing
/// only by a comment compare Different. The old <c>Default</c> promised to
/// ignore them.
/// </para>
/// </remarks>
[Flags]
public enum ComparisonOptions
{
    None = 0,

    /// <summary>Read by <c>ScriptGenerator</c>: skip GRANT / REVOKE emission.</summary>
    IgnorePermissions = 1 << 5,

    /// <summary>Read by <c>ComparisonEngine</c>: a table's indexes stop being compared.</summary>
    IgnoreIndexes = 1 << 8,

    /// <summary>Read by <c>ComparisonEngine</c>: a table's constraints stop being compared.</summary>
    IgnoreKeys = 1 << 9,

    /// <summary>Read by <c>ScriptGenerator</c>: emit the script without a transaction.</summary>
    NoTransactions = 1 << 16,

    /// <summary>
    /// Read by <c>ComparisonEngine</c>: a column that moved ordinal counts as a
    /// difference. Off, only its shape matters.
    /// </summary>
    ForceColumnOrder = 1 << 17,

    /// <summary>Read by <c>ScriptGenerator</c>: leave out the header comment block.</summary>
    DoNotOutputCommentHeader = 1 << 19,

    /// <summary>
    /// What every caller passes. Permissions stay out of the generated script,
    /// matching Redgate's default; everything else is compared.
    /// </summary>
    Default = IgnorePermissions,
}
