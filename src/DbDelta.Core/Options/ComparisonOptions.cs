namespace DbDelta.Core.Options;

/// <summary>
/// Bitmap of comparison toggles. v1 covers the most-used 20 options;
/// later milestones add the remaining flags from spec §1.2.
/// </summary>
[Flags]
public enum ComparisonOptions
{
    None = 0,
    IgnoreWhitespace = 1 << 0,
    IgnoreComments = 1 << 1,
    IgnoreCollations = 1 << 2,
    IgnoreFillFactor = 1 << 3,
    IgnoreConstraintNames = 1 << 4,
    IgnorePermissions = 1 << 5,
    IgnoreUserSettings = 1 << 6,
    CaseSensitiveObjectDefinition = 1 << 7,
    IgnoreIndexes = 1 << 8,
    IgnoreKeys = 1 << 9,
    IgnoreStatistics = 1 << 10,
    IgnoreTriggers = 1 << 11,
    IgnoreWithElementOrder = 1 << 12,
    IgnoreFileGroups = 1 << 13,
    IgnoreIdentitySeed = 1 << 14,
    IgnoreUsersPermissionsAndRoleMemberships = 1 << 15,
    NoTransactions = 1 << 16,
    ForceColumnOrder = 1 << 17,
    ThrowOnFileParseFailed = 1 << 18,
    DoNotOutputCommentHeader = 1 << 19,

    /// <summary>
    /// The defaults Redgate ships, mirrored: ignore whitespace, comments,
    /// fill factor, permissions, statistics.
    /// </summary>
    Default = IgnoreWhitespace | IgnoreComments | IgnoreFillFactor
            | IgnorePermissions | IgnoreStatistics,
}
