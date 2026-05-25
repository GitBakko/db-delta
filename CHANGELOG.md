# Changelog

All notable changes to **DbDelta** are tracked in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until the v1.0.0 RC is cut, the project is on an `0.x` series where each `M`-milestone bumps the minor.

## [Unreleased] — heading toward v1.0.0 RC

(Empty — pending v0.14 column-collation diff coverage + identity
rebuild PK swap, BenchmarkDotNet perf suite, FsCheck property tests,
compat matrix, formal Kahn dependency resolver, DocFX site, WiX MSI.)

## [0.13.1] — 2026-05-25 — Redgate parity patch

### Fixed
- **Sequence diff** — when only seed / increment / min / max / cycle /
  cache differ (data type unchanged) `ScriptGenerator` now emits
  `ALTER SEQUENCE [schema].[name] RESTART WITH n …` rather than
  DROP + CREATE. The old DROP path broke any column with
  `DEFAULT NEXT VALUE FOR <seq>` because the dependent default
  constraint was dropped alongside the sequence. DROP + CREATE
  remains the fallback when the base data type actually changes.
  Caught by the 2026-05-25 Redgate SQL Compare parity run
  (`docs/parity/redgate-2026-05-25.md`, scenario 08).

### Tests
- +8 `SequenceAlterTests` cover seed-only, increment-only,
  combined, data-type fallback to DROP+CREATE, cycle toggle,
  cache size change, cache disable, min/max change.
- 355 / 355 passing (+8).

### Documentation
- `docs/parity/redgate-2026-05-25.md` records the full 11-scenario
  parity matrix (6 match + 4 cosmetic + 1 bug, all triaged).
- `tests/Fixtures/Parity/` ships the deterministic 11-scenario
  fixture so anyone can rerun the parity audit against Redgate
  SQL Compare 16.x.

## [0.13.0] — 2026-05-25 — M13 wave 1 alpha (RC candidate)

### Fixed
- **Critical** — `ScriptGenerator` now emits DDL for every M5/M6 object
  kind. Until this release `Sequence`, `Synonym`, alias `UserDefinedType`,
  `User`, `Role`, and `Permission` emitters existed as classes but were
  never called by the pipeline; deployment scripts for any schema using
  those kinds silently shipped without the corresponding DDL. New
  pipeline order: prologue (Sequence → UserDefinedType → TableType →
  User → Role) → body (Table → Index → View → Function → Procedure →
  Trigger) → epilogue (Synonym → FK → Permission, gated on
  `!IgnorePermissions`).
- `TableScriptEmitter` now performs the spec §3.4 temp-table rebuild
  when an existing column's IDENTITY flag flips or its seed / increment
  changes. Previously the path emitted `DROP COLUMN + ADD COLUMN`,
  which silently erased every row's value in the affected column.

### Added
- **CLI `script` verb** — `dbdelta script --source --target [--out path | -]
  [--include-permissions]` runs the comparison and writes a deployment
  script to disk (or stdout). Closes the spec §1.2 CLI surface gap.
- **CLI `apply` verb** — `dbdelta apply --target --script <path> [--dry-run]`
  executes a pre-generated script via the existing `SqlExecutor`
  GO-batched single-transaction runner; `--dry-run` parses the script
  and reports its batch count without touching the target.
- **Table-type UDT (UDTT) — 13th object kind.** New `TableTypeUdt`
  record, comparison engine path, `TableTypeUdtScriptEmitter`
  (CREATE TYPE … AS TABLE / DROP TYPE), and live-DB
  `TableTypeUdtReader` over `sys.table_types`. `KindCatalog.KnownKinds`
  grows from 11 → 12; new Italian display label "Tipi tabella".
- **Hexagonal sharedness** — `Cli/CliErrorMapper` pulled up to centralise
  the `Error → exit code + JSON stderr` mapping shared by `compare`,
  `report`, `script`, and `apply` verbs.

### Refactored
- DRY: `Views/Controls/PasswordBox.axaml` extracted from three call
  sites (`ProjectSetupDialog` Source + Target halves and
  `ConnectionEditDialog`); the six hand-rolled
  `PointerPressed/Released` reveal handlers now live inside the
  control. CLAUDE.md UI rule #3.
- DRY: shared `Styles/Templates.axaml` resource dictionary holds the
  canonical `DiscoveredServerItemTemplate`; the three inline 40-line
  copies in `ProjectSetupDialog` + `ConnectionEditDialog` collapse to
  `<StaticResource ResourceKey="DiscoveredServerItemTemplate" />`. The
  `ConnectionEditDialog` site automatically picks up the section-header
  variant it had been missing.

### Documentation
- `README` refreshed to reflect the Avalonia 11 GUI pivot (away from
  Blazor Hybrid + WebView2). Adds `report` / `script` / `apply` verb
  examples and an architecture table.
- New `docs/superpowers/specs/2026-05-25-avalonia-ui-pivot-addendum.md`
  capturing the rationale for the UI stack change and superseding the
  Blazor / WebView2 rows of the v1 design spec.
- `scripts/SMOKE-RESULTS.md` — sanitised end-to-end smoke run against
  the live PcrmV2Pl_test2 / PcrmV2Pl endpoints (680 objects compared,
  4 TableType UDTTs surfaced, 4 691-line migration script generated).
- `scripts/PUBLISH-SIZES.md` — measured single-file binary sizes
  (framework-dependent 20 MB CLI / 28 MB App vs §6.1 budget), explains
  why self-contained + trim is post-RC work.

### Tests
- 347 / 347 passing across nine projects: Core unit 207
  (+8 TableType, +6 table-rebuild, +24 orphan-kind, +1 KindCatalog
  TableType slot), Shared unit 4, Architecture 3, App Headless 39
  (+4 PasswordBox), ScriptGen Golden 28, Persistence unit 30,
  Persistence integration 6, LiveDb integration 14
  (+1 TableTypeUdtReader), CLI acceptance 16 (+5 script/apply).

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
