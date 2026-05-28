# Changelog

All notable changes to **DbDelta** are tracked in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until the v1.0.0 RC is cut, the project is on an `0.x` series where each `M`-milestone bumps the minor.

## [Unreleased]

(Empty — next for v1.0.0 final: code signing, public alpha announcement.)

## [1.0.0-rc1] — 2026-05-28 — first release candidate

First release candidate for v1.0.0. The full v1.0 scope is feature-complete —
milestones M1–M13, the #24 Kahn dependency resolver, the #25 DocFX docs site,
the WiX MSI installer, and the verbose self-contained deploy script are all
shipped (see the 0.x history below). This tag marks the v1.0 feature freeze and
begins release-candidate stabilization; code signing and the public
announcement remain before v1.0.0 final.

### Changed
- CI workflows bumped to Node-24 action majors (`checkout` v6, `setup-dotnet`
  v5, `cache` v5, `upload-artifact` v7, `configure-pages` v6,
  `upload-pages-artifact` v5, `deploy-pages` v5, `action-gh-release` v3) ahead
  of GitHub's 2026-06-02 Node-20 runtime cutover.
- `ScriptGenerator` role-emitter / phase-label cleanup — internal refactor, no
  behavioural change to generated scripts.

### Fixed
- The desktop app's "Salva/Build alignment script" path emitted a malformed
  preamble (a dropped `SET NUMERIC_ROUNDABORT OFF` and an orphaned `GO`) because
  the deploy builder still trimmed the pre-`0.17.0` two-line header. It now
  reuses the generator's full self-contained envelope verbatim. The CLI
  `dbdelta script` path was unaffected.

## [0.17.0] — 2026-05-28 — verbose self-contained deploy script

