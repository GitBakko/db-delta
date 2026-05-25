# Changelog

All notable changes to **DbDelta** are tracked in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until the v1.0.0 RC is cut, the project is on an `0.x` series where each `M`-milestone bumps the minor.

## [Unreleased] — heading toward v1.0.0 RC

### Documentation
- README refreshed to reflect the Avalonia 11 GUI pivot (was Blazor Hybrid + WebView2 in the original spec).
- New `docs/superpowers/specs/2026-05-25-avalonia-ui-pivot-addendum.md` capturing the rationale for the UI stack change.
- `report` CLI verb documented with example invocation.

## [0.12.0] — 2026-05-25 — M12 Reports HTML + JSON

### Added
- `Core/Reports/KindCatalog` — single source of truth for Kind sort order, Italian display labels, and Status visual priority.
- `Core/Reports/HtmlReportGenerator` — self-contained HTML5 report with embedded CSS, collapsible `<details>` sections per Kind, palette-coded rows (cobalt `#0064C8` / crimson `#B31220` / emerald `#007339` / grey `#9097A0`).
- `Shared/Reports/JsonReportGenerator` — pretty-printed camelCase JSON via the existing `Mapper.ToDto` projection.
- `Cli/Commands/ReportCommand` — `dbdelta report --source --target --html=<path> --json=<path>` (at least one of `--html`/`--json` is required).

### Refactored
- DRY: `Cli/CliErrorMapper` pulled up from `CompareCommand` to be reused by `ReportCommand`.
- DRY: `tests/DbDelta.Cli.AcceptanceTests/CliRunner` shared between `compare` and `report` acceptance suites.

### Tests
- 299 passing (up from 233 at end of M8): +66 across Core unit (KindCatalog + HtmlReportGenerator), new `tests/DbDelta.Shared.UnitTests` project for JsonReportGenerator, and four `report` CLI acceptance tests.

## [0.11.1] — 2026-05-23 — M11-bis Connection Management
- DPAPI-backed per-server credential store (`Meziantou.Framework.Win32.CredentialManager`).
- Connection edit dialog with password reveal / copy / paste behaviour.
- Per-band server discovery with Italian date formatting.

## [0.11.0] — M11 Persistence
- `.dbd` XML project file v2 (`XmlProjectStore`), `JsonRecentProjectsStore`, configurable `ProjectsFolder`.
- Save / Load / Edit project verbs in the Avalonia shell; MRU list; per-project selection persistence.

## [0.10.0] — M10 GUI polish (Avalonia)
- 15 rounds of UX iteration on top of the Avalonia shell:
  - Red-Gate-style topbar restructure, `ProjectHeaderStrip`, results-grid `DataGrid`.
  - Dual-pane SQL diff viewer with minimap, synced scroll, section navigation.
  - Crimson `ConfirmExecuteDialog`, busy overlays, in-button compact spinner.
  - DiscoveredServer sectioned picker, IP-in-band badges, auto-scan + clone config.
  - Theme-aware diff lines, dark-mode contrast audit, accordion chevron + single-click toggle.
  - Compact loaders (`LoadingContent` UserControl), invariable UI rules (no naked buttons, 32 px monoline).

### Architecture decisions in this window
- The original Blazor Hybrid + WebView2 GUI was abandoned in favour of Avalonia 11 + Fluent + CommunityToolkit.Mvvm. See [the pivot addendum](docs/superpowers/specs/2026-05-25-avalonia-ui-pivot-addendum.md).

## [0.9.0] — M9 Deployment executor
- `SqlExecutor` — GO-splitting transactional batch runner with `SET XACT_ABORT ON` and rollback on failed batch.
- `DeployScriptBuilder` — alignment script composer that wraps emitted DDL in the spec-§3.1 batch envelope.

## [0.8.0] — M8 Script-gen polish (shipped with M7 in commit `2af2200`)
- `TableScriptEmitter.EmitAlter` complete: drop columns, alter columns, add columns, drop / add constraints.
- `ScriptGenerator` emits index + FK delta for `Different` tables (not just columns).
- Golden tests added for ALTER permutations.

## [0.7.0] — M7 Dependency resolver
- Two-phase ordering: `CREATE TABLE` for all tables first, then FKs batched after. Handles circular cross-table FKs without needing an explicit cycle-break pass for v1.

## [0.6.0] — 2026-05-23 — M6 User + Role + Permission
- `Database.Users / Roles / Permissions` read paths via `LiveDbSource`.
- Comparison engine for users (type code + login + default schema), roles (owner + member set), permissions (Grantee + Action + Target presence/absence).
- `UserScriptEmitter`, `RoleScriptEmitter`, `PermissionScriptEmitter`.

## [0.5.0] — M5 Sequence + Synonym + alias UserDefinedType
- New object kinds with full read / diff / emit support.
- `BodyNormalizer` extended to handle module-less kinds via property comparison.

## [0.4.0] — M4 Functions + Triggers
- Scalar, inline TVF, and multi-TVF `Function` read + diff + emit.
- DML `Trigger` with disabled / not-for-replication state diffing on top of body equality.

## [0.3.0] — M3 Views + Stored Procedures
- `View` + `StoredProcedure` modules with `BodyNormalizer` whitespace / comment handling.
- `ViewScriptEmitter` + `ProcedureScriptEmitter` with `CREATE OR ALTER` semantics.

## [0.2.0] — M2 Constraints + Indexes
- Primary keys, unique constraints, foreign keys, checks, defaults.
- `TableIndex` with key + included columns, filter expression, uniqueness, clustered flag.
- `ComparisonOptions.IgnoreKeys` / `IgnoreIndexes` flags honoured.

## [0.1.0] — M1 Walking skeleton — Table only
- `Database / Schema / Table / Column` object model.
- `TableReader` over `sys.tables` + `sys.columns` (two batched queries).
- `ComparisonEngine` table identity pairing + column diff.
- `TableScriptEmitter` for `CREATE TABLE`, `DROP TABLE`, `ALTER TABLE ADD COLUMN`.
- `dbdelta compare` CLI verb with JSON + text formatters; spec §4.3 exit codes wired.
- `LiveDbSource` `ISchemaSource` adapter over `Microsoft.Data.SqlClient`.

## [0.0.0] — M0 Repo bootstrap
- Solution scaffold (`src/` + `tests/`), Central Package Management, `Directory.Build.props`, `.editorconfig`.
- NetArchTest layering tests gating Core purity.
- GitHub Actions CI on `windows-latest` + .NET 10 SDK.
- Apache 2.0 licence, design system v1 import, M0/M1 implementation plan.
