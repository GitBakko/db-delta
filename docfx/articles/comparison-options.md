# Comparison options

A comparison is shaped by the `ComparisonOptions` flags enum
(`DbDelta.Core.Options.ComparisonOptions`). The CLI uses the `Default` set;
library consumers can pass any combination. `script --include-permissions`
clears `IgnorePermissions`.

`Default` = `IgnoreWhitespace | IgnoreComments | IgnoreFillFactor |
IgnorePermissions | IgnoreStatistics` — the toggles Redgate ships on.

| Flag | Effect when set |
|------|-----------------|
| `IgnoreWhitespace` | Ignore whitespace differences in module bodies. |
| `IgnoreComments` | Ignore comment-only differences. |
| `IgnoreCollations` | Ignore column/DB collation differences. |
| `IgnoreFillFactor` | Ignore index fill-factor differences. |
| `IgnoreConstraintNames` | Ignore differences in constraint names. |
| `IgnorePermissions` | Skip GRANT/DENY/REVOKE entirely. |
| `IgnoreUserSettings` | Ignore user-level settings. |
| `CaseSensitiveObjectDefinition` | Compare module bodies case-sensitively. |
| `IgnoreIndexes` | Ignore index differences. |
| `IgnoreKeys` | Ignore primary/unique key differences. |
| `IgnoreStatistics` | Ignore statistics objects. |
| `IgnoreTriggers` | Ignore trigger differences. |
| `IgnoreWithElementOrder` | Ignore ordering of `WITH` elements. |
| `IgnoreFileGroups` | Ignore filegroup placement. |
| `IgnoreIdentitySeed` | Ignore identity seed/increment. |
| `IgnoreUsersPermissionsAndRoleMemberships` | Ignore users, permissions, and role memberships together. |
| `NoTransactions` | Emit the script without the transaction envelope. |
| `ForceColumnOrder` | Treat column ordering as significant. |
| `ThrowOnFileParseFailed` | Fail loudly on an unparseable definition. |
| `DoNotOutputCommentHeader` | Suppress the generated comment header. |