### Changed
- **Deploy scripts are now self-contained and verbose (Redgate-style).** Every
  operation is preceded by a `PRINT` of the exact phase and followed by an
  `IF @@ERROR <> 0 SET NOEXEC ON` gate, framed by a full `SET` preamble +
  transaction and a final `The database update succeeded`/`failed` verdict that
  rolls back on failure — so a deploy aborts at the first failing step with the
  native error as the reason, instead of running silently. Proven both via
  `dbdelta apply` and standalone (sqlcmd/SSMS). The `NoTransactions` option is
  now honoured. `dbdelta apply` (and the app's execute path) run the
  self-managing script without an outer transaction (`SqlExecutor` gained a
  `useOwnTransaction` flag).

## [0.16.0] — 2026-05-27 — docs site + Windows installer

### Added
- **DocFX documentation site (#25)** — API reference auto-generated from the
  XML docs of the four library projects (`Core`, `Shared`, `Persistence`,
  `Providers.LiveDb`) plus getting-started / CLI / comparison-options guides,
  published to GitHub Pages and auto-deployed on every push to `main`
  (`.github/workflows/docs.yml`). DocFX is pinned as a dotnet local tool.
  Live at <https://gitbakko.github.io/db-delta/>.
- **Windows MSI installer (WiX v5)** — a per-machine MSI installs the DbDelta
  desktop app (Start-Menu shortcut) and the `dbdelta` CLI (added to the system
  `PATH`), self-contained `win-x64`. Built and attached to the GitHub Release on
  `v*` tags (`.github/workflows/release.yml`). The MSI is **unsigned** (no
  code-signing certificate yet), so Windows SmartScreen / UAC shows an "unknown
  publisher" prompt on first run.

## [0.15.0] — 2026-05-27 — #24 dependency resolver

### Added
- **Object-level dependency resolver** (#24, spec M7) — a pure
  `DbDelta.Core/Dependency/` module (`DependencyEdge`, `DependencyResolver`)
  topologically orders deploy-script emission via Kahn's algorithm with a
  deterministic `(kind-rank, schema, name)` tiebreak. `LiveDbSource` populates
  `Database.Dependencies` from `sys.sql_expression_dependencies`.
- **Cross-kind CREATE ordering** — `ScriptGenerator` now emits objects in
  dependency order, fixing scripts that previously failed when a computed
  column referenced a scalar function, a view selected from another view, a
  view referenced a function, or a schemabound TVF referenced a table. With an
  empty edge list the order is byte-identical to the previous
  kind-then-alphabetical output, so existing fixtures are unaffected.
- **Reverse-topological DROP pass** — removed objects are dropped
  dependent-first (e.g. a table is dropped before a sequence it references).
  Foreign-key cycles continue to be handled by the existing final FK phase
  (FK edges are excluded from the topo graph).

### Fixed
- **Module body trailing-semicolon parity** — `BodyNormalizer` strips a single
  trailing `;` so a module whose stored definition lacks the semicolon the
  script generator appends no longer reports a spurious `Different`.

### Tests
- Pure resolver unit tests (Kahn ordering, FK-edge exclusion, deferred-kind
  cycle tolerance, create-validated cycle throw); a live cross-kind round-trip
  integration test (generate → apply on a Testcontainers SQL Server → converge);
  and an FsCheck property asserting valid topological linearization +
  determinism. The `Sequences_Precede_Tables` property was split into the two
  real directional invariants (CREATE sequence-before-table; DROP
  table-before-sequence).

### Known limitations
- DROP ordering uses the source database's dependency edges, so removed
  (target-only) objects are dropped in reverse kind-rank order rather than by
  their target-side dependencies. Safe for standard schemas; supplying
  target-side edges would tighten within-kind drop chains.

## [0.14.0] — 2026-05-27 — M13 wave 2 — parity hardening + quality bar

### Added
- **Column collation diff** (#32, M13-PARITY.5) — `Column.Collation` +
  `Database.DefaultCollation` are now read (`sys.columns.collation_name`
  + `DATABASEPROPERTYEX`) and `TableScriptEmitter` emits a diff-aware
  `COLLATE` clause: skipped when it matches the target DB default,
  defensively explicit when the default is unknown.
- **PK-around-swap identity rebuild + inbound-FK lifecycle** (#33,
  M13-PARITY.6) — `EmitRebuild` drops named non-FK constraints before the
  `_tmp` table; `ScriptGenerator` orchestrates inbound FKs onto rebuilt
  tables via new pipeline sections 0.9 (drop) + 7.9 (re-add), name-deduped
  against the section-7 pair-level FK delta.

### Tests
- **BenchmarkDotNet perf suite** (#19, M13-PERF.1) — `bench/DbDelta.Benchmarks`
  (`ComparisonBench` + `ScriptGenBench`, `SchemaFixtureBuilder`) calibrates
  against spec §6.1. Baseline 2026-05-26: `ComparisonBench.Compare` 10k tables
  = 17.8 ms vs the 3000 ms budget (~170× under).
- **FsCheck property suite** (#20, M13-PERF.2) — `tests/DbDelta.Property.Tests`
  drives FsCheck 3.0 invariants over generated schemas (6 comparison-engine
  + 5 script-generator properties): determinism, ordering, idempotence.
- **Nightly compat matrix** (#21, M13-PERF.3) — `tests/DbDelta.Compat.Tests`
  round-trips read→diff→script→apply against real SQL Server 2017/2019/2022
  images via Testcontainers; self-skips unless `DBDELTA_COMPAT=1` + Docker.
  SQL Server 2016 has no Linux container (SQL-on-Linux began at 2017) so it
  remains a min-compat target exercised on live Windows instances only.
  `ci.yml` gains a `schedule` (cron 03:17 UTC) + scheduled-only
  `nightly-compat-matrix` job.
- 385 / 385 passing across 11 test projects (compat cases skipped by default).

### Documentation
- `docs/parity/redgate-2026-05-25.md` updated with the #31..#33 follow-up
  status table; `tests/Fixtures/Parity/` ships scenario 12 (`dbo.[Order]`
  identity-flip + `dbo.OrderLine` inbound FK).

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
